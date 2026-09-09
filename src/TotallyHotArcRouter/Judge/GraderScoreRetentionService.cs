using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Background service that enforces configurable age and size bounds on <c>grader_scores</c>
/// (docs/router/grader-reliability-plan.md, Phase Q4). Runs on a 5-minute check interval, mirroring
/// <see cref="JudgeShadowScoreRetentionService"/>'s shape exactly - except with no enabled gate: unlike
/// <c>judge_shadow_scores</c>, which only fills while the judge is switched on,
/// <c>grader_scores</c> gains a row from <see cref="GraderKeys.Analysis"/> on every graded request
/// regardless of which (if any) LLM grader is live, so this purge must always run.
/// </summary>
public sealed class GraderScoreRetentionService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<GraderScoreRetentionService> _logger;
    private readonly IOptionsMonitor<JudgeOptions> _options;
    private readonly IGraderScoreStore _store;

    /// <summary>Initializes a new instance of the <see cref="GraderScoreRetentionService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="store">Supplies row count and deletion operations.</param>
    /// <param name="options">Provides retention configuration (days and max rows).</param>
    public GraderScoreRetentionService(
        ILogger<GraderScoreRetentionService> logger,
        IGraderScoreStore store,
        IOptionsMonitor<JudgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _store = store;
        _options = options;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                try
                {
                    await CheckAndPurgeAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(exception: ex,
                        message: "Grader-score retention check threw unexpectedly; continuing.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one cycle of the retention purge - the loop body <see cref="ExecuteAsync"/> runs on every
    /// tick. Internal (not private) so a test can exercise one cycle directly rather than waiting on
    /// <see cref="CheckInterval"/>, mirroring <see cref="JudgeShadowScoreRetentionService.CheckAndPurgeAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task CheckAndPurgeAsync(CancellationToken cancellationToken)
    {
        var rowCount = await _store.GetRowCountAsync(cancellationToken).ConfigureAwait(false);
        var deletedByOverage = 0;

        if (rowCount > _options.CurrentValue.GraderScoreMaxRows)
        {
            var overageCount = rowCount - _options.CurrentValue.GraderScoreMaxRows;
            deletedByOverage = await _store.DeleteOldestAsync(count: overageCount, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var cutoffTime = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.CurrentValue.GraderScoreRetentionDays);
        var deletedByAge = await _store.DeleteBeforeAsync(cutoff: cutoffTime, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (deletedByOverage > 0 || deletedByAge > 0)
            _logger.LogInformation(
                message:
                "Grader-score retention purge complete: {DeletedByOverage} rows deleted by overage, {DeletedByAge} rows deleted by age.",
                deletedByOverage,
                deletedByAge);
    }
}
