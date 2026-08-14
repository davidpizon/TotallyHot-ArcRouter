using System.Reflection;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Covers <see cref="LogRegTrainer.Train"/> end-to-end against a small, synthetic
/// <see cref="BenchmarkDatabase"/> - two clearly-separable classes over a tiny vocabulary, enough to
/// verify the tokenize -> vocabulary -> TF-IDF -> gradient-descent -> argmax pipeline produces a usable
/// <see cref="LogRegModelArtifact"/> without depending on the real, multi-hundred-MB synced corpus (see
/// <see cref="LogRegTrainerReconciliationTests"/> for that).
/// </summary>
public class LogRegTrainerTests
{
    [Fact]
    public async Task Train_SeparableSyntheticCorpus_LearnsToDistinguishTheTwoClasses()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        // 20 "bug" tasks labeled by model-bug's higher score, 20 "algorithm" tasks labeled by model-algo's.
        for (var i = 0; i < 20; i++)
        {
            InsertTask(temp.Database, $"bug-{i}", "probing", "bug_fixing", "There is a bug and an error to fix.");
            InsertResult(temp.Database, $"bug-{i}", "probing", "bug_fixing", "model-bug", 0.9);
            InsertResult(temp.Database, $"bug-{i}", "probing", "bug_fixing", "model-algo", 0.1);

            InsertTask(temp.Database, $"algo-{i}", "probing", "algorithm", "Optimize this algorithm for lower complexity.");
            InsertResult(temp.Database, $"algo-{i}", "probing", "algorithm", "model-algo", 0.9);
            InsertResult(temp.Database, $"algo-{i}", "probing", "algorithm", "model-bug", 0.1);
        }

        var artifact = LogRegTrainer.Train(temp.Database, "probing", vocabularySize: 50, epochs: 200, learningRate: 0.5);

        Assert.False(artifact.IsPlaceholder);
        Assert.Contains("model-bug", artifact.ClassWeights.Keys);
        Assert.Contains("model-algo", artifact.ClassWeights.Keys);

        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, artifact);
        var candidates = new[]
        {
            new RoutingCandidate("model-bug", "openai", IsFree: false),
            new RoutingCandidate("model-algo", "openai", IsFree: false),
        };

        var bugVote = await voter.VoteAsync(
            new VotingContext("bug_fixing", candidates, TaskText: "another bug causing an error"),
            TestContext.Current.CancellationToken);
        var algoVote = await voter.VoteAsync(
            new VotingContext("algorithm", candidates, TaskText: "reduce the algorithm's complexity"),
            TestContext.Current.CancellationToken);

        Assert.Equal("model-bug", bugVote.ModelName);
        Assert.Equal("model-algo", algoVote.ModelName);
    }

    [Fact]
    public void Train_EmptySplit_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Assert.Throws<InvalidOperationException>(() => LogRegTrainer.Train(temp.Database, "probing"));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5)]
    public void Train_NonPositiveLearningRate_Throws(double learningRate)
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(temp.Database, "t1", "probing", "bug_fixing", "fix the bug");
        InsertResult(temp.Database, "t1", "probing", "bug_fixing", "model-a", 0.9);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogRegTrainer.Train(temp.Database, "probing", learningRate: learningRate));
    }

    [Fact]
    public void Train_NegativeL2Regularization_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(temp.Database, "t1", "probing", "bug_fixing", "fix the bug");
        InsertResult(temp.Database, "t1", "probing", "bug_fixing", "model-a", 0.9);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogRegTrainer.Train(temp.Database, "probing", l2Regularization: -0.001));
    }

    /// <summary>
    /// Regression test for the naive <c>1/(1+exp(-z))</c> form, which overflows <c>exp(-z)</c> to
    /// <see cref="double.PositiveInfinity"/> for very negative z. The stable form must stay finite and
    /// converge to the correct 0/1 limits at both extremes.
    /// </summary>
    [Theory]
    [InlineData(-1000d, 0d)]
    [InlineData(1000d, 1d)]
    [InlineData(0d, 0.5d)]
    public void Sigmoid_ExtremeInputs_StaysFiniteAndConvergesToCorrectLimit(double z, double expected)
    {
        var method = typeof(LogRegTrainer).GetMethod("Sigmoid", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LogRegTrainer.Sigmoid was not found via reflection.");

        var result = (double)method.Invoke(null, [z])!;

        Assert.False(double.IsNaN(result));
        Assert.False(double.IsInfinity(result));
        Assert.Equal(expected, result, precision: 6);
    }

    private static void InsertTask(BenchmarkDatabase database, string taskId, string split, string dimension, string prompt)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_id_tasks (task_id, split, source_split, dimension, raw_json)
            VALUES ($taskId, $split, $split, $dimension, $rawJson);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$split", split);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$rawJson", $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
        command.ExecuteNonQuery();
    }

    private static void InsertResult(BenchmarkDatabase database, string taskId, string split, string dimension, string model, double score)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_id_results (task_id, split, source_split, dimension, model, score)
            VALUES ($taskId, $split, $split, $dimension, $model, $score);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$split", split);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$score", score);
        command.ExecuteNonQuery();
    }
}
