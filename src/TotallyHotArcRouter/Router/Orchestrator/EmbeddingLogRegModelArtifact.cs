namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// A trained embedding-backed <c>logreg</c> voter model (docs/router/live-feedback-learning-plan.md
/// Phase 3): one one-vs-rest logistic-regression weight vector per candidate model class, scored as a
/// dense dot product against the task embedding rather than the sparse TF-IDF walk
/// <see cref="CodeRouterBench.Evaluation.LogRegModelArtifact"/> used. Trained per-installation from the operator's own
/// synced corpus
/// and live traffic (docs/router/live-feedback-learning-plan.md Phase 4) and never checked in - the
/// TF-IDF placeholder this replaces has been deleted outright rather than kept checked in, since an
/// absent embedding-backed artifact is already a normal, honest state (<see cref="LogRegVoter"/> simply
/// abstains) rather than something to ship a stand-in for.
/// </summary>
/// <param name="EmbeddingDimension">
/// The embedding dimension this artifact was trained at (e.g. BGE-large's 1024). Changing
/// <c>EmbeddingOptions.EmbeddingDimension</c> invalidates a trained artifact;
/// <see cref="LogRegVoter"/> refuses to score a differently-sized embedding and abstains rather than
/// silently misindexing weights. A change of embedding <em>model</em> at the same dimension is caught by
/// <see cref="EmbeddingModel"/> instead - this field cannot see it.
/// </param>
/// <param name="ClassWeights">
/// One weight vector per candidate model class, keyed by <see cref="Models.ModelNameCanonicalizer.Canonicalize"/>
/// of the model id - the same keying convention <see cref="CodeRouterBench.Evaluation.LogRegModelArtifact"/> used. Each
/// vector has
/// length <see cref="EmbeddingDimension"/> + 1: index 0 is the class's bias term, indices
/// <c>1..EmbeddingDimension</c> align with the embedding's own components.
/// </param>
/// <param name="TrainedFrom">
/// A human-readable provenance string: which sources contributed (OOD bootstrap, live memory, or both -
/// see Phase 4's blend weighting), row counts, and the training date. Live rows are drawn from a sliding
/// 20,000-entry FIFO window, never "all history" - the provenance string says so rather than implying
/// otherwise.
/// </param>
/// <param name="BootstrapTaskCount">
/// The number of OOD bootstrap tasks (Phase 4a) that contributed to this artifact, or 0
/// if none.
/// </param>
/// <param name="MemoryEntryCount">
/// The number of live <c>memory_entries</c> rows (Phase 4b) that contributed to this
/// artifact, or 0 if none. Under a judge-row policy of <see cref="Models.JudgeRowPolicy.Exclude"/> this
/// is smaller than the store's raw row count at training time - see <see cref="TotalLiveMemoryEntryCount"/>
/// for the count comparable to <c>IMemoryEntryStore.LoadAllAsync</c>'s raw total.
/// </param>
/// <param name="EmbeddingModel">
/// The identity of the embedding model whose vectors this artifact was fitted against
/// (<see cref="Router.Embeddings.IEmbeddingClient.ModelIdentity"/>), or <see langword="null"/> for an
/// artifact trained before this provenance existed. <see cref="EmbeddingDimension"/> alone cannot make
/// the invalidation guarantee this type's own documentation claims: two different embedding models
/// frequently share a dimensionality, and swapping between them leaves every length check passing while
/// the weights below refer to a coordinate space that no longer exists. A consumer compares this against
/// the live client and abstains on a mismatch, exactly as it does for a dimension mismatch.
/// </param>
/// <param name="TotalLiveMemoryEntryCount">
/// The raw <c>memory_entries</c> row count observed at training time, before any judge-row policy
/// filtering - the watermark <see cref="Hosting.LogRegRetrainHostedService"/> compares against the
/// store's current raw row count to decide whether enough new rows have accumulated to retrain.
/// Comparing against <see cref="MemoryEntryCount"/> instead would be wrong under
/// <see cref="Models.JudgeRowPolicy.Exclude"/>: that count omits policy-excluded rows, so it can never
/// catch up to the raw store total once excluded rows accumulate, triggering a retrain on every poll.
/// Defaults to 0 for an artifact trained before this field existed.
/// </param>
public sealed record EmbeddingLogRegModelArtifact(
    int EmbeddingDimension,
    IReadOnlyDictionary<string, double[]> ClassWeights,
    string TrainedFrom,
    int BootstrapTaskCount,
    int MemoryEntryCount,
    string? EmbeddingModel = null,
    int TotalLiveMemoryEntryCount = 0);