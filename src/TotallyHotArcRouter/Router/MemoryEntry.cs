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
/// <param name="IsJudgeScored">
/// Whether <paramref name="Score"/> was produced by the G-Eval judge rather than
/// <see cref="Sandbox.Scoring.VerifierScorer"/>'s structural/execution signals
/// (docs/router/geval-shadow-scoring-plan.md §Provenance). Always <see langword="false"/> through Phase G1
/// and G2 - shadow scores live only in <c>judge_shadow_scores</c>, never here - landed early so every
/// learning consumer (<see cref="Orchestrator.MemoryKnnVoter"/>, the logreg/clustering trainers) can be
/// written against the final schema once, and can weight, discount, or exclude judge-graded rows from the
/// day Phase G3 first writes one.
/// </param>
/// <param name="EmbeddingModel">
/// The identity of the embedding model that produced <paramref name="TaskEmbedding"/>
/// (<see cref="Embeddings.IEmbeddingClient.ModelIdentity"/>), or <see langword="null"/> for an entry
/// written before this provenance existed. Exists because vector length alone cannot detect a swap
/// between two <em>different</em> embedding models that happen to share a dimensionality: the stored and
/// freshly-computed vectors then occupy incomparable coordinate spaces while every length guard passes,
/// so kNN retrieval and both trainers would silently blend meaningless numbers. See
/// <see cref="MatchesEmbeddingModel"/> for how null is interpreted.
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
    string? Dimension = null,
    bool IsJudgeScored = false,
    string? EmbeddingModel = null)
{
    /// <summary>
    /// Whether <see cref="TaskEmbedding"/> is comparable to a vector freshly produced by the embedding
    /// client identified by <paramref name="currentModelIdentity"/> - the guard every consumer of stored
    /// embeddings applies before using this entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A null <see cref="EmbeddingModel"/> matches anything, deliberately.</b> Rows written before the
    /// column existed carry no identity, and the only honest reading of them is the one that is also
    /// almost always true: they were produced by whatever embedding model the installation is still
    /// configured with, because nothing had changed it. Treating null as a <em>mismatch</em> would
    /// silently discard every existing installation's entire accumulated corpus on the first startup
    /// after upgrading - a data loss far worse than the rare case it would protect against, and one the
    /// operator would have no way to see coming. This is the same reasoning
    /// <c>RouterMemoryDatabase.MigrateProvenanceColumns</c> applies to its own backfilled defaults:
    /// default to how the pre-existing rows actually behaved.
    /// </para>
    /// <para>
    /// The vector-length check that consumers already perform remains the backstop for the loud half of
    /// this problem (a dimension change); this identity check exists for the silent half (a same-dimension
    /// model swap), which no length comparison can detect.
    /// </para>
    /// </remarks>
    /// <param name="currentModelIdentity">The identity reported by the live <see cref="Embeddings.IEmbeddingClient.ModelIdentity"/>.</param>
    public bool MatchesEmbeddingModel(string currentModelIdentity) =>
        EmbeddingModel is null || string.Equals(EmbeddingModel, currentModelIdentity, StringComparison.Ordinal);
}
