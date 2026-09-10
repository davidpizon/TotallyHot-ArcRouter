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
    // TaxonomyComparisonService safe: a call that observes no change in BenchmarkDatabase.GetContentStamp
    // must return the exact same matrix instance rather than re-scanning - proving the shared cache
    // actually deduplicates the underlying full-table scan, not just the returned values.
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

    // Regression coverage for the "duplicate synchronous SQLite scan" fix: an explicit benchmark sync must
    // be observed on the next call, not masked forever by a cached miss/stale matrix. Uses
    // File.SetLastWriteTimeUtc rather than a real timing gap between two sequential inserts, since NTFS/OS
    // mtime resolution could otherwise leave two rapid writes with an identical stamp and make this test
    // flaky.
    [Fact]
    public void GetMatrix_CorpusStampAdvances_ReloadsTheMatrix()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(database: temp.Database, taskId: "task-1", split: "probing", dimension: "code_generation",
            model: "model-a", 0.2);
        var cache = new ProbingPriorMatrixCache(database: temp.Database, logger: NullLogger.Instance);
        var beforeSync = cache.GetMatrix();
        Assert.Equal(0.2, actual: beforeSync!.AverageScore(dimension: "code_generation", model: "model-a"));

        InsertResultRow(database: temp.Database, taskId: "task-2", split: "probing", dimension: "code_generation",
            model: "model-a", 0.8);
        File.SetLastWriteTimeUtc(temp.DatabasePath, DateTime.UtcNow.AddSeconds(5));

        var afterSync = cache.GetMatrix();

        // (0.2 + 0.8) / 2 - the average now includes the second row, proving the cache actually re-scanned
        // rather than returning the first snapshot.
        Assert.Equal(0.5, actual: afterSync!.AverageScore(dimension: "code_generation", model: "model-a")!.Value,
            10);
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
