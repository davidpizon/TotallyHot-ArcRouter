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

        InsertTask(database: temp.Database, taskId: "t1", dimension: "bug_fixing", prompt: "fix the null pointer bug");
        InsertResult(database: temp.Database, taskId: "t1", model: "model-a", true);
        InsertResult(database: temp.Database, taskId: "t1", model: "model-b", false);

        InsertTask(database: temp.Database, taskId: "t2", dimension: "algorithm",
            prompt: "optimize the sorting algorithm");
        InsertResult(database: temp.Database, taskId: "t2", model: "model-b", true);
        InsertResult(database: temp.Database, taskId: "t2", model: "model-a", false);

        var embeddingClient = new FakeEmbeddingClient(text =>
            text.Contains(value: "bug", comparisonType: StringComparison.Ordinal) ? [1f, 0f] : [0f, 1f]);

        var artifact = await KnnRetrievalIndexBuilder.BuildAsync(database: temp.Database,
            embeddingClient: embeddingClient, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, actual: artifact.EmbeddingDimension);
        Assert.Equal(expected: embeddingClient.ModelIdentity, actual: artifact.EmbeddingModel);
        Assert.Equal(2, actual: artifact.Entries.Count);

        var t1 = artifact.Entries.Single(e => e.TaskId == "t1");
        Assert.Equal(expected: "model-a", actual: t1.Label);
        Assert.Equal(expected: [1f, 0f], actual: t1.Embedding);

        var t2 = artifact.Entries.Single(e => e.TaskId == "t2");
        Assert.Equal(expected: "model-b", actual: t2.Label);
        Assert.Equal(expected: [0f, 1f], actual: t2.Embedding);
    }

    [Fact]
    public async Task BuildAsync_EmptyOodSplit_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KnnRetrievalIndexBuilder.BuildAsync(database: temp.Database,
                embeddingClient: new FakeEmbeddingClient(_ => [0f]),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAsync_DatabaseNotSynced_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        // Deliberately no EnsureCreated() - the database file does not exist.

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KnnRetrievalIndexBuilder.BuildAsync(database: temp.Database,
                embeddingClient: new FakeEmbeddingClient(_ => [0f]),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAsync_NullDatabase_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => KnnRetrievalIndexBuilder.BuildAsync(database: null!,
            embeddingClient: new FakeEmbeddingClient(_ => [0f]),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAsync_NullEmbeddingClient_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            KnnRetrievalIndexBuilder.BuildAsync(database: temp.Database, embeddingClient: null!,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private static void InsertTask(BenchmarkDatabase database, string taskId, string dimension, string prompt)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
                              VALUES ($taskId, 'test', 'test-bench', $dimension, $rawJson);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$rawJson",
            value: $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
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
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$resolved", value: resolved ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public string ModelIdentity => "fake-embedding-model";

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EmbeddingResult(Vector: embed(text), 0));
        }
    }
}