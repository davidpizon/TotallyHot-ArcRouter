using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The age/size purge operations shared by <c>judge_shadow_scores</c> and <c>grader_scores</c>. The
/// tables stay separate (different G2 columns); only this retention loop is shared.
/// </summary>
public interface IScoreRetentionStore
{
    /// <summary>Returns the total number of rows in the implementing table.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> GetRowCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes the oldest <paramref name="count"/> rows, enforcing the table's max-rows bound.</summary>
    /// <param name="count">The number of oldest rows to delete.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all rows where <c>created_at_utc &lt; <paramref name="cutoff"/></c>, enforcing the table's
    /// retention-days bound.
    /// </summary>
    /// <param name="cutoff">The exclusive UTC timestamp cutoff; rows older than this are deleted.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

/// <summary>
/// Background service that enforces configurable age and size bounds on one score table. Two instances
/// are registered: one for <c>judge_shadow_scores</c> (gated on <see cref="JudgeOptions.Enabled"/>) and
/// one for <c>grader_scores</c> (always on, because analysis rows arrive regardless of LLM graders).
/// </summary>
public sealed class ScoreTableRetentionService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly Func<JudgeOptions, bool> _isEnabled;
    private readonly ILogger<ScoreTableRetentionService> _logger;
    private readonly Func<JudgeOptions, int> _maxRows;
    private readonly IOptionsMonitor<JudgeOptions> _options;
    private readonly Func<JudgeOptions, int> _retentionDays;
    private readonly IScoreRetentionStore _store;
    private readonly string _tableLabel;

    /// <summary>Initializes a new instance of the <see cref="ScoreTableRetentionService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="store">Supplies row count and deletion operations for one SQLite table.</param>
    /// <param name="options">Provides the live <see cref="JudgeOptions"/> the bound delegates read.</param>
    /// <param name="isEnabled">Returns whether this cycle should run; false makes the tick a no-op.</param>
    /// <param name="maxRows">Returns the max-rows bound for this table.</param>
    /// <param name="retentionDays">Returns the retention-days bound for this table.</param>
    /// <param name="tableLabel">Human-readable table name used in log messages, e.g. <c>Shadow judge</c>.</param>
    public ScoreTableRetentionService(
        ILogger<ScoreTableRetentionService> logger,
        IScoreRetentionStore store,
        IOptionsMonitor<JudgeOptions> options,
        Func<JudgeOptions, bool> isEnabled,
        Func<JudgeOptions, int> maxRows,
        Func<JudgeOptions, int> retentionDays,
        string tableLabel)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(maxRows);
        ArgumentNullException.ThrowIfNull(retentionDays);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableLabel);

        _logger = logger;
        _store = store;
        _options = options;
        _isEnabled = isEnabled;
        _maxRows = maxRows;
        _retentionDays = retentionDays;
        _tableLabel = tableLabel;
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
                        message: "{Table} retention check threw unexpectedly; continuing.",
                        _tableLabel);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one cycle of the retention purge. Internal so a test can exercise one cycle directly rather
    /// than waiting on the five-minute interval.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task CheckAndPurgeAsync(CancellationToken cancellationToken)
    {
        var current = _options.CurrentValue;
        if (!_isEnabled(current)) return;

        var rowCount = await _store.GetRowCountAsync(cancellationToken).ConfigureAwait(false);
        var deletedByOverage = 0;
        var maxRows = _maxRows(current);

        if (rowCount > maxRows)
        {
            var overageCount = rowCount - maxRows;
            deletedByOverage = await _store.DeleteOldestAsync(count: overageCount, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var cutoffTime = DateTimeOffset.UtcNow - TimeSpan.FromDays(_retentionDays(current));
        var deletedByAge = await _store.DeleteBeforeAsync(cutoff: cutoffTime, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (deletedByOverage > 0 || deletedByAge > 0)
            _logger.LogInformation(
                message:
                "{Table} retention purge complete: {DeletedByOverage} rows deleted by overage, {DeletedByAge} rows deleted by age.",
                _tableLabel,
                deletedByOverage,
                deletedByAge);
    }
}
