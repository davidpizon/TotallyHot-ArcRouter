using Microsoft.Extensions.Options;
using System.Diagnostics;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Background worker that continuously drains <see cref="IGraderQueue"/> for every LLM grader, running one
/// sequential consumer per grader lane (<see cref="GraderDispatcher.DispatchableKeys"/>) so a slow backbone
/// for one grader never delays another grader's jobs; concurrency is bounded at one in-flight call per
/// grader. For each
/// dequeued job it looks up the matching <see cref="IPortfolioGraderClient"/> by
/// <see cref="GraderScoringJob.GraderKey"/>, peeks the cached prompt/response, scores, and completes or
/// abandons the aggregator's join. A missing or whitespace-only prompt fails closed (abandon with
/// <c>{grader}-question-missing</c>) rather than scoring the response in isolation. The G-Eval judge additionally persists one row to
/// <c>judge_shadow_scores</c> (G2) before completing the join; that table is not merged with
/// <c>grader_scores</c>.
/// </summary>
public sealed class GraderDrainService : BackgroundService
{
    private readonly IQualityScoreAggregator _aggregator;
    private readonly IReadOnlyDictionary<string, IPortfolioGraderClient> _clientsByKey;
    private readonly IOptionsMonitor<JudgeOptions> _judgeOptions;
    private readonly ILogger<GraderDrainService> _logger;
    private readonly PendingGraderBackboneCache _pendingGraderBackboneCache;
    private readonly PendingPromptCache _pendingPromptCache;
    private readonly PendingResponseTextCache _pendingResponseTextCache;
    private readonly IOptionsMonitor<PortfolioGraderOptions> _portfolioOptions;
    private readonly IGraderQueue _queue;
    private readonly IJudgeShadowScoreStore _shadowStore;

    /// <summary>Initializes a new instance of the <see cref="GraderDrainService"/> class.</summary>
    /// <param name="queue">The work queue to drain.</param>
    /// <param name="pendingResponseTextCache">Supplies the response text for each job, keyed by correlation id.</param>
    /// <param name="pendingPromptCache">
    /// Supplies the originating prompt for each job. Required: a miss (aged out, never cached, or
    /// whitespace-only) abandons that grader's join rather than scoring the response without its question.
    /// </param>
    /// <param name="pendingGraderBackboneCache">Records which backbone each successful score actually used.</param>
    /// <param name="clients">Every registered LLM grader client, indexed by <see cref="IPortfolioGraderClient.GraderKey"/>.</param>
    /// <param name="shadowStore">Where G-Eval judge scores are persisted for G2; unused for portfolio keys.</param>
    /// <param name="judgeOptions">The live judge enabled gate and prompt version, read per job.</param>
    /// <param name="portfolioOptions">The live per-portfolio-grader enabled gates, read per job.</param>
    /// <param name="aggregator">The quality aggregator holding each job's static verdict open.</param>
    /// <param name="logger">The logger.</param>
    public GraderDrainService(
        IGraderQueue queue,
        PendingResponseTextCache pendingResponseTextCache,
        PendingPromptCache pendingPromptCache,
        PendingGraderBackboneCache pendingGraderBackboneCache,
        IEnumerable<IPortfolioGraderClient> clients,
        IJudgeShadowScoreStore shadowStore,
        IOptionsMonitor<JudgeOptions> judgeOptions,
        IOptionsMonitor<PortfolioGraderOptions> portfolioOptions,
        IQualityScoreAggregator aggregator,
        ILogger<GraderDrainService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pendingResponseTextCache);
        ArgumentNullException.ThrowIfNull(pendingPromptCache);
        ArgumentNullException.ThrowIfNull(pendingGraderBackboneCache);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(shadowStore);
        ArgumentNullException.ThrowIfNull(judgeOptions);
        ArgumentNullException.ThrowIfNull(portfolioOptions);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _pendingResponseTextCache = pendingResponseTextCache;
        _pendingPromptCache = pendingPromptCache;
        _pendingGraderBackboneCache = pendingGraderBackboneCache;
        _clientsByKey = clients.ToDictionary(keySelector: c => c.GraderKey, comparer: StringComparer.OrdinalIgnoreCase);
        _shadowStore = shadowStore;
        _judgeOptions = judgeOptions;
        _portfolioOptions = portfolioOptions;
        _aggregator = aggregator;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting grader drain worker.");

        await Task.WhenAll(GraderDispatcher.DispatchableKeys.Select(key => DrainLaneAsync(key, stoppingToken)))
            .ConfigureAwait(false);
    }

    /// <summary>Drains one grader's lane, processing its jobs one at a time until shutdown.</summary>
    /// <param name="graderKey">The grader whose lane to drain.</param>
    /// <param name="stoppingToken">A cancellation token.</param>
    private async Task DrainLaneAsync(string graderKey, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.DequeueAllAsync(graderKey, stoppingToken).ConfigureAwait(false))
                await ProcessAsync(job: job, stoppingToken: stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Scores a single job and joins it, swallowing any failure so one bad job cannot stop the worker.
    /// Internal so a test can exercise one job directly rather than driving the channel loop.
    /// </summary>
    /// <param name="job">The dequeued scoring job.</param>
    /// <param name="stoppingToken">A cancellation token.</param>
    internal async Task ProcessAsync(GraderScoringJob job, CancellationToken stoppingToken)
    {
        var graderKey = job.GraderKey;

        if (!GraderDispatcher.IsEnabled(graderKey, _judgeOptions.CurrentValue, _portfolioOptions.CurrentValue))
        {
            await AbandonAsync(job, FormattableString.Invariant($"{graderKey}-disabled"), stoppingToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_clientsByKey.TryGetValue(graderKey, out var client))
        {
            _logger.LogDebug(message: "No registered client for grader {GraderKey}; abandoning.", graderKey);
            await AbandonAsync(job, FormattableString.Invariant($"{graderKey}-not-registered"), stoppingToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_pendingResponseTextCache.TryPeek(correlationId: job.CorrelationId, text: out var responseText) ||
            string.IsNullOrEmpty(responseText))
        {
            _logger.LogDebug(
                message: "No pending response text for correlation {CorrelationId}; skipping {GraderKey} scoring.",
                job.CorrelationId,
                graderKey);
            await AbandonAsync(job, FormattableString.Invariant($"{graderKey}-text-evicted"), stoppingToken)
                .ConfigureAwait(false);
            return;
        }

        // Fail closed: a recovered response without its question would be graded in isolation, which is
        // exactly the response-only scoring docs/research/code-quality-metrics-assessment.md §1 forbids.
        if (!_pendingPromptCache.TryPeek(correlationId: job.CorrelationId, prompt: out var prompt) ||
            !GraderQuestionText.IsPresent(prompt))
        {
            _logger.LogDebug(
                message:
                "No pending user/task question for correlation {CorrelationId}; skipping {GraderKey} scoring.",
                job.CorrelationId,
                graderKey);
            await AbandonAsync(job, GraderQuestionText.MissingReason(graderKey), stoppingToken)
                .ConfigureAwait(false);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await client
                .ScoreAsync(
                    request: new PortfolioGraderScoreRequest(Dimension: job.Dimension, ResponseText: responseText,
                        Prompt: prompt),
                    cancellationToken: stoppingToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (result is null)
            {
                _logger.LogDebug(
                    message: "No eligible free backbone for {GraderKey} (correlation {CorrelationId}); recorded no score.",
                    graderKey,
                    job.CorrelationId);
                await AbandonAsync(job, FormattableString.Invariant($"{graderKey}-abstained"), stoppingToken)
                    .ConfigureAwait(false);
                return;
            }

            _pendingGraderBackboneCache.Set(correlationId: job.CorrelationId, graderKey: graderKey,
                backboneModel: result.GraderModel);

            if (IsJudge(graderKey))
            {
                // Shadow row first, then the join: the row is the audit trail for a score that is about to
                // influence routing, so it must exist before the score does.
                await _shadowStore.InsertAsync(
                    record: new JudgeShadowScoreRecord(
                        0,
                        CorrelationId: job.CorrelationId,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        Dimension: job.Dimension,
                        Model: job.Model,
                        StaticScore: job.StaticScore,
                        JudgeScore: result.Score,
                        JudgeModel: result.GraderModel,
                        JudgePromptVersion: _judgeOptions.CurrentValue.PromptVersion,
                        JudgeLatencyMs: stopwatch.ElapsedMilliseconds,
                        UsedLogprobs: result.UsedLogprobs,
                        SyntaxAuthoritative: job.SyntaxAuthoritative),
                    cancellationToken: stoppingToken).ConfigureAwait(false);
            }

            await _aggregator.CompleteGraderAsync(correlationId: job.CorrelationId, graderKey: graderKey,
                score: result.Score, cancellationToken: stoppingToken).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(
                    message: "Recorded {GraderKey} score {Score:F3} for correlation {CorrelationId}.",
                    graderKey,
                    result.Score,
                    job.CorrelationId);
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress; drop the in-flight item.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                exception: ex,
                message: "{GraderKey} scoring failed for correlation {CorrelationId}; dropping.",
                graderKey,
                job.CorrelationId);
            await AbandonAsync(job, FormattableString.Invariant($"{graderKey}-failed"), stoppingToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Abandons the aggregator's join for this job's grader key.</summary>
    private Task AbandonAsync(GraderScoringJob job, string reason, CancellationToken stoppingToken)
    {
        return _aggregator.AbandonGraderAsync(correlationId: job.CorrelationId, graderKey: job.GraderKey,
            reason: reason, cancellationToken: stoppingToken);
    }

    /// <summary>Returns whether <paramref name="graderKey"/> is the G-Eval judge.</summary>
    private static bool IsJudge(string graderKey)
    {
        return string.Equals(graderKey, GraderKeys.Judge, StringComparison.OrdinalIgnoreCase);
    }
}
