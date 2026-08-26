using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="KnnRetrievalIndexBuilder.BuildAsync"/> against a small, synthetic
/// <see cref="BenchmarkDatabase"/> and a deterministic fake embedding client - proving the loader, label
/// join, and embedding call sequence produce a valid, non-placeholder <see cref="KnnRetrievalArtifact"/>
/// without depending on a real ONNX model or the multi-hundred-MB synced corpus.
/// </summary>
public class KnnRetrievalIndexBuilderTests
{
    [Fact]
    public async Task BuildAsync_SyntheticOodSplit_ProducesOneEntryPerResolvedTask()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTask(temp.Database, "t1", "bug_fixing", "fix the null pointer bug");
        InsertResult(temp.Database, "t1", "model-a", resolved: true);
        InsertResult(temp.Database, "t1", "model-b", resolved: false);

        InsertTask(temp.Database, "t2", "algorithm", "optimize the sorting algorithm");
        InsertResult(temp.Database, "t2", "model-b", resolved: true);
        InsertResult(temp.Database, "t2", "model-a", resolved: false);

        var embeddingClient = new FakeEmbeddingClient(text => text.Contains("bug", StringComparison.Ordinal) ? [1f, 0f] : [0f, 1f]);

        var artifact = await KnnRetrievalIndexBuilder.BuildAsync(temp.Database, embeddingClient, TestContext.Current.CancellationToken);

        Assert.Equal(2, artifact.EmbeddingDimension);
        Assert.Equal(embeddingClient.ModelIdentity, artifact.EmbeddingModel);
        Assert.Equal(2, artifact.Entries.Count);

        var t1 = artifact.Entries.Single(e => e.TaskId == "t1");
        Assert.Equal("model-a", t1.Label);
        Assert.Equal([1f, 0f], t1.Embedding);

        var t2 = artifact.Entries.Single(e => e.TaskId == "t2");
        Assert.Equal("model-b", t2.Label);
        Assert.Equal([0f, 1f], t2.Embedding);
    }

    [Fact]
    public async Task BuildAsync_EmptyOodSplit_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => KnnRetrievalIndexBuilder.BuildAsync(temp.Database, new FakeEmbeddingClient(_ => [0f]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAsync_DatabaseNotSynced_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        // Deliberately no EnsureCreated() - the database file does not exist.

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => KnnRetrievalIndexBuilder.BuildAsync(temp.Database, new FakeEmbeddingClient(_ => [0f]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAsync_NullDatabase_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => KnnRetrievalIndexBuilder.BuildAsync(null!, new FakeEmbeddingClient(_ => [0f]), TestContext.Current.CancellationToken));

    [Fact]
    public async Task BuildAsync_NullEmbeddingClient_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => KnnRetrievalIndexBuilder.BuildAsync(temp.Database, null!, TestContext.Current.CancellationToken));
    }

    private static void InsertTask(BenchmarkDatabase database, string taskId, string dimension, string prompt)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
            VALUES ($taskId, 'test', 'test-bench', $dimension, $rawJson);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$rawJson", $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
        command.ExecuteNonQuery();
    }

    private static void InsertResult(BenchmarkDatabase database, string taskId, string model, bool resolved)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
            VALUES (
                $taskId,
                'test',
                'test-bench',
                (SELECT dimension FROM benchmark_ood_tasks WHERE task_id = $taskId),
                $model,
                $resolved,
                0.01);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$resolved", resolved ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public string ModelIdentity => "fake-embedding-model";

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult(embed(text), TokenCount: 0));
    }
}
