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

        InsertResult(temp.Database, "t1", "id_test", "bug_fixing", "model-a", score: 0.8, costUsd: 0.01, inTok: 10, outTok: 5, totalTokens: 15);
        InsertResult(temp.Database, "p1", "probing", "bug_fixing", "model-a", score: 0.6, costUsd: 0.01, inTok: 10, outTok: 5, totalTokens: 15);

        var idTestOutcomes = IdSplitRegretTaskOutcomeLoader.Load(temp.Database, "id_test");

        var outcome = Assert.Single(idTestOutcomes);
        Assert.Equal("t1", outcome.TaskId);
        Assert.Null(outcome.TaskText);
        Assert.Equal(0.8, outcome.Cells["model-a"].Score);
        Assert.Equal(15, outcome.Cells["model-a"].TotalTokens);
    }

    [Fact]
    public void Load_MissingCostUsd_FallsBackToModelPricingOverTokenCounts()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertResultWithNullCost(temp.Database, "t1", "id_test", "bug_fixing", "model-a", score: 0.5, inTok: 1_000_000, outTok: 1_000_000);
        InsertModelPricing(temp.Database, "model-a", inputPer1M: 2.0, outputPer1M: 4.0);

        var outcomes = IdSplitRegretTaskOutcomeLoader.Load(temp.Database, "id_test");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(6.0, outcome.Cells["model-a"].CostUsd, precision: 6);
    }

    [Fact]
    public void Load_MissingCostUsdAndNoPricing_ExcludesTheCell()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertResultWithNullCost(temp.Database, "t1", "id_test", "bug_fixing", "model-a", score: 0.5, inTok: 10, outTok: 5);

        var outcomes = IdSplitRegretTaskOutcomeLoader.Load(temp.Database, "id_test");

        Assert.Empty(outcomes);
    }

    [Fact]
    public void Load_DatabaseNotSynced_Throws()
    {
        using var temp = new TempBenchmarkDatabase();

        Assert.Throws<InvalidOperationException>(() => IdSplitRegretTaskOutcomeLoader.Load(temp.Database, "id_test"));
    }

    [Fact]
    public void Load_NullDatabase_Throws() =>
        Assert.Throws<ArgumentNullException>(() => IdSplitRegretTaskOutcomeLoader.Load(null!, "id_test"));

    [Fact]
    public void Load_NullSplit_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Assert.Throws<ArgumentNullException>(() => IdSplitRegretTaskOutcomeLoader.Load(temp.Database, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Load_BlankSplit_Throws(string split)
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Assert.Throws<ArgumentException>(() => IdSplitRegretTaskOutcomeLoader.Load(temp.Database, split));
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
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$split", split);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$score", score);
        command.Parameters.AddWithValue("$costUsd", costUsd);
        command.Parameters.AddWithValue("$inTok", inTok);
        command.Parameters.AddWithValue("$outTok", outTok);
        command.Parameters.AddWithValue("$totalTokens", totalTokens);
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
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$split", split);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$score", score);
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
