using System.Reflection;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Covers <see cref="LogRegTrainer.Train"/> end-to-end against a small, synthetic
/// <see cref="BenchmarkDatabase"/> - two clearly-separable classes over a tiny vocabulary, enough to
/// verify the tokenize -> vocabulary -> TF-IDF -> gradient-descent -> argmax pipeline produces a usable
/// <see cref="LogRegModelArtifact"/> without depending on the real, multi-hundred-MB synced corpus (see
/// <see cref="LogRegTrainerReconciliationTests"/> for that). Scores the artifact directly rather than
/// through <see cref="Router.Orchestrator.LogRegVoter"/> - docs/router/live-feedback-learning-plan.md
/// Phase 3 repurposed that voter to score embeddings; <see cref="LogRegModelArtifact"/>'s TF-IDF shape now
/// only feeds the Phase N static comparison baseline this trainer produces, trained from the OOD split -
/// the only split CodeRouterBench publishes task text for (see <see cref="LogRegTrainer"/>'s remarks).
/// </summary>
public class LogRegTrainerTests
{
    [Fact]
    public void Train_SeparableSyntheticCorpus_LearnsToDistinguishTheTwoClasses()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        // 20 "bug" tasks resolved only by model-bug, 20 "algorithm" tasks resolved only by model-algo.
        for (var i = 0; i < 20; i++)
        {
            InsertTask(temp.Database, $"bug-{i}", "bug_fixing", "There is a bug and an error to fix.");
            InsertResult(temp.Database, $"bug-{i}", "model-bug", resolved: true, costUsd: 0.01);
            InsertResult(temp.Database, $"bug-{i}", "model-algo", resolved: false, costUsd: 0.01);

            InsertTask(temp.Database, $"algo-{i}", "algorithm", "Optimize this algorithm for lower complexity.");
            InsertResult(temp.Database, $"algo-{i}", "model-algo", resolved: true, costUsd: 0.01);
            InsertResult(temp.Database, $"algo-{i}", "model-bug", resolved: false, costUsd: 0.01);
        }

        var artifact = LogRegTrainer.Train(temp.Database, vocabularySize: 50, epochs: 200, learningRate: 0.5);

        Assert.False(artifact.IsPlaceholder);
        Assert.Contains("model-bug", artifact.ClassWeights.Keys);
        Assert.Contains("model-algo", artifact.ClassWeights.Keys);

        Assert.Equal("model-bug", Argmax(artifact, "another bug causing an error"));
        Assert.Equal("model-algo", Argmax(artifact, "reduce the algorithm's complexity"));
    }

    /// <summary>Scores every class in <paramref name="artifact"/> against <paramref name="text"/>'s TF-IDF vector and returns the argmax class.</summary>
    private static string Argmax(LogRegModelArtifact artifact, string text)
    {
        var vocabularyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < artifact.Vocabulary.Count; i++)
        {
            vocabularyIndex[artifact.Vocabulary[i]] = i;
        }

        var tokens = LogRegTextTokenizer.Tokenize(text);
        var counts = new Dictionary<int, int>();
        foreach (var token in tokens)
        {
            if (vocabularyIndex.TryGetValue(token, out var index))
            {
                counts[index] = counts.GetValueOrDefault(index) + 1;
            }
        }

        string? bestClass = null;
        var bestScore = double.NegativeInfinity;
        foreach (var (model, weights) in artifact.ClassWeights)
        {
            var score = weights[0];
            foreach (var (index, count) in counts)
            {
                var termFrequency = (double)count / tokens.Count;
                score += weights[index + 1] * termFrequency * artifact.InverseDocumentFrequency[index];
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestClass = model;
            }
        }

        return bestClass ?? throw new InvalidOperationException("No class scored.");
    }

    [Fact]
    public void Train_EmptyCorpus_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Assert.Throws<InvalidOperationException>(() => LogRegTrainer.Train(temp.Database));
    }

    /// <summary>
    /// A task where no model resolved it has no well-defined winner and must be excluded from training
    /// rather than assigned an arbitrary label - the same "no fabricated labels" rule the trainer's
    /// remarks document.
    /// </summary>
    [Fact]
    public void Train_NoModelResolvesTheOnlyTask_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(temp.Database, "t1", "bug_fixing", "fix the bug");
        InsertResult(temp.Database, "t1", "model-a", resolved: false, costUsd: 0.01);

        Assert.Throws<InvalidOperationException>(() => LogRegTrainer.Train(temp.Database));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5)]
    public void Train_NonPositiveLearningRate_Throws(double learningRate)
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(temp.Database, "t1", "bug_fixing", "fix the bug");
        InsertResult(temp.Database, "t1", "model-a", resolved: true, costUsd: 0.01);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogRegTrainer.Train(temp.Database, learningRate: learningRate));
    }

    [Fact]
    public void Train_NegativeL2Regularization_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(temp.Database, "t1", "bug_fixing", "fix the bug");
        InsertResult(temp.Database, "t1", "model-a", resolved: true, costUsd: 0.01);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogRegTrainer.Train(temp.Database, l2Regularization: -0.001));
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

    private static void InsertTask(BenchmarkDatabase database, string taskId, string dimension, string prompt)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
            VALUES ($taskId, 'test', 'test-bench', $dimension, $rawJson);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$rawJson", $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
        command.ExecuteNonQuery();
    }

    private static void InsertResult(BenchmarkDatabase database, string taskId, string model, bool resolved, double costUsd)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
            VALUES ($taskId, 'test', 'test-bench', 'bug_fixing', $model, $resolved, $costUsd);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$resolved", resolved ? 1 : 0);
        command.Parameters.AddWithValue("$costUsd", costUsd);
        command.ExecuteNonQuery();
    }
}
