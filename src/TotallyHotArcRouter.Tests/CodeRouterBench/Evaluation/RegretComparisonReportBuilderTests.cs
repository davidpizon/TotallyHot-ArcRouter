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

        InsertProbingResult(temp.Database, "p1", "bug_fixing", "model-a", 0.9);
        InsertProbingResult(temp.Database, "p2", "bug_fixing", "model-b", 0.1);
        InsertOodTaskAndResults(temp.Database, "t1", "There is a bug and an error to fix.", ("model-a", true), ("model-b", false));
        InsertOodTaskAndResults(temp.Database, "t2", "There is a bug and an error to fix again.", ("model-a", true), ("model-b", false));

        var probingMatrix = DimensionModelScoreMatrix.FromDatabase(temp.Database, "probing");
        var probingOutcomes = IdSplitRegretTaskOutcomeLoader.Load(temp.Database, "probing");
        var oodOutcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);
        var logRegArtifact = LogRegTrainer.Train(temp.Database, vocabularySize: 50, epochs: 50, learningRate: 0.5);

        var knnArtifact = new KnnRetrievalArtifact(
            EmbeddingDimension: 2,
            EmbeddingModel: "test-embedding-model",
            Entries: [new KnnRetrievalEntry("t1", [1f, 0f], "model-a"), new KnnRetrievalEntry("t2", [1f, 0f], "model-a")],
            TrainedFrom: "unit test fixture");

        var orchestratorArm = OrchestratorArmFactory.Build(temp.Database, oodOutcomes, knnArtifact, NullLoggerFactory.Instance);

        var report = RegretComparisonReportBuilder.BuildReport(
            oodOutcomes, probingOutcomes, probingMatrix, logRegArtifact, knnArtifact, orchestratorArm, RewardWeights.Canonical);

        // 2 distinct models (Always-*m*) + dim_best + linucb + lints + logreg + knn_retrieval + orchestrator.
        Assert.Equal(8, report.Count);
        Assert.Equal(
            ["always_model-a", "always_model-b", "dim_best", "linucb", "lints", "logreg", "knn_retrieval", "orchestrator"],
            report.Select(row => row.RouterName));
        Assert.All(report, row => Assert.True(double.IsFinite(row.CumulativeRegret)));
    }

    [Fact]
    public void FormatMarkdownTable_RendersHeaderAndEveryRouterName()
    {
        var rows = new List<RegretReplayResult>
        {
            new() { RouterName = "dim_best" },
            new() { RouterName = "orchestrator" },
        };

        var markdown = RegretComparisonReportBuilder.FormatMarkdownTable("OOD split", rows);

        Assert.Contains("### OOD split", markdown, StringComparison.Ordinal);
        Assert.Contains("| Router | CumReg |", markdown, StringComparison.Ordinal);
        Assert.Contains("dim_best", markdown, StringComparison.Ordinal);
        Assert.Contains("orchestrator", markdown, StringComparison.Ordinal);
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

    private static void InsertOodTaskAndResults(BenchmarkDatabase database, string taskId, string prompt, params (string Model, bool Resolved)[] results)
    {
        using var connection = database.OpenConnection();
        using (var taskCommand = connection.CreateCommand())
        {
            taskCommand.CommandText = """
                INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
                VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $rawJson);
                """;
            taskCommand.Parameters.AddWithValue("$taskId", taskId);
            taskCommand.Parameters.AddWithValue("$rawJson", $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
            taskCommand.ExecuteNonQuery();
        }

        foreach (var (model, resolved) in results)
        {
            using var resultCommand = connection.CreateCommand();
            resultCommand.CommandText = """
                INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
                VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $model, $resolved, 0.01);
                """;
            resultCommand.Parameters.AddWithValue("$taskId", taskId);
            resultCommand.Parameters.AddWithValue("$model", model);
            resultCommand.Parameters.AddWithValue("$resolved", resolved ? 1 : 0);
            resultCommand.ExecuteNonQuery();
        }
    }
}
