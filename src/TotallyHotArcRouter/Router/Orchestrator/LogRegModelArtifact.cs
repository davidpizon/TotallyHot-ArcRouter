namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// A trained (or placeholder) <c>logreg</c> voter model: a fixed TF-IDF vocabulary plus one one-vs-rest
/// logistic-regression weight vector per candidate model class, keyed through
/// <see cref="Models.ModelNameCanonicalizer.Canonicalize"/> so the class keys match
/// <see cref="CodeRouterBench.DimensionModelScoreMatrix"/>'s convention for the same reason - the
/// CodeRouterBench dataset's model spellings differ from the router's configured <c>ModelName</c>
/// vocabulary. Serialized to/from <c>CodeRouterBench/Resources/logreg_voter_model.json</c> by
/// <see cref="LogRegVoter"/> (read) and <see cref="CodeRouterBench.LogRegTrainer"/> (write).
/// </summary>
/// <param name="Vocabulary">
/// The fixed vocabulary, in the index order every weight vector and <paramref name="InverseDocumentFrequency"/>
/// entry assumes.
/// </param>
/// <param name="InverseDocumentFrequency">
/// One IDF weight per <paramref name="Vocabulary"/> entry, same index order.
/// </param>
/// <param name="ClassWeights">
/// One weight vector per candidate model class, keyed by <see cref="Models.ModelNameCanonicalizer.Canonicalize"/>
/// of the model id. Each vector has length <c>Vocabulary.Count + 1</c>: index 0 is the class's bias term,
/// indices <c>1..Vocabulary.Count</c> align with <paramref name="Vocabulary"/>.
/// </param>
/// <param name="IsPlaceholder">
/// Whether this artifact is a small, hand-built stand-in rather than a real training run over the synced
/// CodeRouterBench probing split (PLAN.md Phase L's documented data-availability caveat) - never claim a
/// placeholder artifact represents trained accuracy.
/// </param>
/// <param name="TrainedFrom">
/// A human-readable description of how this artifact was produced - the split, task count, vocabulary
/// size, and date for a real training run, or an explanation of the placeholder's provenance.
/// </param>
public sealed record LogRegModelArtifact(
    IReadOnlyList<string> Vocabulary,
    IReadOnlyList<double> InverseDocumentFrequency,
    IReadOnlyDictionary<string, double[]> ClassWeights,
    bool IsPlaceholder,
    string TrainedFrom);
