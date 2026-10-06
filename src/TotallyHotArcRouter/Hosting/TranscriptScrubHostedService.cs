using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Runs the one-time transcript-database scrub (#184, ADR-0024) off the startup path. The scrub rebuilds
/// the whole file, which takes minutes on a large database; doing that inside
/// <see cref="StartupHealthCheckHostedService.StartAsync"/> would hold the proxy back from binding its port
/// and could trip a service-start timeout. A deferred or interrupted scrub is simply retried at the next
/// start, so running late costs nothing.
/// </summary>
/// <remarks>
/// The rebuild cannot be cancelled once it starts, so stopping the host mid-scrub waits for it, up to the
/// host's shutdown timeout. SQLite rolls an interrupted rebuild back, so nothing is corrupted; that run's
/// work is lost and the scrub is retried at the next start. Accepted over adding cancellation to
/// <c>VACUUM</c>, which SQLite does not support.
/// </remarks>
public sealed class TranscriptScrubHostedService : BackgroundService
{
    private readonly TranscriptDatabase _database;
    private readonly ILogger<TranscriptScrubHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="TranscriptScrubHostedService"/> class.</summary>
    /// <param name="database">The transcript database to scrub.</param>
    /// <param name="logger">The logger.</param>
    public TranscriptScrubHostedService(TranscriptDatabase database, ILogger<TranscriptScrubHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Task.Run so the synchronous VACUUM occupies a pool thread and StartAsync returns immediately.
        return Task.Run(() =>
        {
            try
            {
                _database.RunOneTimeScrub(_logger);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(exception: ex, message: "The one-time transcript scrub failed; it will be retried at the next start.");
            }
        }, stoppingToken);
    }
}
