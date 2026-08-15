namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// A trained embedding-backed <c>logreg</c> voter model (docs/router/live-feedback-learning-plan.md
/// Phase 3): one one-vs-rest logistic-regression weight vector per candidate model class, scored as a
/// dense dot product against the task embedding rather than the sparse TF-IDF walk
/// <see cref="LogRegModelArtifact"/> used. Trained per-installation from the operator's own synced corpus
/// and live traffic (docs/router/live-feedback-learning-plan.md Phase 4) and never checked in - unlike
/// the deleted TF-IDF placeholder this replaces, an absent artifact is a normal, honest state
/// (<see cref="LogRegVoter"/> simply abstains) rather than something to ship a stand-in for.
/// </summary>
/// <param name="EmbeddingDimension">
/// The embedding dimension this artifact was trained at (e.g. BGE-large's 1024). Changing
/// <c>EmbeddingOptions.EmbeddingDimension</c> or the model URL invalidates a trained artifact;
/// <see cref="LogRegVoter"/> refuses to score a differently-sized embedding and abstains rather than
/// silently misindexing weights.
/// </param>
/// <param name="ClassWeights">
/// One weight vector per candidate model class, keyed by <see cref="Models.ModelNameCanonicalizer.Canonicalize"/>
/// of the model id - the same keying convention <see cref="LogRegModelArtifact"/> used. Each vector has
/// length <see cref="EmbeddingDimension"/> + 1: index 0 is the class's bias term, indices
/// <c>1..EmbeddingDimension</c> align with the embedding's own components.
/// </param>
/// <param name="TrainedFrom">
/// A human-readable provenance string: which sources contributed (OOD bootstrap, live memory, or both -
/// see Phase 4's blend weighting), row counts, and the training date. Live rows are drawn from a sliding
/// 20,000-entry FIFO window, never "all history" - the provenance string says so rather than implying
/// otherwise.
/// </param>
/// <param name="BootstrapTaskCount">The number of OOD bootstrap tasks (Phase 4a) that contributed to this artifact, or 0 if none.</param>
/// <param name="MemoryEntryCount">The number of live <c>memory_entries</c> rows (Phase 4b) that contributed to this artifact, or 0 if none.</param>
public sealed record EmbeddingLogRegModelArtifact(
    int EmbeddingDimension,
    IReadOnlyDictionary<string, double[]> ClassWeights,
    string TrainedFrom,
    int BootstrapTaskCount,
    int MemoryEntryCount);
