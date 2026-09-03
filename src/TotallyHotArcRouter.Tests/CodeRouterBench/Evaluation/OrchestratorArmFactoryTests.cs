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
        InsertProbingResult(database: temp.Database, taskId: "p1", dimension: "bug_fixing", model: "model-a", 0.9);
        InsertProbingResult(database: temp.Database, taskId: "p2", dimension: "bug_fixing", model: "model-b", 0.1);

        var embeddingsByTaskId = new Dictionary<string, KnnRetrievalEntry>(StringComparer.Ordinal);
        var oodOutcomes = new List<RegretTaskOutcome>();
        for (var i = 0; i < 20; i++)
        {
            var taskId = $"algo-{i}";
            var cells = new Dictionary<string, RegretOutcomeCell>(StringComparer.Ordinal)
            {
                ["model-a"] = new(0.0, 0.01, 100),
                ["model-b"] = new(1.0, 0.01, 100)
            };
            oodOutcomes.Add(new RegretTaskOutcome(TaskId: taskId, Dimension: "algorithm", Cells: cells,
                TaskText: $"task {i}"));
            embeddingsByTaskId[taskId] =
                new KnnRetrievalEntry(TaskId: taskId, Embedding: [1f, i / 20f], Label: "model-b");
        }

        var knnArtifact = new KnnRetrievalArtifact(
            2,
            EmbeddingModel: "test-embedding-model",
            Entries: [.. embeddingsByTaskId.Values],
            TrainedFrom: "unit test fixture");

        var arm = OrchestratorArmFactory.Build(database: temp.Database, oodOutcomes: oodOutcomes,
            embeddingIndex: knnArtifact, loggerFactory: NullLoggerFactory.Instance);

        var picked = arm.Route(new RegretReplayContext(TaskId: "algo-0", Dimension: "algorithm",
            CandidateModelIds: ["model-a", "model-b"]));

        Assert.Equal(expected: "model-b", actual: picked);
    }

    [Fact]
    public void Build_DimBestPriorFlowsThroughUnaffectedWhenNoOodTaskHasAPrecomputedEmbedding()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertProbingResult(database: temp.Database, taskId: "p1", dimension: "bug_fixing", model: "model-a", 0.9);
        InsertProbingResult(database: temp.Database, taskId: "p2", dimension: "bug_fixing", model: "model-b", 0.1);

        var oodOutcomes = new List<RegretTaskOutcome>
        {
            new(
                TaskId: "t1",
                Dimension: "algorithm",
                Cells: new Dictionary<string, RegretOutcomeCell>(StringComparer.Ordinal)
                {
                    ["model-a"] = new(1.0, 0.01, 100)
                },
                TaskText: "some text")
        };

        // The embedding index carries no entry for "t1", so zero (task, model) samples can be built -
        // logreg must not be wired at all, and dim_best's frozen "bug_fixing" prior must still flow
        // through untouched for a query on that dimension.
        var knnArtifact = new KnnRetrievalArtifact(
            2,
            EmbeddingModel: "test-embedding-model",
            Entries: [new KnnRetrievalEntry(TaskId: "unrelated-task", Embedding: [0f, 1f], Label: "model-a")],
            TrainedFrom: "unit test fixture");

        var arm = OrchestratorArmFactory.Build(database: temp.Database, oodOutcomes: oodOutcomes,
            embeddingIndex: knnArtifact, loggerFactory: NullLoggerFactory.Instance);

        var picked = arm.Route(new RegretReplayContext(TaskId: "some-query-task", Dimension: "bug_fixing",
            CandidateModelIds: ["model-a", "model-b"]));

        Assert.Equal(expected: "model-a", actual: picked);
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        var artifact = new KnnRetrievalArtifact(1, EmbeddingModel: "m",
            Entries: [new KnnRetrievalEntry(TaskId: "t1", Embedding: [0f], Label: "model-a")], TrainedFrom: "x");

        Assert.Throws<ArgumentNullException>(() => OrchestratorArmFactory.Build(database: null!, oodOutcomes: [],
            embeddingIndex: artifact, loggerFactory: NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => OrchestratorArmFactory.Build(database: temp.Database,
            oodOutcomes: null!, embeddingIndex: artifact, loggerFactory: NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => OrchestratorArmFactory.Build(database: temp.Database,
            oodOutcomes: [], embeddingIndex: null!, loggerFactory: NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => OrchestratorArmFactory.Build(database: temp.Database,
            oodOutcomes: [], embeddingIndex: artifact, loggerFactory: null!));
    }

    private static void InsertProbingResult(BenchmarkDatabase database, string taskId, string dimension, string model,
        double score)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_id_results (task_id, split, source_split, dimension, model, score)
                              VALUES ($taskId, 'probing', 'probing', $dimension, $model, $score);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$score", value: score);
        command.ExecuteNonQuery();
    }
}