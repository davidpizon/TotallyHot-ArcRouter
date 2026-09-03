using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="IdSplitRegretTaskOutcomeLoader.Load"/>: reading <c>benchmark_id_results</c>' own
/// continuous <c>score</c> column directly (no resolved-to-score conversion, unlike the OOD split), cost
/// resolution (row's own <c>cost_usd</c>, falling back to <c>benchmark_models</c> pricing), the split
/// filter, and that every row's <see cref="RegretTaskOutcome.TaskText"/> is always <see langword="null"/>.
/// </summary>
public class IdSplitRegretTaskOutcomeLoaderTests
{
    [Fact]
    public void Load_FiltersToTheRequestedSplit_AndTaskTextIsAlwaysNull()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertResult(database: temp.Database, taskId: "t1", split: "id_test", dimension: "bug_fixing", model: "model-a",
            0.8, 0.01, 10, 5, 15);
        InsertResult(database: temp.Database, taskId: "p1", split: "probing", dimension: "bug_fixing", model: "model-a",
            0.6, 0.01, 10, 5, 15);

        var idTestOutcomes = IdSplitRegretTaskOutcomeLoader.Load(database: temp.Database, split: "id_test");

        var outcome = Assert.Single(idTestOutcomes);
        Assert.Equal(expected: "t1", actual: outcome.TaskId);
        Assert.Null(outcome.TaskText);
        Assert.Equal(0.8, actual: outcome.Cells["model-a"].Score);
        Assert.Equal(15, actual: outcome.Cells["model-a"].TotalTokens);
    }

    [Fact]
    public void Load_MissingCostUsd_FallsBackToModelPricingOverTokenCounts()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertResultWithNullCost(database: temp.Database, taskId: "t1", split: "id_test", dimension: "bug_fixing",
            model: "model-a", 0.5, 1_000_000, 1_000_000);
        InsertModelPricing(database: temp.Database, model: "model-a", 2.0, 4.0);

        var outcomes = IdSplitRegretTaskOutcomeLoader.Load(database: temp.Database, split: "id_test");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(6.0, actual: outcome.Cells["model-a"].CostUsd, 6);
    }

    [Fact]
    public void Load_MissingCostUsdAndNoPricing_ExcludesTheCell()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertResultWithNullCost(database: temp.Database, taskId: "t1", split: "id_test", dimension: "bug_fixing",
            model: "model-a", 0.5, 10, 5);

        var outcomes = IdSplitRegretTaskOutcomeLoader.Load(database: temp.Database, split: "id_test");

        Assert.Empty(outcomes);
    }

    [Fact]
    public void Load_DatabaseNotSynced_Throws()
    {
        using var temp = new TempBenchmarkDatabase();

        Assert.Throws<InvalidOperationException>(() =>
            IdSplitRegretTaskOutcomeLoader.Load(database: temp.Database, split: "id_test"));
    }

    [Fact]
    public void Load_NullDatabase_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            IdSplitRegretTaskOutcomeLoader.Load(database: null!, split: "id_test"));
    }

    [Fact]
    public void Load_NullSplit_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Assert.Throws<ArgumentNullException>(() =>
            IdSplitRegretTaskOutcomeLoader.Load(database: temp.Database, split: null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Load_BlankSplit_Throws(string split)
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Assert.Throws<ArgumentException>(() =>
            IdSplitRegretTaskOutcomeLoader.Load(database: temp.Database, split: split));
    }

    private static void InsertResult(
        BenchmarkDatabase database, string taskId, string split, string dimension, string model,
        double score, double costUsd, long inTok, long outTok, long totalTokens)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_id_results
                                  (task_id, split, source_split, dimension, model, score, cost_usd, input_tokens, output_tokens, total_tokens)
                              VALUES
                                  ($taskId, $split, $split, $dimension, $model, $score, $costUsd, $inTok, $outTok, $totalTokens);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$split", value: split);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$score", value: score);
        command.Parameters.AddWithValue(parameterName: "$costUsd", value: costUsd);
        command.Parameters.AddWithValue(parameterName: "$inTok", value: inTok);
        command.Parameters.AddWithValue(parameterName: "$outTok", value: outTok);
        command.Parameters.AddWithValue(parameterName: "$totalTokens", value: totalTokens);
        command.ExecuteNonQuery();
    }

    private static void InsertResultWithNullCost(
        BenchmarkDatabase database, string taskId, string split, string dimension, string model,
        double score, long inTok, long outTok)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_id_results
                                  (task_id, split, source_split, dimension, model, score, cost_usd, input_tokens, output_tokens)
                              VALUES
                                  ($taskId, $split, $split, $dimension, $model, $score, NULL, $inTok, $outTok);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$split", value: split);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$score", value: score);
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