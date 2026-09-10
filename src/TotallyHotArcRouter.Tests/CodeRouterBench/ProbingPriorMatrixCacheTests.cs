using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Covers <see cref="ProbingPriorMatrixCache"/>: its degrade path, and the freshness/sharing properties
/// that let it replace the three private caches
/// <see cref="TotallyHot.ArcRouter.Router.Orchestrator.DimBestVoter"/>,
/// <see cref="TotallyHot.ArcRouter.Router.UntrainedBaselineSelector"/>, and
/// <see cref="TotallyHot.ArcRouter.Transcripts.TaxonomyComparisonService"/> used to keep independently
/// (each paying its own full-table scan of the same "probing" split).
/// </summary>
public sealed class ProbingPriorMatrixCacheTests
{
    [Fact]
    public void GetMatrix_NoCorpusFile_ReturnsNull()
    {
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated - no file on disk
        var cache = new ProbingPriorMatrixCache(database: temp.Database, logger: NullLogger.Instance);

        Assert.Null(cache.GetMatrix());
    }

    [Fact]
    public void GetMatrix_UsesProbingSplit()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(database: temp.Database, taskId: "task-1", split: "probing", dimension: "code_generation",
            model: "model-a", 0.2);
        InsertResultRow(database: temp.Database, taskId: "task-2", split: "probing", dimension: "code_generation",
            model: "model-b", 0.8);
        var cache = new ProbingPriorMatrixCache(database: temp.Database, logger: NullLogger.Instance);

        var matrix = cache.GetMatrix();

        Assert.NotNull(matrix);
        Assert.Equal(0.8, actual: matrix.AverageScore(dimension: "code_generation", model: "model-b"));
    }

    // The property that makes sharing this cache across DimBestVoter/UntrainedBaselineSelector/
    // TaxonomyComparisonService safe: a call that observes no change in the probing file's ledger stamp
    // must return the exact same matrix instance rather than re-scanning - proving the shared cache
    // actually deduplicates the underlying full-table scan, not just the returned values. Rows seeded
    // directly (as every test in this suite does, bypassing BenchmarkSyncService) never touch
    // benchmark_files, so this also exercises the "file exists but no ledger row" path.
    [Fact]
    public void GetMatrix_NoCorpusChangeBetweenCalls_ReturnsTheSameInstance()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(database: temp.Database, taskId: "task-1", split: "probing", dimension: "code_generation",
            model: "model-a", 0.2);
        var cache = new ProbingPriorMatrixCache(database: temp.Database, logger: NullLogger.Instance);

        var first = cache.GetMatrix();
        var second = cache.GetMatrix();

        Assert.NotNull(first);
        Assert.Same(expected: first, actual: second);
    }

    // Regression coverage for the "duplicate synchronous SQLite scan" fix, and for Copilot's follow-up
    // finding that a filesystem-mtime freshness signal can miss a real sync on some filesystems: an
    // explicit sync - modeled here as BenchmarkSyncService would leave it, a benchmark_files ledger row
    // whose synced_at_utc advances - must be observed on the cache's next call, not masked forever by a
    // cached miss/stale matrix.
    [Fact]
    public void GetMatrix_ProbingFileLedgerStampAdvances_ReloadsTheMatrix()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(database: temp.Database, taskId: "task-1", split: "probing", dimension: "code_generation",
            model: "model-a", 0.2);
        RecordProbingSync(database: temp.Database, syncedAtUtc: DateTimeOffset.UtcNow);
        var cache = new ProbingPriorMatrixCache(database: temp.Database, logger: NullLogger.Instance);
        var beforeSync = cache.GetMatrix();
        Assert.Equal(0.2, actual: beforeSync!.AverageScore(dimension: "code_generation", model: "model-a"));

        InsertResultRow(database: temp.Database, taskId: "task-2", split: "probing", dimension: "code_generation",
            model: "model-a", 0.8);
        RecordProbingSync(database: temp.Database, syncedAtUtc: DateTimeOffset.UtcNow.AddSeconds(5));

        var afterSync = cache.GetMatrix();

        // (0.2 + 0.8) / 2 - the average now includes the second row, proving the cache actually re-scanned
        // rather than returning the first snapshot.
        Assert.Equal(0.5, actual: afterSync!.AverageScore(dimension: "code_generation", model: "model-a")!.Value,
            10);
    }

    /// <summary>
    /// Records a <c>benchmark_files</c> ledger row for the probing split's source file, the way
    /// <c>BenchmarkSyncService</c> would after a real sync - the freshness signal
    /// <see cref="ProbingPriorMatrixCache"/> keys its cache on.
    /// </summary>
    private static void RecordProbingSync(BenchmarkDatabase database, DateTimeOffset syncedAtUtc)
    {
        new BenchmarkFileLedger(database).Upsert(new BenchmarkFileLedgerEntry(
            FileName: "id_probing_results_long.csv",
            PublishedOid: "test-oid",
            SizeBytes: 0,
            RowCount: 0,
            RepoCommit: "test-commit",
            SyncedAtUtc: syncedAtUtc));
    }

    private static void InsertResultRow(
        BenchmarkDatabase database,
        string taskId,
        string split,
        string dimension,
        string model,
        double score)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_id_results (task_id, split, source_split, dimension, model, score)
                              VALUES ($taskId, $split, $split, $dimension, $model, $score);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$split", value: split);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$score", value: score);
        command.ExecuteNonQuery();
    }
}
