namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The <c>kNN Retrieval</c> baseline's frozen index (research-doc Table 4, N4): every OOD task's
/// embedding and winning-model label, built once offline by <see cref="KnnRetrievalIndexBuilder"/> so
/// <see cref="KnnRetrievalBaseline.Route"/> never calls an embedding client during replay — it can only
/// ever look up a query task's own precomputed entry, never embed arbitrary text.
/// </summary>
/// <remarks>
/// <b>Deviation from Table 4's literal "frozen probing-set embedding index."</b> The probing split
/// publishes no task text (only <c>task_id</c>/<c>split</c>/<c>dimension</c>), the same constraint that
/// forced <see cref="LogRegTrainer"/> onto the 176-task OOD split instead of the probing split Table 4
/// names for the static-classifier family. This index is therefore built and queried entirely within the
/// OOD split (<see cref="KnnRetrievalBaseline"/> excludes a query task from its own neighbor search —
/// leave-one-out), an honest reconstruction rather than an exact reproduction, per
/// docs/router/regret-evaluation-harness-plan.md N4.
/// </remarks>
/// <param name="EmbeddingDimension">The dimension every entry's <see cref="KnnRetrievalEntry.Embedding"/> must match.</param>
/// <param name="EmbeddingModel">
/// The identity of the embedding client that produced every entry's vector
/// (<see cref="Router.Embeddings.IEmbeddingClient.ModelIdentity"/>), so a consumer can refuse to mix an
/// index built by one embedding model with a query embedded by another.
/// </param>
/// <param name="Entries">Every indexed OOD task, keyed implicitly by <see cref="KnnRetrievalEntry.TaskId"/> — ids must be unique.</param>
/// <param name="TrainedFrom">A human-readable provenance string: the split, task count, embedding model, and build date.</param>
public sealed record KnnRetrievalArtifact(
    int EmbeddingDimension,
    string EmbeddingModel,
    IReadOnlyList<KnnRetrievalEntry> Entries,
    string TrainedFrom);
