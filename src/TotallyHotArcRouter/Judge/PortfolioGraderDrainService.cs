using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Background worker that continuously drains <see cref="IPortfolioGraderQueue"/>, mirroring
/// <see cref="JudgeShadowScoreDrainService"/>'s shape for Phase Q3's CodeJudge/ICE-Score/RACE portfolio. For
/// each dequeued job it reads the response text (and, best-effort, the originating prompt) via
/// <see cref="PendingResponseTextCache.TryPeek"/>/<see cref="PendingPromptCache.TryPeek"/> - never
/// <c>TryTake</c>, since the G-Eval judge's own drain worker (or another portfolio grader's job for the same
/// request) may still need the same cached entry - calls the matching <see cref="IPortfolioGraderClient"/>,
/// and completes or abandons the aggregator's join for that grader's key.
/// </summary>
public sealed class PortfolioGraderDrainService : BackgroundService
{
    private readonly IQualityScoreAggregator _aggregator;
    private readonly IReadOnlyDictionary<string, IPortfolioGraderClient> _clientsByKey;
    private readonly ILogger<PortfolioGraderDrainService> _logger;
    private readonly IOptionsMonitor<PortfolioGraderOptions> _options;
    private readonly PendingPromptCache _pendingPromptCache;
    private readonly PendingResponseTextCache _pendingResponseTextCache;
    private readonly IPortfolioGraderQueue _queue;

    /// <summary>Initializes a new instance of the <see cref="PortfolioGraderDrainService"/> class.</summary>
    /// <param name="queue">The work queue to drain.</param>
    /// <param name="pendingResponseTextCache">Supplies the response text for each job, keyed by correlation id.</param>
    /// <param name="pendingPromptCache">Supplies the originating prompt for each job, best-effort.</param>
    /// <param name="clients">Every registered portfolio grader client, indexed by <see cref="IPortfolioGraderClient.GraderKey"/>.</param>
    /// <param name="options">The live per-grader enabled gates, read per job rather than captured.</param>
    /// <param name="aggregator">The quality aggregator holding each job's static verdict open for this grader's score.</param>
    /// <param name="logger">The logger.</param>
    public PortfolioGraderDrainService(
        IPortfolioGraderQueue queue,
        PendingResponseTextCache pendingResponseTextCache,
        PendingPromptCache pendingPromptCache,
        IEnumerable<IPortfolioGraderClient> clients,
        IOptionsMonitor<PortfolioGraderOptions> options,
        IQualityScoreAggregator aggregator,
        ILogger<PortfolioGraderDrainService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pendingResponseTextCache);
        ArgumentNullException.ThrowIfNull(pendingPromptCache);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _pendingResponseTextCache = pendingResponseTextCache;
        _pendingPromptCache = pendingPromptCache;
        _clientsByKey = clients.ToDictionary(keySelector: c => c.GraderKey, comparer: StringComparer.OrdinalIgnoreCase);
        _options = options;
        _aggregator = aggregator;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting portfolio-grader drain worker.");

        try
        {
            await foreach (var job in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
                await ProcessAsync(job: job, stoppingToken: stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Scores a single job and joins it, swallowing any failure so one bad job cannot stop the worker.
    /// Internal so a test can exercise one job directly, mirroring
    /// <see cref="JudgeShadowScoreDrainService.ProcessAsync"/>'s convention.
    /// </summary>
    internal async Task ProcessAsync(PortfolioGraderJob job, CancellationToken stoppingToken)
    {
        if (!IsEnabled(job.GraderKey))
        {
            await _aggregator.AbandonGraderAsync(correlationId: job.CorrelationId, graderKey: job.GraderKey,
                reason: FormattableString.Invariant($"{job.GraderKey}-disabled"),
                cancellationToken: stoppingToken).ConfigureAwait(false);
            return;
        }

        if (!_clientsByKey.TryGetValue(job.GraderKey, out var client))
        {
            _logger.LogDebug(message: "No registered client for portfolio grader {GraderKey}; abandoning.",
                job.GraderKey);
            await _aggregator.AbandonGraderAsync(correlationId: job.CorrelationId, graderKey: job.GraderKey,
                reason: FormattableString.Invariant($"{job.GraderKey}-not-registered"),
                cancellationToken: stoppingToken).ConfigureAwait(false);
            return;
        }

        if (!_pendingResponseTextCache.TryPeek(correlationId: job.CorrelationId, text: out var responseText) ||
            string.IsNullOrEmpty(responseText))
        {
            _logger.LogDebug(
                message: "No pending response text for correlation {CorrelationId}; skipping {GraderKey} scoring.",
                job.CorrelationId,
                job.GraderKey);
            await _aggregator.AbandonGraderAsync(correlationId: job.CorrelationId, graderKey: job.GraderKey,
                reason: FormattableString.Invariant($"{job.GraderKey}-text-evicted"),
                cancellationToken: stoppingToken).ConfigureAwait(false);
            return;
        }

        _pendingPromptCache.TryPeek(correlationId: job.CorrelationId, prompt: out var prompt);

        try
        {
            var score = await client
                .ScoreAsync(
                    request: new PortfolioGraderScoreRequest(Dimension: job.Dimension, ResponseText: responseText,
                        Prompt: prompt ?? string.Empty),
                    cancellationToken: stoppingToken).ConfigureAwait(false);

            if (score is null)
            {
                _logger.LogDebug(
                    message: "No eligible free backbone for {GraderKey} (correlation {CorrelationId}); recorded no score.",
                    job.GraderKey,
                    job.CorrelationId);
                await _aggregator.AbandonGraderAsync(correlationId: job.CorrelationId, graderKey: job.GraderKey,
                    reason: FormattableString.Invariant($"{job.GraderKey}-abstained"),
                    cancellationToken: stoppingToken).ConfigureAwait(false);
                return;
            }

            await _aggregator.CompleteGraderAsync(correlationId: job.CorrelationId, graderKey: job.GraderKey,
                score: score.Value, cancellationToken: stoppingToken).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(
                    message: "Recorded {GraderKey} score {Score:F3} for correlation {CorrelationId}.",
                    job.GraderKey,
                    score.Value,
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
                job.GraderKey,
                job.CorrelationId);
            await _aggregator.AbandonGraderAsync(correlationId: job.CorrelationId, graderKey: job.GraderKey,
                reason: FormattableString.Invariant($"{job.GraderKey}-failed"),
                cancellationToken: stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Checks whether the given grader key is currently enabled, re-read live rather than captured.</summary>
    private bool IsEnabled(string graderKey)
    {
        var current = _options.CurrentValue;
        if (string.Equals(graderKey, GraderKeys.CodeJudge, StringComparison.OrdinalIgnoreCase)) return current.CodeJudgeEnabled;
        if (string.Equals(graderKey, GraderKeys.IceScore, StringComparison.OrdinalIgnoreCase)) return current.IceScoreEnabled;
        if (string.Equals(graderKey, GraderKeys.Race, StringComparison.OrdinalIgnoreCase)) return current.RaceEnabled;
        return false;
    }
}
