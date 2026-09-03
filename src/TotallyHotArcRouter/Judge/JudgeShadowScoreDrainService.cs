using Microsoft.Extensions.Options;
using System.Diagnostics;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Background worker that continuously drains <see cref="IJudgeShadowScoreQueue"/>
/// (docs/router/geval-shadow-scoring-plan.md §1c), mirroring
/// <see cref="Quality.Grading.QualityGradingService"/>'s <c>await foreach</c> shape rather than
/// <c>TranscriptRetentionService</c>'s polling-timer shape - this drains a queue continuously, it does not
/// poll on an interval. For each dequeued job it <see cref="PendingResponseTextCache.TryTake"/>s the
/// response text, calls the configured <see cref="IJudgeClient"/>, and writes one row to
/// <see cref="IJudgeShadowScoreStore"/>. Whether judging succeeds, fails, or the cache entry already aged
/// out, the text is gone from <see cref="PendingResponseTextCache"/> afterward - <c>TryTake</c> always
/// consumes the slot. Each job is a no-op while <see cref="JudgeOptions.Enabled"/> is
/// <see langword="false"/>: the worker keeps running and re-reads the flag per job rather than exiting at
/// startup, so the System Settings window's toggle resumes judging without a restart.
/// </summary>
public sealed class JudgeShadowScoreDrainService : BackgroundService
{
    private readonly IJudgeShadowScoreQueue _queue;
    private readonly PendingResponseTextCache _pendingResponseTextCache;
    private readonly IJudgeClient _judgeClient;
    private readonly IJudgeShadowScoreStore _store;
    private readonly IQualityScoreAggregator _aggregator;
    private readonly IOptionsMonitor<JudgeOptions> _options;
    private readonly ILogger<JudgeShadowScoreDrainService> _logger;

    /// <summary>Initializes a new instance of the <see cref="JudgeShadowScoreDrainService"/> class.</summary>
    /// <param name="queue">The work queue to drain.</param>
    /// <param name="pendingResponseTextCache">Supplies the response text for each job, keyed by correlation id.</param>
    /// <param name="judgeClient">The judge backbone client.</param>
    /// <param name="store">Where each scored job's row is persisted.</param>
    /// <param name="options">The judge options (live enabled gate, prompt version), read per job rather than captured.</param>
    /// <param name="aggregator">The quality aggregator holding this job's static verdict open for the judge's grade.</param>
    /// <param name="logger">The logger.</param>
    public JudgeShadowScoreDrainService(
        IJudgeShadowScoreQueue queue,
        PendingResponseTextCache pendingResponseTextCache,
        IJudgeClient judgeClient,
        IJudgeShadowScoreStore store,
        IOptionsMonitor<JudgeOptions> options,
        IQualityScoreAggregator aggregator,
        ILogger<JudgeShadowScoreDrainService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pendingResponseTextCache);
        ArgumentNullException.ThrowIfNull(judgeClient);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _pendingResponseTextCache = pendingResponseTextCache;
        _judgeClient = judgeClient;
        _store = store;
        _options = options;
        _aggregator = aggregator;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting shadow-judge drain worker.");

        try
        {
            await foreach (var job in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Scores a single job and records it, swallowing any failure so one bad job cannot stop the worker.
    /// The pending response text is always consumed (<see cref="PendingResponseTextCache.TryTake"/> runs
    /// unconditionally) regardless of whether scoring subsequently succeeds. Internal (not private) so a
    /// test can exercise one job directly rather than driving it through <see cref="ExecuteAsync"/>'s
    /// channel loop, mirroring <see cref="Transcripts.TranscriptRetentionService.CheckAndPurgeAsync"/>'s
    /// "internal for direct test access" convention.
    /// </summary>
    internal async Task ProcessAsync(JudgeShadowScoringJob job, CancellationToken stoppingToken)
    {
        // Re-checked per job, not once at startup: the toggle is live, and a job enqueued moments before it
        // was switched off must not still reach the backbone. The cache slot is consumed below either way,
        // so a disabled judge still releases the retained response text rather than leaving it to age out.
        if (!_options.CurrentValue.Enabled)
        {
            _pendingResponseTextCache.TryTake(job.CorrelationId, out _);
            await _aggregator.AbandonJudgeAsync(job.CorrelationId, "judge-disabled", stoppingToken).ConfigureAwait(false);
            return;
        }

        if (!_pendingResponseTextCache.TryTake(job.CorrelationId, out var responseText) || string.IsNullOrEmpty(responseText))
        {
            _logger.LogDebug(
                "No pending response text for correlation {CorrelationId}; skipping shadow-judge scoring.",
                job.CorrelationId);
            await _aggregator.AbandonJudgeAsync(job.CorrelationId, "judge-text-evicted", stoppingToken).ConfigureAwait(false);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _judgeClient.ScoreAsync(new JudgeScoreRequest(job.Dimension, responseText), stoppingToken).ConfigureAwait(false);
            stopwatch.Stop();

            // An abstention, not a failure: no free model is currently eligible to judge. Recording nothing
            // is the honest outcome - a shadow row exists to be compared against the Verifier, and one with
            // a made-up score would corrupt exactly the analysis the table is for.
            if (result is null)
            {
                _logger.LogDebug(
                    "No eligible free judge model for correlation {CorrelationId}; recorded no shadow score.",
                    job.CorrelationId);
                await _aggregator.AbandonJudgeAsync(job.CorrelationId, "judge-abstained", stoppingToken).ConfigureAwait(false);
                return;
            }

            await _store.InsertAsync(
                new JudgeShadowScoreRecord(
                    Id: 0,
                    CorrelationId: job.CorrelationId,
                    CreatedAtUtc: DateTimeOffset.UtcNow,
                    Dimension: job.Dimension,
                    Model: job.Model,
                    StaticScore: job.StaticScore,
                    JudgeScore: result.Score,
                    JudgeModel: result.JudgeModel,
                    JudgePromptVersion: _options.CurrentValue.PromptVersion,
                    JudgeLatencyMs: stopwatch.ElapsedMilliseconds,
                    UsedLogprobs: result.UsedLogprobs),
                stoppingToken).ConfigureAwait(false);

            // The shadow row is written first, then the join is completed. Order matters: the row is the
            // audit trail for a score that is about to influence routing, so it must exist before the score
            // does - never the other way round, which would leave a routed-on grade with no record of where
            // it came from if the insert then failed.
            await _aggregator.CompleteWithJudgeAsync(job.CorrelationId, result.Score, stoppingToken).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Recorded shadow judge score {JudgeScore:F3} for model {Model} (correlation {CorrelationId}); static score was {StaticScore:F3}.",
                    result.Score,
                    job.Model,
                    job.CorrelationId,
                    job.StaticScore);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress; drop the in-flight item.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Shadow-judge scoring failed for correlation {CorrelationId}; dropping.",
                job.CorrelationId);
        }
    }
}
