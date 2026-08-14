using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// The <c>logreg</c> voter's offline training step (PLAN.md Phase L): reads the CodeRouterBench probing
/// split from a synced <see cref="BenchmarkDatabase"/>, builds a fixed TF-IDF vocabulary, and trains one
/// one-vs-rest logistic-regression classifier per model via plain batch gradient descent - no external ML
/// package, matching the phase's "training and inference both in .NET" requirement.
/// </summary>
/// <remarks>
/// <para>
/// <b>To reproduce a real model:</b> sync the corpus (Governance → Benchmark Data, the
/// <c>sync_benchmark_data</c> MCP tool, or <c>TotallyHotArcRouter --sync-benchmark-data</c>), then call
/// <see cref="Train"/> against the resulting <see cref="BenchmarkDatabase"/> and write the result through
/// <see cref="LogRegModelArtifactSerializer.Serialize"/> to
/// <c>src/TotallyHotArcRouter/CodeRouterBench/Resources/logreg_voter_model.json</c>, replacing the checked-in
/// placeholder. <c>src/TotallyHotArcRouter.Tests/CodeRouterBench/LogRegTrainerReconciliationTests.cs</c> exercises
/// exactly this path (self-skipping when the corpus isn't synced, mirroring
/// <c>CodeRouterBenchTable10ReconciliationTests</c>) and doubles as the runnable reproduction recipe - see its
/// <c>Train_OnRealCorpus_ProducesAUsableArtifact</c> test for the exact call.
/// </para>
/// <para>
/// <b>Labeling:</b> each task's label is the model with the highest observed score for that task in the
/// requested split (an intra-task argmax over <c>benchmark_id_results</c>, joined to
/// <c>benchmark_id_tasks</c> by <c>task_id</c>). <b>Task text:</b> extracted from each task row's
/// <c>raw_json</c> under the <c>prompt</c> property - the field name OOD task records are documented to
/// carry (docs/router/coderouterbench-sqlite-migration-plan.md); a task row missing that property is
/// skipped rather than failing the whole run, since the schema is "verbatim JSON preserved, not a pinned
/// column set" by design.
/// </para>
/// </remarks>
public static class LogRegTrainer
{
    /// <summary>
    /// Trains a <see cref="LogRegModelArtifact"/> from <paramref name="database"/>'s <paramref name="split"/>.
    /// </summary>
    /// <param name="database">The synced CodeRouterBench corpus to train from.</param>
    /// <param name="split">The <c>split</c> value to train on - <c>"probing"</c> by default, per PLAN.md Phase L.</param>
    /// <param name="vocabularySize">The vocabulary size, by descending document frequency.</param>
    /// <param name="epochs">The number of full passes over the training set per class.</param>
    /// <param name="learningRate">The gradient-descent step size.</param>
    /// <param name="l2Regularization">The L2 penalty applied to non-bias weights each epoch.</param>
    /// <returns>A trained, non-placeholder <see cref="LogRegModelArtifact"/>.</returns>
    /// <exception cref="InvalidOperationException">The split has no usable (task, label) pairs to train from.</exception>
    public static LogRegModelArtifact Train(
        BenchmarkDatabase database,
        string split = "probing",
        int vocabularySize = 2000,
        int epochs = 50,
        double learningRate = 0.5,
        double l2Regularization = 0.001)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(vocabularySize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(epochs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(learningRate, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(l2Regularization);

        if (!File.Exists(database.DatabasePath))
        {
            throw new InvalidOperationException(
                $"The CodeRouterBench corpus database was not found at '{database.DatabasePath}' - is the corpus synced?");
        }

        var examples = LoadTrainingExamples(database, split);
        if (examples.Count == 0)
        {
            throw new InvalidOperationException(
                $"No (task text, label) pairs could be built from split '{split}' - is the corpus synced?");
        }

        var (vocabulary, idf) = BuildVocabulary(examples, vocabularySize);
        var vocabularyIndex = BuildIndex(vocabulary);
        var features = examples
            .Select(example => ComputeTfIdf(example.Text, vocabularyIndex, idf))
            .ToList();

        var classes = examples.Select(e => e.Label).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var classWeights = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var modelClass in classes)
        {
            var labels = examples.Select(e => e.Label == modelClass ? 1.0 : 0.0).ToArray();
            classWeights[modelClass] = TrainOneVsRest(features, labels, vocabulary.Count, epochs, learningRate, l2Regularization);
        }

        return new LogRegModelArtifact(
            vocabulary,
            idf,
            classWeights,
            IsPlaceholder: false,
            TrainedFrom: $"split='{split}', tasks={examples.Count}, vocabulary={vocabulary.Count}, classes={classes.Count}, trained {DateTimeOffset.UtcNow:O}");
    }

    private sealed record TrainingExample(string Text, string Label);

    private static List<TrainingExample> LoadTrainingExamples(BenchmarkDatabase database, string split)
    {
        using var connection = database.OpenConnection();

        var taskText = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var tasksCommand = connection.CreateCommand())
        {
            tasksCommand.CommandText = "SELECT task_id, raw_json FROM benchmark_id_tasks WHERE split = $split;";
            tasksCommand.Parameters.AddWithValue("$split", split);
            using var reader = tasksCommand.ExecuteReader();
            while (reader.Read())
            {
                var taskId = reader.GetString(0);
                var rawJson = reader.GetString(1);
                var text = TryExtractPrompt(rawJson);
                if (text is not null)
                {
                    taskText[taskId] = text;
                }
            }
        }

        var bestScorePerTask = new Dictionary<string, (string Model, double Score)>(StringComparer.Ordinal);
        using (var resultsCommand = connection.CreateCommand())
        {
            resultsCommand.CommandText = "SELECT task_id, model, score FROM benchmark_id_results WHERE split = $split;";
            resultsCommand.Parameters.AddWithValue("$split", split);
            using var reader = resultsCommand.ExecuteReader();
            while (reader.Read())
            {
                var taskId = reader.GetString(0);
                var model = ModelNameCanonicalizer.Canonicalize(reader.GetString(1));
                var score = reader.GetDouble(2);

                if (!bestScorePerTask.TryGetValue(taskId, out var current) || score > current.Score)
                {
                    bestScorePerTask[taskId] = (model, score);
                }
            }
        }

        var examples = new List<TrainingExample>();
        foreach (var (taskId, text) in taskText)
        {
            if (bestScorePerTask.TryGetValue(taskId, out var best))
            {
                examples.Add(new TrainingExample(text, best.Model));
            }
        }

        return examples;
    }

    private static string? TryExtractPrompt(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
            {
                var text = prompt.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }
        catch (JsonException)
        {
            // A malformed raw_json row is excluded from training rather than failing the whole run.
        }

        return null;
    }

    private static (IReadOnlyList<string> Vocabulary, IReadOnlyList<double> Idf) BuildVocabulary(
        List<TrainingExample> examples,
        int vocabularySize)
    {
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var example in examples)
        {
            foreach (var token in LogRegTextTokenizer.Tokenize(example.Text).Distinct(StringComparer.Ordinal))
            {
                documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
            }
        }

        var vocabulary = documentFrequency
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Take(vocabularySize)
            .Select(kvp => kvp.Key)
            .ToList();

        var documentCount = examples.Count;
        var idf = vocabulary
            .Select(term => Math.Log((double)documentCount / (1 + documentFrequency[term])) + 1.0)
            .ToList();

        return (vocabulary, idf);
    }

    private static Dictionary<string, int> BuildIndex(IReadOnlyList<string> vocabulary)
    {
        var index = new Dictionary<string, int>(vocabulary.Count, StringComparer.Ordinal);
        for (var i = 0; i < vocabulary.Count; i++)
        {
            index[vocabulary[i]] = i;
        }

        return index;
    }

    private static (int Index, double Value)[] ComputeTfIdf(
        string text,
        IReadOnlyDictionary<string, int> vocabularyIndex,
        IReadOnlyList<double> idf)
    {
        var tokens = LogRegTextTokenizer.Tokenize(text);
        if (tokens.Count == 0)
        {
            return [];
        }

        var counts = new Dictionary<int, int>();
        foreach (var token in tokens)
        {
            if (vocabularyIndex.TryGetValue(token, out var index))
            {
                counts[index] = counts.GetValueOrDefault(index) + 1;
            }
        }

        return [.. counts.Select(kvp => (kvp.Key, (double)kvp.Value / tokens.Count * idf[kvp.Key]))];
    }

    /// <summary>
    /// Trains one binary logistic-regression weight vector via batch gradient descent over sparse TF-IDF
    /// features, L2-regularized on every weight except the bias.
    /// </summary>
    private static double[] TrainOneVsRest(
        IReadOnlyList<(int Index, double Value)[]> features,
        double[] labels,
        int vocabularySize,
        int epochs,
        double learningRate,
        double l2Regularization)
    {
        var weights = new double[vocabularySize + 1];
        var n = features.Count;

        for (var epoch = 0; epoch < epochs; epoch++)
        {
            var gradient = new double[vocabularySize + 1];

            for (var i = 0; i < n; i++)
            {
                var z = weights[0];
                foreach (var (index, value) in features[i])
                {
                    z += weights[index + 1] * value;
                }

                var prediction = Sigmoid(z);
                var error = prediction - labels[i];

                gradient[0] += error;
                foreach (var (index, value) in features[i])
                {
                    gradient[index + 1] += error * value;
                }
            }

            weights[0] -= learningRate * gradient[0] / n;
            for (var w = 1; w < weights.Length; w++)
            {
                weights[w] -= learningRate * ((gradient[w] / n) + (l2Regularization * weights[w]));
            }
        }

        return weights;
    }

    // The naive 1/(1+exp(-z)) form overflows exp(-z) to Infinity for very negative z, which combined
    // elsewhere with a zero can produce NaN. Evaluating exp() on -|z| instead keeps its argument
    // non-positive, so it can only underflow to 0 (safe), never overflow to Infinity.
    private static double Sigmoid(double z) => z >= 0
        ? 1.0 / (1.0 + Math.Exp(-z))
        : Math.Exp(z) / (1.0 + Math.Exp(z));
}
