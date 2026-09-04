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

        InsertTask(database: temp.Database, taskId: "t1", dimension: "bug_fixing", prompt: "fix the bug");
        InsertResult(database: temp.Database, taskId: "t1", model: "model-a", true, 0.01, 100, 50);
        InsertResult(database: temp.Database, taskId: "t1", model: "model-b", false, 0.02, 100, 50);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(expected: "t1", actual: outcome.TaskId);
        Assert.Equal(expected: "bug_fixing", actual: outcome.Dimension);
        Assert.Equal(expected: "fix the bug", actual: outcome.TaskText);
        Assert.Equal(1.0, actual: outcome.Cells["model-a"].Score);
        Assert.Equal(0.0, actual: outcome.Cells["model-b"].Score);
        Assert.Equal(150, actual: outcome.Cells["model-a"].TotalTokens);
    }

    [Fact]
    public void Load_RowWithNullResolved_IsExcluded()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTask(database: temp.Database, taskId: "t1", dimension: "bug_fixing", prompt: "fix the bug");
        InsertResultWithNullResolved(database: temp.Database, taskId: "t1", model: "model-a", 0.01);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        Assert.Empty(outcomes);
    }

    [Fact]
    public void Load_MissingCostUsd_FallsBackToModelPricingOverTokenCounts()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTask(database: temp.Database, taskId: "t1", dimension: "bug_fixing", prompt: "fix the bug");
        InsertResultWithNullCost(database: temp.Database, taskId: "t1", model: "model-a", true, 1_000_000, 1_000_000);
        InsertModelPricing(database: temp.Database, model: "model-a", 2.0, 4.0);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(6.0, actual: outcome.Cells["model-a"].CostUsd, 6);
    }

    [Fact]
    public void Load_MissingCostUsdAndNoPricing_ExcludesTheCell()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTask(database: temp.Database, taskId: "t1", dimension: "bug_fixing", prompt: "fix the bug");
        InsertResultWithNullCost(database: temp.Database, taskId: "t1", model: "model-a", true, 100, 50);

        var outcomes = OodRegretTaskOutcomeLoader.Load(temp.Database);

        Assert.Empty(outcomes);
    }

    [Fact]
    public void Load_TaskWithNoExtractablePrompt_HasNullTaskText()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertTaskWithRawJson(database: temp.Database, taskId: "t1", dimension: "bug_fixing", """{"task_id":"t1"}""");
        InsertResult(database: temp.Database, taskId: "t1", model: "model-a", true, 0.01, 10, 10);

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
    public void Load_NullDatabase_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => OodRegretTaskOutcomeLoader.Load(null!));
    }

    private static void InsertTask(BenchmarkDatabase database, string taskId, string dimension, string prompt)
    {
        InsertTaskWithRawJson(database: database, taskId: taskId, dimension: dimension,
            rawJson: $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
    }

    private static void InsertTaskWithRawJson(BenchmarkDatabase database, string taskId, string dimension,
        string rawJson)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
                              VALUES ($taskId, 'test', 'test-bench', $dimension, $rawJson);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$rawJson", value: rawJson);
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
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$resolved", value: resolved ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$costUsd", value: costUsd);
        command.Parameters.AddWithValue(parameterName: "$inTok", value: inTok);
        command.Parameters.AddWithValue(parameterName: "$outTok", value: outTok);
        command.ExecuteNonQuery();
    }

    private static void InsertResultWithNullResolved(BenchmarkDatabase database, string taskId, string model,
        double costUsd)
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
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$costUsd", value: costUsd);
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
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$resolved", value: resolved ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$inTok", value: inTok);
        command.Parameters.AddWithValue(parameterName: "$outTok", value: outTok);
        command.ExecuteNonQuery();
    }

    private static void InsertModelPricing(BenchmarkDatabase database, string model, double inputPer1M,
        double outputPer1M)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_models (model, canonical_key, input_per_1m, output_per_1m, raw_json)
                              VALUES ($model, $model, $inputPer1M, $outputPer1M, '{}');
                              """;
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$inputPer1M", value: inputPer1M);
        command.Parameters.AddWithValue(parameterName: "$outputPer1M", value: outputPer1M);
        command.ExecuteNonQuery();
    }
}