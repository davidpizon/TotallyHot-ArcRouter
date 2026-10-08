using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Logging;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Sessions;

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
/// file. It keeps the spend and routing databases, which hold no conversation text. It also deletes
/// ADR-0019's session files and index rows, then destroys the master-key entries in <c>secrets.dat</c>,
/// so a copy of a session file or of the database cannot be decrypted afterwards.
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

        string? resultDirectory = null;
        try
        {
            var databasePath = ResolveStorageOptions().ResolveTranscriptDatabasePath();
            var logsDirectory = AppDataPaths.ResolveLogsDirectory();
            resultDirectory = Path.GetDirectoryName(databasePath);

            // Before the first SQLite call, so the rebuild's scratch copy lands in the protected directory.
            // A fresh folder per run, so the cleanup below can never delete anything this run did not create.
            var tempDirectory = Path.Combine(path1: resultDirectory ?? ".", path2: $"scrub-temp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            foreach (var variable in TempVariables) Environment.SetEnvironmentVariable(variable: variable, value: tempDirectory);

            try
            {
                var exitCode = Shred(databasePath: databasePath, logsDirectory: logsDirectory, logger: logger,
                    masterKeys: new SecretStoreSessionMasterKeyStore(new ProtectedSecretStore()));
                RecordResult(directory: resultDirectory, exitCode: exitCode, detail: $"database={databasePath}; logs={logsDirectory}");
                return exitCode;
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }
        catch (Exception ex)
        {
            // Top-level of a one-shot command: whatever went wrong (bad configuration, unprotected directory,
            // I/O), the uninstall must continue and the operator must be told text may remain.
            logger.LogError(exception: ex, message: "Could not shred conversation text: {Reason}", ex.Message);
            RecordResult(directory: resultDirectory, exitCode: IncompleteExitCode, detail: ex.Message);
            return IncompleteExitCode;
        }
    }

    /// <summary>
    /// Writes the outcome next to the database. An uninstall runs with no console, so the bootstrap logger's
    /// output is lost; this file is the only place the operator can learn that text may remain.
    /// </summary>
    private static void RecordResult(string? directory, int exitCode, string detail)
    {
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            File.WriteAllText(
                path: Path.Combine(path1: directory, path2: "shred-conversations.result"),
                contents: $"{DateTimeOffset.UtcNow:O} exit={exitCode} {(exitCode == DoneExitCode ? "complete" : "INCOMPLETE")}: {detail}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the exit code is still returned.
        }
    }

    /// <summary>
    /// Removes conversation text from <paramref name="databasePath"/> and <paramref name="logsDirectory"/>.
    /// </summary>
    /// <param name="databasePath">The transcript database.</param>
    /// <param name="logsDirectory">The directory holding <c>bodies-*.log</c>.</param>
    /// <param name="logger">Receives the outcome of each step.</param>
    /// <param name="probeVolume">Overrides the scrub's free-space probe, for tests.</param>
    /// <param name="masterKeys">
    /// The session master-key custody to destroy once the session files are gone; <see langword="null"/> skips
    /// that step, for tests that exercise only the files.
    /// </param>
    /// <returns><see cref="DoneExitCode"/> when nothing was left behind, otherwise <see cref="IncompleteExitCode"/>.</returns>
    internal static int Shred(
        string databasePath,
        string logsDirectory,
        ILogger logger,
        Func<string, SqliteScrub.VolumeSpace>? probeVolume = null,
        ISessionMasterKeyStore? masterKeys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        // Files before the key: a key destroyed first would leave unreadable files that a failed delete keeps.
        var complete = ShredSessionFolder(SessionStore.FolderBeside(databasePath), logger);
        complete &= ShredDatabase(databasePath: databasePath, logger: logger, probeVolume: probeVolume);
        complete &= DestroyMasterKeys(masterKeys, logger);

        if (BodyLogController.DeleteBodyFiles(logsDirectory))
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
        if (!File.Exists(databasePath)) return DeleteSidecars(databasePath: databasePath, logger: logger);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        try
        {
            using var connection = SqliteHardening.Open(connectionString);
            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'request_transcripts';";
                // No table means capture never ran, so there are no rows to delete.
                if (exists.ExecuteScalar() is not null)
                {
                    using var delete = connection.CreateCommand();
                    delete.CommandText = "DELETE FROM request_transcripts;";
                    delete.ExecuteNonQuery();
                }
            }

            // The wrapped session keys and turn positions are not text, but with the files and master key
            // gone they are meaningless, and an uninstall should leave no trace of what sessions existed.
            foreach (var table in new[] { "session_turns", "session_files" })
            {
                using var present = connection.CreateCommand();
                present.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
                present.Parameters.AddWithValue("$name", table);
                if (present.ExecuteScalar() is null) continue;

                using var clear = connection.CreateCommand();
                clear.CommandText = $"DELETE FROM {table};";
                clear.ExecuteNonQuery();
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
        if (SqliteScrub.Run(databasePath: databasePath, markerPath: marker, logger: logger, probeVolume: probeVolume)
            == SqliteScrub.Outcome.Deferred)
            return false;

        // The scrub tolerates a busy log because the rebuilt file is already clean, but an uninstall has no
        // later checkpoint to finish the job: old frames of the deleted rows could still sit in the -wal file.
        try
        {
            if (SqliteHardening.TruncateWal(connectionString)) return true;
            logger.LogWarning("The write-ahead log of {DatabasePath} was busy after the rebuild; stop the router and run the command again.", databasePath);
        }
        catch (SqliteException ex)
        {
            logger.LogError(exception: ex, message: "Could not truncate the write-ahead log of {DatabasePath}.", databasePath);
        }

        return false;
    }

    /// <summary>Deletes the session folder and everything in it.</summary>
    /// <returns><see langword="true"/> when no session file remains.</returns>
    private static bool ShredSessionFolder(string sessionsDirectory, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(sessionsDirectory)) return true;

            Directory.Delete(path: sessionsDirectory, recursive: true);
            logger.LogInformation("Removed the session files under {SessionsDirectory}.", sessionsDirectory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception: ex, message: "Could not delete the session files under {SessionsDirectory}.", sessionsDirectory);
            return false;
        }
    }

    /// <summary>Destroys the master-key entries, which makes any surviving copy of a session file undecryptable.</summary>
    /// <returns><see langword="true"/> when the entries are gone or there was no custody to ask.</returns>
    private static bool DestroyMasterKeys(ISessionMasterKeyStore? masterKeys, ILogger logger)
    {
        if (masterKeys is null) return true;

        try
        {
            masterKeys.DestroyAll();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogError(exception: ex, message: "Could not destroy the session master key.");
            return false;
        }
    }

    /// <summary>
    /// Removes the write-ahead log, shared-memory and journal files left beside a missing database. An orphaned
    /// <c>-wal</c> (for example after a crash followed by deletion of the main file) can still hold
    /// conversation text, so "no database" is not by itself "nothing to remove".
    /// </summary>
    /// <returns><see langword="true"/> when no sidecar remains.</returns>
    private static bool DeleteSidecars(string databasePath, ILogger logger)
    {
        var allDeleted = true;
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = databasePath + suffix;
            try
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                allDeleted = false;
                logger.LogError(exception: ex, message: "Could not delete the orphaned file {SidecarPath}.", sidecar);
            }
        }

        return allDeleted;
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
