using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Periodically closes out held results whose judge wait has expired, so a judge that never answers costs
/// a delayed score rather than a lost one.
/// </summary>
/// <remarks>
/// A single periodic sweep is used in preference to one timer per held result: the table is bounded at a
/// couple of thousand entries, and a timer apiece would trade a fixed, tiny cost for a variable one that
/// peaks exactly when the system is busiest.
/// </remarks>
public sealed class QualityJoinSweepService : BackgroundService
{
    /// <summary>How often expired joins are swept out when no interval is supplied.</summary>
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(5);

    private readonly IQualityScoreAggregator _aggregator;
    private readonly ILogger<QualityJoinSweepService> _logger;
    private readonly TimeSpan _sweepInterval;

    /// <summary>Initializes a new instance of the <see cref="QualityJoinSweepService"/> class.</summary>
    /// <param name="aggregator">The aggregator whose expired joins to sweep.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="sweepInterval">
    /// How often to sweep; defaults to 5 seconds. Overridable so a test can drive several ticks without
    /// waiting out the production cadence, which would otherwise put it against the repository's
    /// five-second ceiling for a single test.
    /// </param>
    public QualityJoinSweepService(
        IQualityScoreAggregator aggregator,
        ILogger<QualityJoinSweepService> logger,
        TimeSpan? sweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(logger);

        _aggregator = aggregator;
        _logger = logger;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_sweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                try
                {
                    var written = await _aggregator.SweepExpiredAsync(stoppingToken).ConfigureAwait(false);
                    if (written > 0)
                        _logger.LogDebug(message: "Swept {Count} expired judge join(s), writing their static scores.",
                            written);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A sweep failure must not end the loop: the next tick should still get a chance.
                    _logger.LogWarning(exception: ex,
                        message: "Sweeping expired judge joins failed; retrying on the next tick.");
                }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}