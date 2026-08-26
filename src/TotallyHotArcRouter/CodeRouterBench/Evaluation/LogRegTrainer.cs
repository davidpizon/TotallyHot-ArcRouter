using System.Text.Json;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Phase N's static <c>logreg</c> comparison baseline (research-doc Table 4): reads the CodeRouterBench
/// OOD split from a synced <see cref="BenchmarkDatabase"/>, builds a fixed TF-IDF vocabulary, and trains
/// one one-vs-rest logistic-regression classifier per model via plain batch gradient descent - no external
/// ML package, matching PLAN.md Phase L's "training and inference both in .NET" requirement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why OOD, not the ID splits:</b> this trainer originally targeted the probing split, but
/// <c>id_probing_tasks.jsonl</c>/<c>id_test_tasks.jsonl</c> publish only <c>task_id</c>/<c>split</c>/
/// <c>source_split</c>/<c>dimension</c> - no task text, for every one of their 9,999 rows. That is a
/// permanent property of the released dataset, not a sync defect (see
/// docs/router/live-feedback-learning-plan.md's "What we are actually able to train on"). The 176-task OOD
/// split is the only source that publishes a <c>prompt</c> field, so it is the only corpus this trainer can
/// build TF-IDF features from. <b>Recorded deferral:</b> research-doc Table 4's LogReg baseline is TF-IDF
/// over probing-split text; since that text isn't published, this is an honest reconstruction trained on the
/// 176 OOD prompts instead, not an exact reproduction - callers should label results accordingly rather than
/// implying parity.
/// </para>
/// <para>
/// <b>To reproduce:</b> sync the corpus (Governance → Benchmark Data, the <c>sync_benchmark_data</c> MCP
/// tool, or <c>TotallyHotArcRouter --sync-benchmark-data</c>), then call <see cref="Train"/> against the
/// resulting <see cref="BenchmarkDatabase"/>.
/// <c>src/TotallyHotArcRouter.Tests/CodeRouterBench/LogRegTrainerReconciliationTests.cs</c> exercises exactly
/// this path (self-skipping when the corpus isn't synced, mirroring
/// <c>CodeRouterBenchTable10ReconciliationTests</c>) and doubles as the runnable reproduction recipe - see its
/// <c>Train_OnRealCorpus_ProducesAUsableArtifact</c> test for the exact call.
/// </para>
/// <para>
/// <b>Labeling:</b> each task's label is the model that resolved it (<c>benchmark_ood_results.resolved = 1</c>),
/// tie-broken by the lowest <c>cost_usd</c> among the models that resolved it; a task no model resolved has no
/// well-defined winner and is excluded rather than assigned an arbitrary label. <b>Task text:</b> extracted
/// from each OOD task row's <c>raw_json</c> under the <c>prompt</c> property; a row missing that property is
/// skipped rather than failing the whole run, since the schema is "verbatim JSON preserved, not a pinned
/// column set" by design.
/// </para>
/// </remarks>
public static class LogRegTrainer
{
    /// <summary>
    /// Trains a <see cref="LogRegModelArtifact"/> from <paramref name="database"/>'s OOD split - the only
    /// split CodeRouterBench publishes task text for.
    /// </summary>
    /// <param name="database">The synced CodeRouterBench corpus to train from.</param>
    /// <param name="vocabularySize">The vocabulary size, by descending document frequency.</param>
    /// <param name="epochs">The number of full passes over the training set per class.</param>
    /// <param name="learningRate">The gradient-descent step size.</param>
    /// <param name="l2Regularization">The L2 penalty applied to non-bias weights each epoch.</param>
    /// <returns>A trained, non-placeholder <see cref="LogRegModelArtifact"/>.</returns>
    /// <exception cref="InvalidOperationException">The OOD split has no usable (task, label) pairs to train from.</exception>
    public static LogRegModelArtifact Train(
        BenchmarkDatabase database,
        int vocabularySize = 2000,
        int epochs = 50,
        double learningRate = 0.5,
        double l2Regularization = 0.001)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(vocabularySize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(epochs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(learningRate, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(l2Regularization);

        if (!File.Exists(database.DatabasePath))
        {
            throw new InvalidOperationException(
                $"The CodeRouterBench corpus database was not found at '{database.DatabasePath}' - is the corpus synced?");
        }

        var examples = LoadOodTrainingExamples(database);
        if (examples.Count == 0)
        {
            throw new InvalidOperationException(
                "No (task text, label) pairs could be built from the OOD split - is the corpus synced, " +
                "and does at least one model resolve at least one OOD task?");
        }

        var (vocabulary, idf) = BuildVocabulary(examples, vocabularySize);
        var vocabularyIndex = BuildVocabularyIndex(vocabulary);
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
            TrainedFrom: $"split='ood', tasks={examples.Count}, vocabulary={vocabulary.Count}, classes={classes.Count}, trained {DateTimeOffset.UtcNow:O}");
    }

    /// <summary>
    /// Builds the (task id, text, label) training set by joining each OOD task's extracted prompt with the
    /// cheapest model that resolved it, per <see cref="Train"/>'s labeling rule. Internal (not private) so
    /// <see cref="KnnRetrievalIndexBuilder"/> shares this exact loading and labeling logic rather than
    /// duplicating it - both baselines train from the same 176-task OOD split under the same "cheapest
    /// resolver wins" label rule.
    /// </summary>
    /// <param name="database">The synced CodeRouterBench corpus to read the OOD split from.</param>
    /// <returns>Every task with both extractable prompt text and at least one resolving model.</returns>
    internal static IReadOnlyList<OodTrainingExample> LoadOodTrainingExamples(BenchmarkDatabase database)
    {
        using var connection = database.OpenConnection();

        var taskText = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var tasksCommand = connection.CreateCommand())
        {
            tasksCommand.CommandText = "SELECT task_id, raw_json FROM benchmark_ood_tasks;";
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

        // The winning label per task: the resolving model with the lowest cost, among models that
        // resolved it. A task no model resolved has no well-defined winner and is left out of
        // bestResolverPerTask entirely, so LoadTrainingExamples' join below naturally excludes it.
        var bestResolverPerTask = new Dictionary<string, (string Model, double CostUsd)>(StringComparer.Ordinal);
        using (var resultsCommand = connection.CreateCommand())
        {
            resultsCommand.CommandText =
                "SELECT task_id, model, cost_usd FROM benchmark_ood_results WHERE resolved = 1;";
            using var reader = resultsCommand.ExecuteReader();
            while (reader.Read())
            {
                var taskId = reader.GetString(0);
                var model = ModelNameCanonicalizer.Canonicalize(reader.GetString(1));
                var costUsd = reader.IsDBNull(2) ? double.PositiveInfinity : reader.GetDouble(2);

                // Ties (equal cost, including two NULL-cost resolvers) break on canonicalized model name
                // so the winner never depends on SQLite's unspecified row enumeration order.
                if (!bestResolverPerTask.TryGetValue(taskId, out var current) ||
                    costUsd < current.CostUsd ||
                    (costUsd == current.CostUsd && string.CompareOrdinal(model, current.Model) < 0))
                {
                    bestResolverPerTask[taskId] = (model, costUsd);
                }
            }
        }

        var examples = new List<OodTrainingExample>();
        foreach (var (taskId, text) in taskText)
        {
            if (bestResolverPerTask.TryGetValue(taskId, out var best))
            {
                examples.Add(new OodTrainingExample(taskId, text, best.Model));
            }
        }

        return examples;
    }

    /// <summary>
    /// Extracts an OOD task's <c>prompt</c> string property from its raw JSON, tolerating malformed or
    /// missing content. Internal (not private) so <see cref="Router.Orchestrator.OodBootstrapSampleSource"/>
    /// (docs/router/live-feedback-learning-plan.md Phase 4a) shares this exact extraction logic rather than
    /// duplicating it - both read the same <c>benchmark_ood_tasks.raw_json</c> shape.
    /// </summary>
    /// <param name="rawJson">The task row's verbatim raw JSON.</param>
    /// <returns>The prompt text, or <see langword="null"/> when absent, blank, not a string, or the JSON fails to parse.</returns>
    internal static string? TryExtractPrompt(string rawJson)
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

    /// <summary>
    /// Selects the top <paramref name="vocabularySize"/> terms by descending document frequency (ties
    /// broken ordinally, for a deterministic vocabulary) and computes each term's inverse document
    /// frequency.
    /// </summary>
    /// <param name="examples">The training examples to derive term statistics from.</param>
    /// <param name="vocabularySize">The maximum number of terms to keep.</param>
    /// <returns>The selected vocabulary terms and their parallel IDF values.</returns>
    private static (IReadOnlyList<string> Vocabulary, IReadOnlyList<double> Idf) BuildVocabulary(
        IReadOnlyList<OodTrainingExample> examples,
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

    /// <summary>
    /// Builds a term-to-position lookup for a vocabulary, so feature vectors can be assembled by index
    /// rather than by repeated linear search. Internal (not private) so <see cref="LogRegBaseline"/> can
    /// build the same lookup once at construction from a deserialized artifact's vocabulary, rather than
    /// duplicating this indexing logic for inference.
    /// </summary>
    /// <param name="vocabulary">The vocabulary terms, in their fixed feature order.</param>
    /// <returns>A dictionary mapping each term to its index in <paramref name="vocabulary"/>.</returns>
    internal static Dictionary<string, int> BuildVocabularyIndex(IReadOnlyList<string> vocabulary)
    {
        var index = new Dictionary<string, int>(vocabulary.Count, StringComparer.Ordinal);
        for (var i = 0; i < vocabulary.Count; i++)
        {
            index[vocabulary[i]] = i;
        }

        return index;
    }

    /// <summary>
    /// Computes a sparse TF-IDF feature vector for one document, restricted to terms present in
    /// <paramref name="vocabularyIndex"/> - out-of-vocabulary tokens contribute nothing, matching a fixed
    /// vocabulary's usual inference-time behavior.
    /// </summary>
    /// <param name="text">The document text to featurize.</param>
    /// <param name="vocabularyIndex">The vocabulary's term-to-index lookup.</param>
    /// <param name="idf">The vocabulary's parallel inverse-document-frequency values.</param>
    /// <returns>The document's nonzero (feature index, TF-IDF value) pairs.</returns>
    /// <remarks>
    /// Internal (not private) so <see cref="LogRegBaseline"/> reuses this exact featurization at inference
    /// time - training and inference must compute TF-IDF identically for a fixed vocabulary's weights to
    /// mean the same thing at both ends, the same reason <see cref="LogRegTextTokenizer"/> is shared rather
    /// than reimplemented.
    /// </remarks>
    internal static (int Index, double Value)[] ComputeTfIdf(
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

    /// <summary>
    /// Computes the logistic sigmoid of <paramref name="z"/>. The naive 1/(1+exp(-z)) form overflows
    /// exp(-z) to Infinity for very negative z, which combined elsewhere with a zero can produce NaN.
    /// Evaluating exp() on -|z| instead keeps its argument non-positive, so it can only underflow to 0
    /// (safe), never overflow to Infinity.
    /// </summary>
    /// <param name="z">The linear score to squash into <c>(0, 1)</c>.</param>
    /// <returns>The sigmoid of <paramref name="z"/>.</returns>
    private static double Sigmoid(double z) => z >= 0
        ? 1.0 / (1.0 + Math.Exp(-z))
        : Math.Exp(z) / (1.0 + Math.Exp(z));
}
