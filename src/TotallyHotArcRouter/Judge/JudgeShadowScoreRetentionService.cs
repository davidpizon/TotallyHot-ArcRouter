using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Background service that enforces configurable age and size bounds on <c>judge_shadow_scores</c>
/// (docs/router/geval-shadow-scoring-plan.md §1d). Runs on a 5-minute check interval, mirroring
/// <see cref="Transcripts.TranscriptRetentionService"/>'s shape exactly. Each cycle is a no-op while
/// <see cref="JudgeOptions.Enabled"/> is <see langword="false"/>, re-read per tick so the System Settings
/// window's judge toggle starts and stops purging without a restart.
/// </summary>
public sealed class JudgeShadowScoreRetentionService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<JudgeShadowScoreRetentionService> _logger;
    private readonly IJudgeShadowScoreStore _store;
    private readonly IOptionsMonitor<JudgeOptions> _options;

    /// <summary>Initializes a new instance of the <see cref="JudgeShadowScoreRetentionService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="store">Supplies row count and deletion operations.</param>
    /// <param name="options">Provides retention configuration (days and max rows).</param>
    public JudgeShadowScoreRetentionService(
        ILogger<JudgeShadowScoreRetentionService> logger,
        IJudgeShadowScoreStore store,
        IOptionsMonitor<JudgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _store = store;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The loop always runs and each cycle re-checks Enabled (see CheckAndPurgeAsync). Exiting here when
        // the judge starts disabled would leave retention dead for the process's lifetime, so enabling the
        // judge from System Settings would accumulate rows with nothing ever purging them until a restart.
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
                    _logger.LogError(ex, "Shadow judge retention check threw unexpectedly; continuing.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one cycle of the retention purge - the loop body <see cref="ExecuteAsync"/> runs on every
    /// tick. Internal (not private) so a test can exercise one cycle directly rather than waiting on
    /// <see cref="CheckInterval"/>, mirroring <see cref="Transcripts.TranscriptRetentionService.CheckAndPurgeAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task CheckAndPurgeAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        var rowCount = await _store.GetRowCountAsync(cancellationToken).ConfigureAwait(false);
        var deletedByOverage = 0;
        var deletedByAge = 0;

        // First, enforce the max-rows bound by deleting oldest-first if over the limit.
        if (rowCount > _options.CurrentValue.MaxRows)
        {
            var overageCount = rowCount - _options.CurrentValue.MaxRows;
            deletedByOverage = await _store.DeleteOldestAsync(overageCount, cancellationToken).ConfigureAwait(false);
        }

        // Then, delete rows past the retention age.
        var cutoffTime = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.CurrentValue.RetentionDays);
        deletedByAge = await _store.DeleteBeforeAsync(cutoffTime, cancellationToken).ConfigureAwait(false);

        if (deletedByOverage > 0 || deletedByAge > 0)
        {
            _logger.LogInformation(
                "Shadow judge retention purge complete: {DeletedByOverage} rows deleted by overage, {DeletedByAge} rows deleted by age.",
                deletedByOverage,
                deletedByAge);
        }
    }
}
