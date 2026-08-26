using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="OrchestratorArmFactory.Build"/>'s wiring: the frozen probing-split prior reaches
/// <c>dim_best</c> through a real, isolated <see cref="Router.Orchestrator.DimBestVoter"/> and empty
/// <see cref="Router.RouterMemory"/>; <c>logreg</c> is trained (via the real
/// <see cref="Router.Orchestrator.EmbeddingLogRegTrainer"/>) and wired only when at least one OOD outcome's
/// task id has a precomputed embedding; and the resulting <see cref="OrchestratorArmBaseline"/> actually
/// routes end to end.
/// </summary>
public class OrchestratorArmFactoryTests
{
    [Fact]
    public void Build_LogRegTrainedFromSeparableOodData_VotesTheTrainedClassWhenDimBestHasNoPrior()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        // A frozen prior exists only for "bug_fixing" - dim_best has nothing to say about "algorithm" and
        // must abstain there, isolating logreg's contribution for this test.
        InsertProbingResult(temp.Database, "p1", "bug_fixing", "model-a", score: 0.9);
        InsertProbingResult(temp.Database, "p2", "bug_fixing", "model-b", score: 0.1);

        var embeddingsByTaskId = new Dictionary<string, KnnRetrievalEntry>(StringComparer.Ordinal);
        var oodOutcomes = new List<RegretTaskOutcome>();
        for (var i = 0; i < 20; i++)
        {
            var taskId = $"algo-{i}";
            var cells = new Dictionary<string, RegretOutcomeCell>(StringComparer.Ordinal)
            {
                ["model-a"] = new RegretOutcomeCell(Score: 0.0, CostUsd: 0.01, TotalTokens: 100),
                ["model-b"] = new RegretOutcomeCell(Score: 1.0, CostUsd: 0.01, TotalTokens: 100),
            };
            oodOutcomes.Add(new RegretTaskOutcome(taskId, "algorithm", cells, TaskText: $"task {i}"));
            embeddingsByTaskId[taskId] = new KnnRetrievalEntry(taskId, [1f, (float)i / 20f], "model-b");
        }

        var knnArtifact = new KnnRetrievalArtifact(
            EmbeddingDimension: 2,
            EmbeddingModel: "test-embedding-model",
            Entries: [.. embeddingsByTaskId.Values],
            TrainedFrom: "unit test fixture");

        var arm = OrchestratorArmFactory.Build(temp.Database, oodOutcomes, knnArtifact, NullLoggerFactory.Instance);

        var picked = arm.Route(new RegretReplayContext("algo-0", "algorithm", ["model-a", "model-b"]));

        Assert.Equal("model-b", picked);
    }

    [Fact]
    public void Build_DimBestPriorFlowsThroughUnaffectedWhenNoOodTaskHasAPrecomputedEmbedding()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertProbingResult(temp.Database, "p1", "bug_fixing", "model-a", score: 0.9);
        InsertProbingResult(temp.Database, "p2", "bug_fixing", "model-b", score: 0.1);

        var oodOutcomes = new List<RegretTaskOutcome>
        {
            new(
                "t1",
                "algorithm",
                new Dictionary<string, RegretOutcomeCell>(StringComparer.Ordinal)
                {
                    ["model-a"] = new RegretOutcomeCell(1.0, 0.01, 100),
                },
                TaskText: "some text"),
        };

        // The embedding index carries no entry for "t1", so zero (task, model) samples can be built -
        // logreg must not be wired at all, and dim_best's frozen "bug_fixing" prior must still flow
        // through untouched for a query on that dimension.
        var knnArtifact = new KnnRetrievalArtifact(
            EmbeddingDimension: 2,
            EmbeddingModel: "test-embedding-model",
            Entries: [new KnnRetrievalEntry("unrelated-task", [0f, 1f], "model-a")],
            TrainedFrom: "unit test fixture");

        var arm = OrchestratorArmFactory.Build(temp.Database, oodOutcomes, knnArtifact, NullLoggerFactory.Instance);

        var picked = arm.Route(new RegretReplayContext("some-query-task", "bug_fixing", ["model-a", "model-b"]));

        Assert.Equal("model-a", picked);
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        var artifact = new KnnRetrievalArtifact(1, "m", [new KnnRetrievalEntry("t1", [0f], "model-a")], "x");

        Assert.Throws<ArgumentNullException>(() => OrchestratorArmFactory.Build(null!, [], artifact, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => OrchestratorArmFactory.Build(temp.Database, null!, artifact, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => OrchestratorArmFactory.Build(temp.Database, [], null!, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => OrchestratorArmFactory.Build(temp.Database, [], artifact, null!));
    }

    private static void InsertProbingResult(BenchmarkDatabase database, string taskId, string dimension, string model, double score)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_id_results (task_id, split, source_split, dimension, model, score)
            VALUES ($taskId, 'probing', 'probing', $dimension, $model, $score);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$score", score);
        command.ExecuteNonQuery();
    }
}
