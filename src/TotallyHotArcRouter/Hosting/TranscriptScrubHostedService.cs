using System.Diagnostics;
using TotallyHot.ArcRouter.Storage;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Runs the one-time transcript-database scrub (#184, ADR-0024) off the startup path and outside the host
/// process. The scrub rebuilds the whole file, which takes minutes on a large database, so it must not run
/// inside <see cref="StartupHealthCheckHostedService.StartAsync"/>; and it needs SQLite's temporary folder
/// pointed at the protected data directory, a process-wide setting that is unsafe to change while the host's
/// other connections are live. So this service starts a child copy of the router
/// (<see cref="ScrubProcessLauncher"/>) and waits for it. A deferred or interrupted scrub is simply retried
/// at the next start, so running late costs nothing.
/// </summary>
/// <remarks>
/// Stopping the host kills the child; SQLite rolls the interrupted rebuild back, so nothing is corrupted and
/// the shutdown does not wait on the <c>VACUUM</c>. The rebuild takes SQLite's write lock for its duration,
/// and the transcript insert is best-effort, so a capture that times out meanwhile is dropped rather than
/// failing a request.
/// </remarks>
public sealed class TranscriptScrubHostedService : BackgroundService
{
    private readonly TranscriptDatabase _database;
    private readonly ILogger<TranscriptScrubHostedService> _logger;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>>? _runProcess;

    /// <summary>Initializes a new instance of the <see cref="TranscriptScrubHostedService"/> class.</summary>
    /// <param name="database">The transcript database to scrub.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="runProcess">
    /// Overrides how the child process is run, so a test need not start a real router; <see langword="null"/>
    /// (the default, and what dependency injection supplies) starts a real child.
    /// </param>
    public TranscriptScrubHostedService(
        TranscriptDatabase database,
        ILogger<TranscriptScrubHostedService> logger,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? runProcess = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _logger = logger;
        _runProcess = runProcess;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first so StartAsync returns immediately whatever the work below does.
        await Task.Yield();

        var (databasePath, markerPath, tempDirectory) = _database.ScrubPaths;
        try
        {
            // The common cases never need a child process: already scrubbed, or no database to scrub.
            if (File.Exists(markerPath)) return;
            if (!File.Exists(databasePath))
            {
                SqliteScrub.WriteMarker(markerPath);
                return;
            }

            await ScrubProcessLauncher.RunAsync(databasePath: databasePath, markerPath: markerPath,
                tempDirectory: tempDirectory, logger: _logger, cancellationToken: stoppingToken,
                runProcess: _runProcess).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the child was killed and SQLite rolled its rebuild back.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(exception: ex, message: "The one-time transcript scrub failed; it will be retried at the next start.");
        }
    }
}
