using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using Microsoft.Extensions.Logging.Abstractions;

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
        InsertTask(temp.Database, "t1", "fix the bug");
        InsertTask(temp.Database, "t2", "write a proof");

        var source = new OodClusterBootstrapSampleSource(
            temp.Database, new FakeEmbeddingClient(text => [1, 0, 0]), NullLogger<OodClusterBootstrapSampleSource>.Instance);

        var samples = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Null(s.Dimension));
        Assert.All(samples, s => Assert.Equal(1.0, s.Weight));
    }

    [Fact]
    public async Task LoadAsync_TaskWithNoPromptProperty_IsSkipped()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTaskWithRawJson(temp.Database, "t1", """{"task_id":"t1"}""");

        var source = new OodClusterBootstrapSampleSource(
            temp.Database, new FakeEmbeddingClient(text => [1, 0, 0]), NullLogger<OodClusterBootstrapSampleSource>.Instance);

        var samples = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(samples);
    }

    [Fact]
    public async Task LoadAsync_DatabaseNotSynced_Throws()
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        var database = new BenchmarkDatabase(
            Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { BenchmarkDatabasePath = Path.Combine(directory, "coderouterbench.db") }));

        var source = new OodClusterBootstrapSampleSource(
            database, new FakeEmbeddingClient(text => [1, 0, 0]), NullLogger<OodClusterBootstrapSampleSource>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(database.DatabasePath));
    }

    private static void InsertTask(BenchmarkDatabase database, string taskId, string prompt) =>
        InsertTaskWithRawJson(database, taskId, $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");

    private static void InsertTaskWithRawJson(BenchmarkDatabase database, string taskId, string rawJson)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
            VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $rawJson);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$rawJson", rawJson);
        command.ExecuteNonQuery();
    }

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult(embed(text), TokenCount: 0));
    }
}
