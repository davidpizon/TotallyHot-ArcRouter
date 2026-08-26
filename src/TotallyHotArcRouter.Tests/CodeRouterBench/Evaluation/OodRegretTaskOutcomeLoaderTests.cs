using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="OodRegretTaskOutcomeLoader.Load"/>: score derivation from <c>resolved</c>, cost
/// resolution (row's own <c>cost_usd</c>, falling back to <c>benchmark_models</c> pricing, excluding a
/// cell that resolves neither), token totals, task-text association, and exclusion of a row with no
/// well-defined score.
/// </summary>
public class OodRegretTaskOutcomeLoaderTests
{
    [Fact]
    public void Load_ResolvedAndUnresolvedRows_MapScoreCorrectly()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTask(temp.Database, "t1", "bug_fixing", "fix the bug");
        InsertResult(temp.Database, "t1", "model-a", resolved: true, costUsd: 0.01, inTok: 100, outTok: 50);
        InsertResult(temp.Database, "t1", "model-b", resolved: false, costUsd: 0.02, inTok: 100, outTok: 50);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("t1", outcome.TaskId);
        Assert.Equal("bug_fixing", outcome.Dimension);
        Assert.Equal("fix the bug", outcome.TaskText);
        Assert.Equal(1.0, outcome.Cells["model-a"].Score);
        Assert.Equal(0.0, outcome.Cells["model-b"].Score);
        Assert.Equal(150, outcome.Cells["model-a"].TotalTokens);
    }

    [Fact]
    public void Load_RowWithNullResolved_IsExcluded()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTask(temp.Database, "t1", "bug_fixing", "fix the bug");
        InsertResultWithNullResolved(temp.Database, "t1", "model-a", costUsd: 0.01);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        Assert.Empty(outcomes);
    }

    [Fact]
    public void Load_MissingCostUsd_FallsBackToModelPricingOverTokenCounts()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTask(temp.Database, "t1", "bug_fixing", "fix the bug");
        InsertResultWithNullCost(temp.Database, "t1", "model-a", resolved: true, inTok: 1_000_000, outTok: 1_000_000);
        InsertModelPricing(temp.Database, "model-a", inputPer1M: 2.0, outputPer1M: 4.0);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(6.0, outcome.Cells["model-a"].CostUsd, precision: 6);
    }

    [Fact]
    public void Load_MissingCostUsdAndNoPricing_ExcludesTheCell()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTask(temp.Database, "t1", "bug_fixing", "fix the bug");
        InsertResultWithNullCost(temp.Database, "t1", "model-a", resolved: true, inTok: 100, outTok: 50);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        Assert.Empty(outcomes);
    }

    [Fact]
    public void Load_TaskWithNoExtractablePrompt_HasNullTaskText()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTaskWithRawJson(temp.Database, "t1", "bug_fixing", """{"task_id":"t1"}""");
        InsertResult(temp.Database, "t1", "model-a", resolved: true, costUsd: 0.01, inTok: 10, outTok: 10);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        Assert.Null(Assert.Single(outcomes).TaskText);
    }

    [Fact]
    public void Load_DatabaseNotSynced_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        // Deliberately no EnsureCreated() - the database file does not exist.

        Assert.Throws<InvalidOperationException>(() => OodRegretTaskOutcomeLoader.Load(temp.Database));
    }

    [Fact]
    public void Load_NullDatabase_Throws() =>
        Assert.Throws<ArgumentNullException>(() => OodRegretTaskOutcomeLoader.Load(null!));

    private static void InsertTask(BenchmarkDatabase database, string taskId, string dimension, string prompt) =>
        InsertTaskWithRawJson(database, taskId, dimension, $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");

    private static void InsertTaskWithRawJson(BenchmarkDatabase database, string taskId, string dimension, string rawJson)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
            VALUES ($taskId, 'test', 'test-bench', $dimension, $rawJson);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$rawJson", rawJson);
        command.ExecuteNonQuery();
    }

    private static void InsertResult(
        BenchmarkDatabase database, string taskId, string model, bool resolved, double costUsd, long inTok, long outTok)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd, in_tok, out_tok)
            VALUES (
                $taskId, 'test', 'test-bench',
                (SELECT dimension FROM benchmark_ood_tasks WHERE task_id = $taskId),
                $model, $resolved, $costUsd, $inTok, $outTok);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$resolved", resolved ? 1 : 0);
        command.Parameters.AddWithValue("$costUsd", costUsd);
        command.Parameters.AddWithValue("$inTok", inTok);
        command.Parameters.AddWithValue("$outTok", outTok);
        command.ExecuteNonQuery();
    }

    private static void InsertResultWithNullResolved(BenchmarkDatabase database, string taskId, string model, double costUsd)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
            VALUES (
                $taskId, 'test', 'test-bench',
                (SELECT dimension FROM benchmark_ood_tasks WHERE task_id = $taskId),
                $model, NULL, $costUsd);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$costUsd", costUsd);
        command.ExecuteNonQuery();
    }

    private static void InsertResultWithNullCost(
        BenchmarkDatabase database, string taskId, string model, bool resolved, long inTok, long outTok)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd, in_tok, out_tok)
            VALUES (
                $taskId, 'test', 'test-bench',
                (SELECT dimension FROM benchmark_ood_tasks WHERE task_id = $taskId),
                $model, $resolved, NULL, $inTok, $outTok);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$resolved", resolved ? 1 : 0);
        command.Parameters.AddWithValue("$inTok", inTok);
        command.Parameters.AddWithValue("$outTok", outTok);
        command.ExecuteNonQuery();
    }

    private static void InsertModelPricing(BenchmarkDatabase database, string model, double inputPer1M, double outputPer1M)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_models (model, canonical_key, input_per_1m, output_per_1m, raw_json)
            VALUES ($model, $model, $inputPer1M, $outputPer1M, '{}');
            """;
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$inputPer1M", inputPer1M);
        command.Parameters.AddWithValue("$outputPer1M", outputPer1M);
        command.ExecuteNonQuery();
    }
}
