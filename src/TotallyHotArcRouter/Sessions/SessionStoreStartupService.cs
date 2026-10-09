using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Opens session storage at startup when there is something to recover, which creates the index tables and runs
/// <see cref="SessionStore.RecoverOnStartup"/> (ADR-0019, "Commit order and recovery"): it finishes an
/// interrupted key rotation, cuts every session file back to its committed extent and deletes spools and files
/// no index row names. A router that has never captured anything has no transcript database and no session
/// folder, and opens nothing here, so a fresh install creates no database before its first captured turn; the
/// store then opens, and recovers, the first time the capture writer needs it. Either way recovery finishes
/// before the first append, because the store cannot be reached without running it.
/// </summary>
public sealed class SessionStoreStartupService : IHostedService
{
    private readonly Lazy<SessionStore> _store;
    private readonly TranscriptDatabase _database;
    private readonly ILogger<SessionStoreStartupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStoreStartupService"/> class.
    /// </summary>
    /// <param name="store">The lazily opened store; opening it creates the index and recovers.</param>
    /// <param name="database">The transcript database, whose path says whether anything has been stored.</param>
    /// <param name="logger">Receives a failure to open the store; templates are static.</param>
    public SessionStoreStartupService(
        Lazy<SessionStore> store, TranscriptDatabase database, ILogger<SessionStoreStartupService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_database.DatabasePath) && !Directory.Exists(SessionStore.FolderBeside(_database.DatabasePath)))
        {
            _logger.LogInformation("Session storage was not opened at startup: nothing has been captured yet.");
            return Task.CompletedTask;
        }

        // Capture is best-effort, so a store that cannot be opened must not stop the proxy from starting; each
        // captured turn then fails on its own and is logged.
        try
        {
            _ = _store.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Session storage could not be opened at startup; capture will fail until it is repaired.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
