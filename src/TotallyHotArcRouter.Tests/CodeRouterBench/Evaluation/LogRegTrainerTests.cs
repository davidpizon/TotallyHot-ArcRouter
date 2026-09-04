using System.Reflection;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="LogRegTrainer.Train"/> end-to-end against a small, synthetic
/// <see cref="BenchmarkDatabase"/> - two clearly-separable classes over a tiny vocabulary, enough to
/// verify the tokenize -> vocabulary -> TF-IDF -> gradient-descent -> argmax pipeline produces a usable
/// <see cref="LogRegModelArtifact"/> without depending on the real, multi-hundred-MB synced corpus (see
/// <see cref="LogRegTrainerReconciliationTests"/> for that). Scores the artifact directly rather than
/// through <see cref="TotallyHot.ArcRouter.Router.Orchestrator.LogRegVoter"/> - docs/router/live-feedback-learning-plan.md
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
            InsertTask(database: temp.Database, taskId: $"bug-{i}", dimension: "bug_fixing",
                prompt: "There is a bug and an error to fix.");
            InsertResult(database: temp.Database, taskId: $"bug-{i}", model: "model-bug", true, 0.01);
            InsertResult(database: temp.Database, taskId: $"bug-{i}", model: "model-algo", false, 0.01);

            InsertTask(database: temp.Database, taskId: $"algo-{i}", dimension: "algorithm",
                prompt: "Optimize this algorithm for lower complexity.");
            InsertResult(database: temp.Database, taskId: $"algo-{i}", model: "model-algo", true, 0.01);
            InsertResult(database: temp.Database, taskId: $"algo-{i}", model: "model-bug", false, 0.01);
        }

        var artifact = LogRegTrainer.Train(database: temp.Database, 50, 200);

        Assert.False(artifact.IsPlaceholder);
        Assert.Contains(expected: "model-bug", collection: artifact.ClassWeights.Keys);
        Assert.Contains(expected: "model-algo", collection: artifact.ClassWeights.Keys);

        Assert.Equal(expected: "model-bug", actual: Argmax(artifact: artifact, text: "another bug causing an error"));
        Assert.Equal(expected: "model-algo",
            actual: Argmax(artifact: artifact, text: "reduce the algorithm's complexity"));
    }

    /// <summary>
    /// Scores every class in <paramref name="artifact"/> against <paramref name="text"/>'s TF-IDF vector and returns
    /// the argmax class.
    /// </summary>
    private static string Argmax(LogRegModelArtifact artifact, string text)
    {
        var vocabularyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < artifact.Vocabulary.Count; i++) vocabularyIndex[artifact.Vocabulary[i]] = i;

        var tokens = LogRegTextTokenizer.Tokenize(text);
        var counts = new Dictionary<int, int>();
        foreach (var token in tokens)
            if (vocabularyIndex.TryGetValue(key: token, value: out var index))
                counts[index] = counts.GetValueOrDefault(index) + 1;

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
        InsertTask(database: temp.Database, taskId: "t1", dimension: "bug_fixing", prompt: "fix the bug");
        InsertResult(database: temp.Database, taskId: "t1", model: "model-a", false, 0.01);

        Assert.Throws<InvalidOperationException>(() => LogRegTrainer.Train(temp.Database));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5)]
    public void Train_NonPositiveLearningRate_Throws(double learningRate)
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(database: temp.Database, taskId: "t1", dimension: "bug_fixing", prompt: "fix the bug");
        InsertResult(database: temp.Database, taskId: "t1", model: "model-a", true, 0.01);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LogRegTrainer.Train(database: temp.Database, learningRate: learningRate));
    }

    [Fact]
    public void Train_NegativeL2Regularization_Throws()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertTask(database: temp.Database, taskId: "t1", dimension: "bug_fixing", prompt: "fix the bug");
        InsertResult(database: temp.Database, taskId: "t1", model: "model-a", true, 0.01);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LogRegTrainer.Train(database: temp.Database, l2Regularization: -0.001));
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
        var method =
            typeof(LogRegTrainer).GetMethod(name: "Sigmoid", bindingAttr: BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LogRegTrainer.Sigmoid was not found via reflection.");

        var result = (double)method.Invoke(null, parameters: [z])!;

        Assert.False(double.IsNaN(result));
        Assert.False(double.IsInfinity(result));
        Assert.Equal(expected: expected, actual: result, 6);
    }

    private static void InsertTask(BenchmarkDatabase database, string taskId, string dimension, string prompt)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
                              VALUES ($taskId, 'test', 'test-bench', $dimension, $rawJson);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$rawJson",
            value: $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
        command.ExecuteNonQuery();
    }

    private static void InsertResult(BenchmarkDatabase database, string taskId, string model, bool resolved,
        double costUsd)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
                              VALUES (
                                  $taskId,
                                  'test',
                                  'test-bench',
                                  (SELECT dimension FROM benchmark_ood_tasks WHERE task_id = $taskId),
                                  $model,
                                  $resolved,
                                  $costUsd);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$resolved", value: resolved ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$costUsd", value: costUsd);
        command.ExecuteNonQuery();
    }
}