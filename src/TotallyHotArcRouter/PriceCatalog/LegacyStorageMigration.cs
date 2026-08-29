using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One-time adoption of the per-user files <see cref="StorageOptions"/>' defaults used before they moved
/// to the machine-wide <c>%ProgramData%\TotallyHotArcRouter\</c> directory. Runs at startup, ahead of the
/// first <c>EnsureCreated</c>, so an existing install keeps its usage ledger, provider spend, synced
/// benchmark corpus, and trained voter models instead of silently starting from empty.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately migrate-if-absent, never merge. Two populations of these files can exist at once - one
/// the installed <c>LocalSystem</c> service wrote under the system profile, one a developer produced by
/// running the router directly - and their <c>usage_ledger</c> rows describe the same traffic recorded
/// twice. Combining them would double-count spend, so a destination that already exists is left strictly
/// alone and the legacy file is not touched.
/// </para>
/// <para>
/// Note that <c>LocalSystem</c> cannot see an interactive user's <c>%LOCALAPPDATA%</c>, so the installed
/// service only ever adopts the copy under its own system profile. That is the migration that matters;
/// a developer's per-user database has to be moved by hand (see
/// <c>docs/router/packaging-and-distribution.md</c>).
/// </para>
/// <para>
/// Every failure is logged and swallowed. All five files are either re-derivable (the price catalog
/// re-polls, the benchmark corpus re-downloads, both voter models retrain) or non-essential to serving
/// traffic, so a migration that cannot complete must degrade to "start fresh" rather than abort startup.
/// </para>
/// </remarks>
public static class LegacyStorageMigration
{
    /// <summary>The suffix a successfully adopted legacy file is renamed with, so it is never adopted twice.</summary>
    private const string MigratedSuffix = ".migrated";

    /// <summary>
    /// Adopts any legacy copy of the five <see cref="StorageOptions"/> files whose destination does not
    /// exist yet. Idempotent: a second run finds every destination present and does nothing.
    /// </summary>
    /// <param name="options">The resolved storage locations to migrate into.</param>
    /// <param name="logger">Receives one Information line per adopted file and a Warning per failure.</param>
    /// <returns>The number of files adopted.</returns>
    public static int Run(StorageOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var migrated = 0;

        // isSqlite distinguishes the two copy strategies below - it is not a guess about the extension.
        migrated += Migrate(options.ResolveDatabasePath(), "agent_telemetry.db", isSqlite: true, logger);
        migrated += Migrate(options.ResolveBenchmarkDatabasePath(), "coderouterbench.db", isSqlite: true, logger);
        migrated += Migrate(options.ResolveTranscriptDatabasePath(), "transcripts.db", isSqlite: true, logger);
        migrated += Migrate(options.ResolveLogRegModelPath(), "logreg_voter_model.json", isSqlite: false, logger);
        migrated += Migrate(options.ResolveClusterModelPath(), "cluster_model.json", isSqlite: false, logger);

        return migrated;
    }

    /// <summary>
    /// Adopts the first legacy copy of <paramref name="fileName"/> found, into
    /// <paramref name="destinationPath"/>.
    /// </summary>
    /// <returns>1 if a file was adopted, 0 if there was nothing to do or the attempt failed.</returns>
    private static int Migrate(string destinationPath, string fileName, bool isSqlite, ILogger logger)
    {
        // Only ever migrate into the default location. An operator who pointed a path somewhere else made
        // a deliberate choice about where their data lives, and silently seeding that location from a
        // per-user file they may have abandoned years ago would be the opposite of honouring it. This is
        // also what keeps the migration inert in tests, which point StorageOptions at temp directories.
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.Equals(
                destinationDirectory?.TrimEnd('/', '\\'),
                StorageOptions.ResolveMachineSharedDirectory().TrimEnd('/', '\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (File.Exists(destinationPath))
        {
            return 0;
        }

        foreach (var legacyDirectory in StorageOptions.ResolveLegacyDirectories())
        {
            var legacyPath = Path.Combine(legacyDirectory, fileName);
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(destinationDirectory!);

                if (isSqlite)
                {
                    CopyDatabase(legacyPath, destinationPath);
                }
                else
                {
                    File.Copy(legacyPath, destinationPath);
                }

                // Rename rather than delete: the operator keeps a recoverable copy, and the suffix stops a
                // later run (e.g. after someone deletes the new file) from silently adopting it again.
                File.Move(legacyPath, legacyPath + MigratedSuffix, overwrite: true);

                logger.LogInformation(
                    "Migrated {FileName} from the legacy per-user location {LegacyPath} to {DestinationPath}.",
                    fileName,
                    legacyPath,
                    destinationPath);

                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                // Leave the legacy file exactly as it was: unrenamed, so a later run (with the permissions
                // it lacked here) can still pick it up.
                logger.LogWarning(
                    ex,
                    "Could not migrate {FileName} from {LegacyPath} to {DestinationPath}; continuing with a new file.",
                    fileName,
                    legacyPath,
                    destinationPath);

                TryDeletePartialDestination(destinationPath, logger);
                return 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Copies a SQLite database via <c>VACUUM INTO</c> rather than <see cref="File.Copy(string, string)"/>.
    /// </summary>
    /// <remarks>
    /// Every database here runs in WAL mode (<c>PRAGMA journal_mode=WAL</c>), so its committed state is
    /// spread across three files - <c>.db</c>, <c>-wal</c>, and <c>-shm</c> - and copying only the first
    /// silently drops whatever has not been checkpointed yet. <c>VACUUM INTO</c> asks SQLite itself for a
    /// single consistent, already-compacted file at the destination, which is exactly the one-file result
    /// this needs.
    /// </remarks>
    internal static void CopyDatabase(string legacyPath, string destinationPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = legacyPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM INTO $destination;";
        vacuum.Parameters.AddWithValue("$destination", destinationPath);
        vacuum.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes a destination file a failed <c>VACUUM INTO</c> or copy may have left half-written, so the
    /// caller's "start fresh" fallback opens a clean file rather than a truncated one.
    /// </summary>
    private static void TryDeletePartialDestination(string destinationPath, ILogger logger)
    {
        try
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the partially migrated file at {DestinationPath}.", destinationPath);
        }
    }
}
