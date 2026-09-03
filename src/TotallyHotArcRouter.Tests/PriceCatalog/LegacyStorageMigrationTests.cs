using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="LegacyStorageMigration"/>'s two load-bearing properties: it refuses to touch any
/// destination an operator pointed away from the machine-shared default, and its SQLite copy carries WAL
/// content a plain file copy would lose.
/// </summary>
public class LegacyStorageMigrationTests
{
    [Fact]
    public void Run_WithNonDefaultDestinations_DoesNothing()
    {
        // Every path here is a temp directory, i.e. an operator override. The migration must be inert:
        // seeding a deliberately relocated database from an abandoned per-user file would be the opposite
        // of honouring that override. This is also the guard that keeps the whole test suite from touching
        // the developer's real %LOCALAPPDATA% and %ProgramData% files.
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = new StorageOptions
            {
                DatabasePath = Path.Combine(path1: directory, path2: "agent_telemetry.db"),
                BenchmarkDatabasePath = Path.Combine(path1: directory, path2: "coderouterbench.db"),
                TranscriptDatabasePath = Path.Combine(path1: directory, path2: "transcripts.db"),
                LogRegModelPath = Path.Combine(path1: directory, path2: "logreg_voter_model.json"),
                ClusterModelPath = Path.Combine(path1: directory, path2: "cluster_model.json")
            };

            var migrated = LegacyStorageMigration.Run(options: options, logger: NullLogger.Instance);

            Assert.Equal(0, actual: migrated);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(path: directory, true);
        }
    }

    [Fact]
    public void CopyDatabase_CarriesRowsStillSittingInTheWriteAheadLog()
    {
        // The regression this exists for: with journal_mode=WAL, a committed row can live only in the -wal
        // sidecar until a checkpoint folds it into the .db. Copying the .db alone would silently drop it,
        // which for agent_telemetry.db means losing usage-ledger rows on migration.
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var source = Path.Combine(path1: directory, path2: "source.db");
            var destination = Path.Combine(path1: directory, path2: "destination.db");

            using (var connection = new SqliteConnection($"Data Source={source}"))
            {
                connection.Open();

                using var setup = connection.CreateCommand();
                setup.CommandText =
                    """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE usage(id INTEGER PRIMARY KEY, note TEXT NOT NULL);
                    INSERT INTO usage(note) VALUES('uncheckpointed');
                    """;
                setup.ExecuteNonQuery();

                // Deliberately no checkpoint and no dispose before copying: the row is in the -wal file,
                // exactly the state a running router's database is in at any given moment.
                LegacyStorageMigration.CopyDatabase(legacyPath: source, destinationPath: destination);
            }

            using var copied = new SqliteConnection($"Data Source={destination};Mode=ReadOnly");
            copied.Open();

            using var read = copied.CreateCommand();
            read.CommandText = "SELECT note FROM usage;";

            Assert.Equal(expected: "uncheckpointed", actual: read.ExecuteScalar() as string);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(path: directory, true);
        }
    }
}