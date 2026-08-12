namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// One task-embedding-keyed memory record, per research-doc §3.3: a task's embedding, the model chosen
/// for it, the observed outcome, and when it was written. Backs the kNN retrieval that replaces
/// dimension-hashed lookup for PLAN.md Phase J.
/// </summary>
/// <param name="Id">The store-assigned identity, used for FIFO-ordered eviction. Zero for an entry not yet persisted.</param>
/// <param name="TaskEmbedding">The task's embedding vector, unit-normalized by <see cref="Embeddings.IEmbeddingClient"/>.</param>
/// <param name="ChosenModel">The model selected for this task.</param>
/// <param name="Score">The Verifier's observed quality score in [0, 1].</param>
/// <param name="Cost">The monetary cost κ of serving this task with <paramref name="ChosenModel"/>.</param>
/// <param name="VerifierTrace">The Verifier's trace/explanation for <paramref name="Score"/>, if recorded.</param>
/// <param name="CreatedAtUtc">When this entry was written, in UTC.</param>
public sealed record MemoryEntry(
    long Id,
    float[] TaskEmbedding,
    string ChosenModel,
    double Score,
    double Cost,
    string? VerifierTrace,
    DateTimeOffset CreatedAtUtc);
