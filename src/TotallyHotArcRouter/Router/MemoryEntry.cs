using TotallyHot.ArcRouter.Models;

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
/// <param name="IsExploratory">
/// Whether the routing decision that chose <paramref name="ChosenModel"/> was an epsilon-greedy
/// exploratory pick rather than the policy's normal choice, mirroring
/// <see cref="Models.RoutingDecision.IsExploratory"/> (docs/router/self-organizing-classification-plan.md
/// Phase T1c). Defaults to <see langword="false"/> for entries written before this provenance existed.
/// </param>
/// <param name="Propensity">
/// The propensity of <paramref name="ChosenModel"/> under the policy's own arm-selection distribution at
/// the time it was chosen, mirroring <see cref="Models.RoutingDecision.Propensity"/>. Defaults to
/// <c>1.0</c> - certain selection - for entries written before this provenance existed.
/// </param>
/// <param name="Dimension">
/// The heuristic classifier's dimension label for this entry's request
/// (<see cref="TotallyHot.ArcRouter.Router.Classification.RequestClassification.Dimension"/>), or <see langword="null"/> for an entry
/// written before this label existed (docs/router/self-organizing-classification-plan.md Phase T2e). Feeds
/// the cluster model's per-cluster heuristic-dimension histogram independently of whether transcript
/// capture is enabled.
/// </param>
public sealed record MemoryEntry(
    long Id,
    float[] TaskEmbedding,
    string ChosenModel,
    double Score,
    double Cost,
    string? VerifierTrace,
    DateTimeOffset CreatedAtUtc,
    bool IsExploratory = false,
    double Propensity = 1.0,
    string? Dimension = null);
