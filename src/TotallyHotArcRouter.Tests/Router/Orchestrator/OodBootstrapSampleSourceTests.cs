using Microsoft.Extensions.Logging.Abstractions;
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
        InsertTask(temp.Database, "t1", "fix the bug");
        InsertResult(temp.Database, "t1", "model-a", resolved: true);
        InsertResult(temp.Database, "t1", "model-b", resolved: false);

        var source = new OodBootstrapSampleSource(
            temp.Database, new FakeEmbeddingClient(text => [1, 0, 0]), NullLogger<OodBootstrapSampleSource>.Instance);

        var (samples, taskCount) = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, taskCount);
        Assert.Equal(2, samples.Count);
        Assert.Contains(samples, s => s.ModelKey == "model-a" && s.Score == 1.0);
        Assert.Contains(samples, s => s.ModelKey == "model-b" && s.Score == 0.0);
        Assert.All(samples, s => Assert.Equal(1.0, s.Weight));
    }

    [Fact]
    public async Task LoadAsync_TaskWithNoPromptProperty_IsSkipped()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTaskWithRawJson(temp.Database, "t1", """{"task_id":"t1"}""");
        InsertResult(temp.Database, "t1", "model-a", resolved: true);

        var source = new OodBootstrapSampleSource(
            temp.Database, new FakeEmbeddingClient(text => [1, 0, 0]), NullLogger<OodBootstrapSampleSource>.Instance);

        var (samples, taskCount) = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, taskCount);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task LoadAsync_TaskWithNoResultRows_IsSkipped()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(temp.Database, "t1", "fix the bug");

        var source = new OodBootstrapSampleSource(
            temp.Database, new FakeEmbeddingClient(text => [1, 0, 0]), NullLogger<OodBootstrapSampleSource>.Instance);

        var (samples, taskCount) = await source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, taskCount);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task LoadAsync_DatabaseNotSynced_Throws()
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        var database = new BenchmarkDatabase(
            Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { BenchmarkDatabasePath = Path.Combine(directory, "coderouterbench.db") }));

        var source = new OodBootstrapSampleSource(
            database, new FakeEmbeddingClient(text => [1, 0, 0]), NullLogger<OodBootstrapSampleSource>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));

        // The existence check must happen before ever opening a connection - SQLite would otherwise
        // create an empty file as a side effect (DimBestVoter's same idiom).
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

    private static void InsertResult(BenchmarkDatabase database, string taskId, string model, bool resolved)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
            VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $model, $resolved, 0.01);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$resolved", resolved ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult(embed(text), TokenCount: 0));
    }
}
