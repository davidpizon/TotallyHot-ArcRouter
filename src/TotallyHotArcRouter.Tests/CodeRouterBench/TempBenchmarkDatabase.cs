using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Extensions.Options;

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
        var directory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        Path_ = Path.Combine(directory, "coderouterbench.db");
        Database = new BenchmarkDatabase(Options.Create(new StorageOptions { BenchmarkDatabasePath = Path_ }));
    }

    public string Path_ { get; }

    public BenchmarkDatabase Database { get; }

    /// <summary>Creates the schema and returns a ledger over it.</summary>
    public BenchmarkFileLedger CreateLedger()
    {
        Database.EnsureCreated();
        return new BenchmarkFileLedger(Database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var directory = System.IO.Path.GetDirectoryName(Path_);
        try
        {
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }
}
