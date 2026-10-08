namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Creates the session index tables and runs <see cref="SessionStore.RecoverOnStartup"/> before anything is
/// captured: it finishes an interrupted key rotation, cuts every session file back to its committed extent and
/// deletes spools and files no index row names (ADR-0019, "Commit order and recovery"). Registered ahead of
/// <see cref="SessionCaptureWriter"/> and the proxy so recovery finishes before the first append.
/// </summary>
public sealed class SessionStoreStartupService : IHostedService
{
    private readonly SessionIndex _index;
    private readonly SessionStore _store;
    private readonly ILogger<SessionStoreStartupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStoreStartupService"/> class.
    /// </summary>
    /// <param name="index">The index whose tables are created.</param>
    /// <param name="store">The store to recover.</param>
    /// <param name="logger">Receives the recovery outcome; templates are static.</param>
    public SessionStoreStartupService(SessionIndex index, SessionStore store, ILogger<SessionStoreStartupService> logger)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _index = index;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Capture is best-effort, so a store that cannot be recovered must not stop the proxy from starting;
        // each append fails on its own and is logged.
        try
        {
            _index.EnsureCreated();
            var result = _store.RecoverOnStartup();
            _logger.LogInformation(
                "Session storage recovered: {Truncated} files cut back, {Orphans} orphans deleted, {Dropped} rows without a file removed, {Corrupt} corrupt files left, rotation {Rotation}.",
                result.TruncatedFiles, result.DeletedOrphanFiles, result.DroppedMissingFiles, result.CorruptFiles,
                result.RotationOutcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Session storage could not be recovered at startup; capture may fail until it is repaired.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
