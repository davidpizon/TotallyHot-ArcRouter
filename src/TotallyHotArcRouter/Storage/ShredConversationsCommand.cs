using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Logging;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Storage;

/// <summary>
/// The router's <c>--shred-conversations</c> command (#184, ADR-0024): what an uninstall runs so the
/// operator's conversation text does not outlive the product. It is one command so the MSI and both
/// <c>uninstall.sh</c> scripts share the same code.
/// </summary>
/// <remarks>
/// <para>
/// This is the interim form. It deletes every row of <c>request_transcripts</c>, truncates the write-ahead
/// log, rebuilds the database file so no freed page keeps old text, and deletes every <c>bodies-*.log</c>
/// file. It keeps the spend and routing databases, which hold no conversation text. Once ADR-0019's
/// per-session files exist, the command also deletes the master-key entry from <c>secrets.dat</c> and
/// removes the session folder; until then there is no key or folder to remove.
/// </para>
/// <para>
/// It runs in its own process, so it points SQLite's temporary folder at the protected data directory before
/// opening any connection: the rebuild's transient copy of the database must not land in a temp folder other
/// accounts can read. It must run as the account that owns the data directory (<c>SYSTEM</c> or an
/// administrator on Windows, the service account on Linux and macOS), because the protection check refuses
/// every other account and a root-run command would leave root-owned files the service cannot read later.
/// </para>
/// </remarks>
internal static class ShredConversationsCommand
{
    /// <summary>The command-line flag that selects this command.</summary>
    internal const string FlagName = "--shred-conversations";

    /// <summary>The exit code for a shred that removed everything it targets, or found nothing to remove.</summary>
    internal const int DoneExitCode = 0;

    /// <summary>The exit code when some conversation text may remain; the uninstall continues regardless.</summary>
    internal const int IncompleteExitCode = 1;

    private static readonly string[] TempVariables = ["TMP", "TEMP", "TMPDIR", "SQLITE_TMPDIR"];

    /// <summary>
    /// Resolves the transcript database and logs directory the way the service does, then shreds them.
    /// </summary>
    /// <param name="logger">Receives what was removed and what could not be.</param>
    /// <returns><see cref="DoneExitCode"/> or <see cref="IncompleteExitCode"/>.</returns>
    internal static int Run(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var databasePath = ResolveStorageOptions().ResolveTranscriptDatabasePath();
            var logsDirectory = AppDataPaths.ResolveLogsDirectory();

            // Before the first SQLite call, so the rebuild's scratch copy lands in the protected directory.
            var tempDirectory = Path.Combine(path1: Path.GetDirectoryName(databasePath) ?? ".", path2: "scrub-temp");
            Directory.CreateDirectory(tempDirectory);
            foreach (var variable in TempVariables) Environment.SetEnvironmentVariable(variable: variable, value: tempDirectory);

            try
            {
                return Shred(databasePath: databasePath, logsDirectory: logsDirectory, logger: logger);
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       Hosting.DataDirectory.DataDirectoryNotProtectedException)
        {
            logger.LogError(exception: ex, message: "Could not shred conversation text: {Reason}", ex.Message);
            return IncompleteExitCode;
        }
    }

    /// <summary>
    /// Removes conversation text from <paramref name="databasePath"/> and <paramref name="logsDirectory"/>.
    /// </summary>
    /// <param name="databasePath">The transcript database.</param>
    /// <param name="logsDirectory">The directory holding <c>bodies-*.log</c>.</param>
    /// <param name="logger">Receives the outcome of each step.</param>
    /// <param name="probeVolume">Overrides the scrub's free-space probe, for tests.</param>
    /// <returns><see cref="DoneExitCode"/> when nothing was left behind, otherwise <see cref="IncompleteExitCode"/>.</returns>
    internal static int Shred(
        string databasePath,
        string logsDirectory,
        ILogger logger,
        Func<string, SqliteScrub.VolumeSpace>? probeVolume = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var complete = ShredDatabase(databasePath: databasePath, logger: logger, probeVolume: probeVolume);

        // A controller that is never written to is just the Clear button's file deleter.
        if (new BodyLogController(logsDirectory: logsDirectory, isEnabled: static () => false).ClearBodyFiles())
            logger.LogInformation("Removed the body-excerpt logs under {LogsDirectory}.", logsDirectory);
        else
            complete = false;

        if (complete)
            logger.LogInformation("Conversation text removed. The spend and routing databases were kept.");
        else
            logger.LogWarning("Some conversation text could not be removed; see the messages above.");
        return complete ? DoneExitCode : IncompleteExitCode;
    }

    /// <summary>Deletes the transcript rows, truncates the log and rebuilds the file.</summary>
    private static bool ShredDatabase(string databasePath, ILogger logger, Func<string, SqliteScrub.VolumeSpace>? probeVolume)
    {
        if (!File.Exists(databasePath)) return true;

        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            using var connection = SqliteHardening.Open(connectionString);
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM request_transcripts;";
                try
                {
                    delete.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.Message.Contains(value: "no such table", comparisonType: StringComparison.OrdinalIgnoreCase))
                {
                    // Capture never ran, so there is no table and no rows.
                }
            }

            if (!SqliteHardening.TruncateWal(connection))
            {
                logger.LogWarning("The write-ahead log of {DatabasePath} was busy; stop the router and run the command again.", databasePath);
                return false;
            }
        }
        catch (SqliteException ex)
        {
            logger.LogError(exception: ex, message: "Could not delete the transcripts in {DatabasePath}.", databasePath);
            return false;
        }

        // Always rebuild, even if the one-time scrub already ran: the rebuild makes the outcome independent
        // of the file's history, and an uninstall can afford the time. Removing the marker forces it.
        var marker = databasePath + ".scrubbed";
        if (File.Exists(marker)) File.Delete(marker);
        return SqliteScrub.Run(databasePath: databasePath, markerPath: marker, logger: logger, probeVolume: probeVolume)
            != SqliteScrub.Outcome.Deferred;
    }

    /// <summary>Binds the <c>Storage</c> section the way the service's configuration would.</summary>
    private static StorageOptions ResolveStorageOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "appsettings.json"), optional: true)
            .AddJsonFile(path: Path.Combine(path1: StorageOptions.ResolveMachineSharedDirectory(), path2: "appsettings.local.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = new StorageOptions();
        configuration.GetSection(StorageOptions.SectionName).Bind(options);
        return options;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(path: directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: SQLite removes its own scratch files on close.
        }
    }
}
