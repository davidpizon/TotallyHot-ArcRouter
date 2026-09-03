using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Background poll loop that refreshes prices on the catalog's own cadence (D4). It does <em>not</em> run
/// an initial cycle - <see cref="StartupHealthCheckHostedService"/> already ran cycle #1 before the proxy
/// bound its port; this service only handles subsequent refreshes every <c>PollIntervalHours</c>.
/// </summary>
/// <remarks>
/// The interval is measured from the last cycle to <em>complete</em>, whoever ran it, rather than on a fixed
/// cadence of its own: a manual pull from Governance → Price Sources buys a full interval before the next
/// scheduled one, instead of being followed minutes later by a poll that re-fetches what it just fetched.
/// The loop reads <see cref="PriceCatalogIngestionService.ScheduleAnchorUtc"/> each pass rather than holding
/// a <see cref="PeriodicTimer"/>, which by construction cannot be rescheduled by an event outside itself.
/// This is also the fact the panel's countdown renders, so the clock on screen and the poll it predicts can
/// never disagree.
/// </remarks>
public sealed class PriceCatalogIngestionHostedService : IHostedService, IDisposable
{
    private readonly ILogger<PriceCatalogIngestionHostedService> _logger;
    private readonly PriceCatalogIngestionService _ingestionService;
    private readonly TimeSpan _pollInterval;

    private readonly CancellationTokenSource _stopping = new();
    private Task? _pollLoop;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceCatalogIngestionHostedService"/> class.
    /// </summary>
    public PriceCatalogIngestionHostedService(
        ILogger<PriceCatalogIngestionHostedService> logger,
        PriceCatalogIngestionService ingestionService,
        IOptions<PriceCatalogOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ingestionService);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _ingestionService = ingestionService;
        _pollInterval = TimeSpan.FromHours(options.Value.PollIntervalHours);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Price catalog ingestion poll loop starting; interval {IntervalHours}h.", _pollInterval.TotalHours);
        _pollLoop = PollLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_pollLoop is not null)
        {
            // Wait for the loop to unwind, but never past the host's own shutdown deadline.
            await Task.WhenAny(_pollLoop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// Runs the poll loop until cancelled: sleeps until the next cycle is due, re-checks the due time in case
    /// a manual pull re-anchored the schedule while sleeping, then runs an ingestion cycle and repeats.
    /// </summary>
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var untilDue = TimeUntilDue();
                if (untilDue > TimeSpan.Zero)
                {
                    await Task.Delay(untilDue, cancellationToken).ConfigureAwait(false);

                    // Re-check rather than fall through and run. A cycle may have completed while we slept -
                    // a Pull Now, or a toggle - which re-anchored the schedule and pushed the due time out
                    // past the delay we just finished. Looping back re-reads it and waits the remainder;
                    // this continue is precisely what makes a manual pull reset the countdown.
                    continue;
                }

                try
                {
                    await _ingestionService.RunCycleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A cycle should already swallow per-source failures; this guards the unexpected so a
                    // single bad tick never tears down the loop. RunCycleAsync re-anchors the schedule even
                    // when it throws, so the loop cannot spin here - it will wait a full interval next pass.
                    _logger.LogError(ex, "Price catalog ingestion cycle threw unexpectedly; continuing.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Computes the time remaining until the next ingestion cycle is due, based on the ingestion service's
    /// last schedule anchor plus the configured poll interval.
    /// </summary>
    private TimeSpan TimeUntilDue() =>
        _ingestionService.ScheduleAnchorUtc + _pollInterval - DateTimeOffset.UtcNow;
}

