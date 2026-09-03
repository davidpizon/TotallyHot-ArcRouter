using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="OodBootstrapSampleSource.LoadAsync"/> (docs/router/live-feedback-learning-plan.md
/// Phase 4a) against a small synthetic <see cref="BenchmarkDatabase"/>, mirroring
/// <see cref="Tests.CodeRouterBench.LogRegTrainerTests"/>'s fixture-insertion helpers rather than the
/// real, multi-hundred-MB synced corpus.
/// </summary>
public class OodBootstrapSampleSourceTests
{
    [Fact]
    public async Task LoadAsync_TaskWithPromptAndResults_ProducesOneSamplePerResultRow()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(database: temp.Database, taskId: "t1", prompt: "fix the bug");
        InsertResult(database: temp.Database, taskId: "t1", model: "model-a", true);
        InsertResult(database: temp.Database, taskId: "t1", model: "model-b", false);

        var source = new OodBootstrapSampleSource(
            database: temp.Database, embeddingClient: new FakeEmbeddingClient(text => [1, 0, 0]),
            logger: NullLogger<OodBootstrapSampleSource>.Instance);

        var (samples, taskCount) = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: taskCount);
        Assert.Equal(2, actual: samples.Count);
        Assert.Contains(collection: samples, filter: s => s.ModelKey == "model-a" && s.Score == 1.0);
        Assert.Contains(collection: samples, filter: s => s.ModelKey == "model-b" && s.Score == 0.0);
        Assert.All(collection: samples, action: s => Assert.Equal(1.0, actual: s.Weight));
    }

    [Fact]
    public async Task LoadAsync_TaskWithNoPromptProperty_IsSkipped()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTaskWithRawJson(database: temp.Database, taskId: "t1", """{"task_id":"t1"}""");
        InsertResult(database: temp.Database, taskId: "t1", model: "model-a", true);

        var source = new OodBootstrapSampleSource(
            database: temp.Database, embeddingClient: new FakeEmbeddingClient(text => [1, 0, 0]),
            logger: NullLogger<OodBootstrapSampleSource>.Instance);

        var (samples, taskCount) = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: taskCount);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task LoadAsync_TaskWithNoResultRows_IsSkipped()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(database: temp.Database, taskId: "t1", prompt: "fix the bug");

        var source = new OodBootstrapSampleSource(
            database: temp.Database, embeddingClient: new FakeEmbeddingClient(text => [1, 0, 0]),
            logger: NullLogger<OodBootstrapSampleSource>.Instance);

        var (samples, taskCount) = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: taskCount);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task LoadAsync_DatabaseNotSynced_Throws()
    {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        var database = new BenchmarkDatabase(
            Options.Create(
                new StorageOptions
                { BenchmarkDatabasePath = Path.Combine(path1: directory, path2: "coderouterbench.db") }));

        var source = new OodBootstrapSampleSource(
            database: database, embeddingClient: new FakeEmbeddingClient(text => [1, 0, 0]),
            logger: NullLogger<OodBootstrapSampleSource>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));

        // The existence check must happen before ever opening a connection - SQLite would otherwise
        // create an empty file as a side effect (DimBestVoter's same idiom).
        Assert.False(File.Exists(database.DatabasePath));
    }

    private static void InsertTask(BenchmarkDatabase database, string taskId, string prompt)
    {
        InsertTaskWithRawJson(database: database, taskId: taskId,
            rawJson: $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
    }

    private static void InsertTaskWithRawJson(BenchmarkDatabase database, string taskId, string rawJson)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
                              VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $rawJson);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$rawJson", value: rawJson);
        command.ExecuteNonQuery();
    }

    private static void InsertResult(BenchmarkDatabase database, string taskId, string model, bool resolved)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
                              VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $model, $resolved, 0.01);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$resolved", value: resolved ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EmbeddingResult(Vector: embed(text), 0));
        }
    }
}