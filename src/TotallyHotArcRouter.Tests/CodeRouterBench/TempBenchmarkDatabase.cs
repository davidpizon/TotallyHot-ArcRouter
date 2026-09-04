using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// A throwaway <see cref="BenchmarkDatabase"/> backed by a unique temp file, cleaned up on dispose
/// (including SQLite's WAL/SHM sidecar files). Mirrors <c>PriceCatalog.TempDatabase</c>; a separate
/// helper because <see cref="BenchmarkDatabase"/> is its own file, not a wrapper over
/// <see cref="PriceCatalogDatabase"/>.
/// </summary>
internal sealed class TempBenchmarkDatabase : IDisposable
{
    public TempBenchmarkDatabase()
    {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(path1: directory, path2: "coderouterbench.db");
        Database = new BenchmarkDatabase(Options.Create(new StorageOptions { BenchmarkDatabasePath = DatabasePath }));
    }

    public string DatabasePath { get; }

    public BenchmarkDatabase Database { get; }

    public void Dispose()
    {
        // ClearPool (scoped to this test's own connection string), not the process-global ClearAllPools:
        // under xUnit's parallel test execution, ClearAllPools can tear down a pooled native sqlite3
        // handle out from under a completely different test's in-flight query, surfacing as a spurious
        // ObjectDisposedException there. Guarded on the file already existing - a test that never called
        // EnsureCreated() never opened a pooled connection (and its directory may not even exist), so
        // there's nothing to clear.
        if (File.Exists(DatabasePath))
            try
            {
                using var connection = Database.OpenConnection();
                SqliteConnection.ClearPool(connection);
            }
            catch (SqliteException)
            {
                // Best-effort cleanup; a database mid-teardown on a busy CI box is not a test failure.
            }

        var directory = Path.GetDirectoryName(DatabasePath);
        try
        {
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(path: directory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }

    /// <summary>Creates the schema and returns a ledger over it.</summary>
    public BenchmarkFileLedger CreateLedger()
    {
        Database.EnsureCreated();
        return new BenchmarkFileLedger(Database);
    }
}