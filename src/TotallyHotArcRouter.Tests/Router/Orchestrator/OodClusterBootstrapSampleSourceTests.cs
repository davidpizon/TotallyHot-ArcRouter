using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="OodClusterBootstrapSampleSource.LoadAsync"/> (docs/router/self-organizing-classification-plan.md
/// Phase T2d) against a small synthetic <see cref="BenchmarkDatabase"/>, mirroring
/// <see cref="OodBootstrapSampleSourceTests"/>'s fixture-insertion helpers.
/// </summary>
public class OodClusterBootstrapSampleSourceTests
{
    [Fact]
    public async Task LoadAsync_TasksWithPrompts_ProducesOneSamplePerTaskWithNoDimensionLabel()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(database: temp.Database, taskId: "t1", prompt: "fix the bug");
        InsertTask(database: temp.Database, taskId: "t2", prompt: "write a proof");

        var source = new OodClusterBootstrapSampleSource(
            database: temp.Database, embeddingClient: new FakeEmbeddingClient(text => [1, 0, 0]),
            logger: NullLogger<OodClusterBootstrapSampleSource>.Instance);

        var samples = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, actual: samples.Count);
        Assert.All(collection: samples, action: s => Assert.Null(s.Dimension));
        Assert.All(collection: samples, action: s => Assert.Equal(1.0, actual: s.Weight));
    }

    [Fact]
    public async Task LoadAsync_TaskWithNoPromptProperty_IsSkipped()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTaskWithRawJson(database: temp.Database, taskId: "t1", """{"task_id":"t1"}""");

        var source = new OodClusterBootstrapSampleSource(
            database: temp.Database, embeddingClient: new FakeEmbeddingClient(text => [1, 0, 0]),
            logger: NullLogger<OodClusterBootstrapSampleSource>.Instance);

        var samples = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        var source = new OodClusterBootstrapSampleSource(
            database: database, embeddingClient: new FakeEmbeddingClient(text => [1, 0, 0]),
            logger: NullLogger<OodClusterBootstrapSampleSource>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));

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

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EmbeddingResult(Vector: embed(text), 0));
        }
    }
}