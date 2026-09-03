using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="RegretComparisonReportBuilder.BuildReport"/> and
/// <see cref="RegretComparisonReportBuilder.FormatMarkdownTable"/> against a small, synthetic fixture -
/// every expected router appears exactly once, in the documented order, and the Markdown table renders
/// every router's name.
/// </summary>
public class RegretComparisonReportBuilderTests
{
    [Fact]
    public void BuildReport_ProducesOneRowPerRouterInDocumentedOrder()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertProbingResult(database: temp.Database, taskId: "p1", dimension: "bug_fixing", model: "model-a", 0.9);
        InsertProbingResult(database: temp.Database, taskId: "p2", dimension: "bug_fixing", model: "model-b", 0.1);
        InsertOodTaskAndResults(database: temp.Database, taskId: "t1", prompt: "There is a bug and an error to fix.",
            ("model-a", true), ("model-b", false));
        InsertOodTaskAndResults(database: temp.Database, taskId: "t2",
            prompt: "There is a bug and an error to fix again.", ("model-a", true), ("model-b", false));

        var probingMatrix = DimensionModelScoreMatrix.FromDatabase(database: temp.Database, split: "probing");
        var probingOutcomes = IdSplitRegretTaskOutcomeLoader.Load(database: temp.Database, split: "probing");
        var oodOutcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);
        var logRegArtifact = LogRegTrainer.Train(database: temp.Database, 50, 50, 0.5);

        var knnArtifact = new KnnRetrievalArtifact(
            2,
            EmbeddingModel: "test-embedding-model",
            Entries:
            [
                new KnnRetrievalEntry(TaskId: "t1", Embedding: [1f, 0f], Label: "model-a"),
                new KnnRetrievalEntry(TaskId: "t2", Embedding: [1f, 0f], Label: "model-a")
            ],
            TrainedFrom: "unit test fixture");

        var orchestratorArm = OrchestratorArmFactory.Build(database: temp.Database, oodOutcomes: oodOutcomes,
            embeddingIndex: knnArtifact, loggerFactory: NullLoggerFactory.Instance);

        var report = RegretComparisonReportBuilder.BuildReport(
            outcomes: oodOutcomes, probingOutcomes: probingOutcomes, probingMatrix: probingMatrix,
            logRegArtifact: logRegArtifact, knnArtifact: knnArtifact, orchestratorArm: orchestratorArm,
            weights: RewardWeights.Canonical);

        // 2 distinct models (Always-*m*) + dim_best + linucb + lints + logreg + knn_retrieval + orchestrator.
        Assert.Equal(8, actual: report.Count);
        Assert.Equal(
            expected:
            [
                "always_model-a", "always_model-b", "dim_best", "linucb", "lints", "logreg", "knn_retrieval",
                "orchestrator"
            ],
            actual: report.Select(row => row.RouterName));
        Assert.All(collection: report, action: row => Assert.True(double.IsFinite(row.CumulativeRegret)));
    }

    [Fact]
    public void FormatMarkdownTable_RendersHeaderAndEveryRouterName()
    {
        var rows = new List<RegretReplayResult>
        {
            new() { RouterName = "dim_best" },
            new() { RouterName = "orchestrator" }
        };

        var markdown = RegretComparisonReportBuilder.FormatMarkdownTable(title: "OOD split", rows: rows);

        Assert.Contains(expectedSubstring: "### OOD split", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "| Router | CumReg |", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "dim_best", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "orchestrator", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
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

    private static void InsertOodTaskAndResults(BenchmarkDatabase database, string taskId, string prompt,
        params (string Model, bool Resolved)[] results)
    {
        using var connection = database.OpenConnection();
        using (var taskCommand = connection.CreateCommand())
        {
            taskCommand.CommandText = """
                                      INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
                                      VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $rawJson);
                                      """;
            taskCommand.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
            taskCommand.Parameters.AddWithValue(parameterName: "$rawJson",
                value: $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
            taskCommand.ExecuteNonQuery();
        }

        foreach (var (model, resolved) in results)
        {
            using var resultCommand = connection.CreateCommand();
            resultCommand.CommandText = """
                                        INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
                                        VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $model, $resolved, 0.01);
                                        """;
            resultCommand.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
            resultCommand.Parameters.AddWithValue(parameterName: "$model", value: model);
            resultCommand.Parameters.AddWithValue(parameterName: "$resolved", value: resolved ? 1 : 0);
            resultCommand.ExecuteNonQuery();
        }
    }
}