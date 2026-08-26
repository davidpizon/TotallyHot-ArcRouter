namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The <c>LogReg</c> baseline (research-doc Table 4, N4): real TF-IDF inference against a
/// <see cref="LogRegModelArtifact"/> trained by <see cref="LogRegTrainer"/> from the CodeRouterBench OOD
/// split — the only split that publishes task text. Argmax over each candidate class's raw one-vs-rest
/// score (monotonic in the sigmoid, so the sigmoid itself is unnecessary at inference), restricted to
/// <see cref="RegretReplayContext.CandidateModelIds"/>.
/// </summary>
/// <remarks>
/// <b>Text-limited.</b> <see cref="Route"/> returns <see langword="null"/> for every task whose
/// <see cref="RegretReplayContext.TaskText"/> is unpublished — every ID-test/probing task, since only OOD
/// carries text (<see cref="LogRegTrainer"/>'s remarks) — which N4's exit criterion reads as "not
/// computable" on that split rather than a routing failure.
/// </remarks>
public sealed class LogRegBaseline : IRegretBaselineRouter
{
    private readonly LogRegModelArtifact _artifact;
    private readonly IReadOnlyDictionary<string, int> _vocabularyIndex;

    /// <summary>Initializes a new instance of the <see cref="LogRegBaseline"/> class.</summary>
    /// <param name="artifact">
    /// The trained TF-IDF artifact, e.g. from <see cref="LogRegTrainer.Train"/> or
    /// <see cref="LogRegModelArtifactSerializer.Deserialize"/>. Validated here so a malformed artifact
    /// (mismatched vocabulary/weight lengths) fails fast at construction rather than throwing
    /// <see cref="IndexOutOfRangeException"/> from inside <see cref="Route"/>.
    /// </param>
    public LogRegBaseline(LogRegModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        LogRegModelArtifactSerializer.Validate(artifact);

        _artifact = artifact;
        _vocabularyIndex = LogRegTrainer.BuildVocabularyIndex(artifact.Vocabulary);
    }

    /// <inheritdoc />
    public string Name => "logreg";

    /// <inheritdoc />
    /// <remarks>
    /// Ties are broken by ordinal model-id order, matching every other baseline's tie-break convention.
    /// Returns <see langword="null"/> when <see cref="RegretReplayContext.TaskText"/> is unpublished (see
    /// this type's remarks), or when none of this task's candidates ever appeared as a training class.
    /// </remarks>
    public string? Route(RegretReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TaskText is null)
        {
            return null;
        }

        var features = LogRegTrainer.ComputeTfIdf(context.TaskText, _vocabularyIndex, _artifact.InverseDocumentFrequency);

        return context.CandidateModelIds
            .Where(id => _artifact.ClassWeights.ContainsKey(id))
            .Select(id => (Model: id, Score: ScoreClass(_artifact.ClassWeights[id], features)))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Model, StringComparer.Ordinal)
            .Select(entry => (string?)entry.Model)
            .FirstOrDefault();
    }

    /// <summary>Computes one class's raw one-vs-rest linear score for a sparse TF-IDF feature vector.</summary>
    /// <param name="weights">The class's weight vector: index 0 is the bias, indices 1.. align with the vocabulary.</param>
    /// <param name="features">The document's nonzero (feature index, TF-IDF value) pairs.</param>
    private static double ScoreClass(double[] weights, (int Index, double Value)[] features)
    {
        var z = weights[0];
        foreach (var (index, value) in features)
        {
            z += weights[index + 1] * value;
        }

        return z;
    }
}
