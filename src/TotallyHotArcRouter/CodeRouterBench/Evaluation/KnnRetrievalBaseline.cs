namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The <c>kNN Retrieval</c> baseline (research-doc Table 4, N4): majority vote over the
/// <see cref="K"/> nearest neighbors (by embedding cosine similarity) of the query task in a frozen
/// <see cref="KnnRetrievalArtifact"/> index, restricted to <see cref="RegretReplayContext.CandidateModelIds"/>.
/// </summary>
/// <remarks>
/// <b>No live embedding calls, ever.</b> Every OOD task is both a member of the frozen index and,
/// exactly once, the query — so <see cref="Route"/> looks up the query's own precomputed entry by
/// <see cref="RegretReplayContext.TaskId"/> rather than embedding <see cref="RegretReplayContext.TaskText"/>
/// itself, and excludes that entry from its own neighbor search (leave-one-out). This is what lets the
/// baseline satisfy the harness's "no live API calls" replay property (see
/// <see cref="KnnRetrievalIndexBuilder"/>'s remarks) while still doing real nearest-neighbor retrieval.
/// <b>Text-limited.</b> A task outside the frozen OOD index — every ID-test/probing task — has no entry to
/// look up, so <see cref="Route"/> returns <see langword="null"/> for it, the same "not computable" signal
/// <see cref="LogRegBaseline"/> reports on those splits.
/// </remarks>
public sealed class KnnRetrievalBaseline : IRegretBaselineRouter
{
    private readonly IReadOnlyList<KnnRetrievalEntry> _entries;
    private readonly IReadOnlyDictionary<string, KnnRetrievalEntry> _entriesByTaskId;

    /// <summary>Initializes a new instance of the <see cref="KnnRetrievalBaseline"/> class.</summary>
    /// <param name="artifact">
    /// The frozen index, e.g. from <see cref="KnnRetrievalIndexBuilder.BuildAsync"/> or
    /// <see cref="KnnRetrievalArtifactSerializer.Deserialize"/>.
    /// </param>
    /// <param name="k">The number of nearest neighbors to vote over. Must be positive.</param>
    public KnnRetrievalBaseline(KnnRetrievalArtifact artifact, int k = 5)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: k, 0);
        KnnRetrievalArtifactSerializer.Validate(artifact);

        _entries = artifact.Entries;
        _entriesByTaskId =
            artifact.Entries.ToDictionary(keySelector: entry => entry.TaskId, comparer: StringComparer.Ordinal);
        K = k;
    }

    /// <summary>Gets the number of nearest neighbors this baseline votes over.</summary>
    public int K { get; }

    /// <inheritdoc/>
    public string Name => "knn_retrieval";

    /// <inheritdoc/>
    /// <remarks>
    /// Votes are counted per candidate label among the <see cref="K"/> nearest neighbors (highest cosine
    /// similarity first, leave-one-out), restricted to neighbors whose label is in
    /// <see cref="RegretReplayContext.CandidateModelIds"/>; ties break first by summed similarity, then by
    /// ordinal model id. Returns <see langword="null"/> when the query task has no entry in the frozen
    /// index (see this type's remarks), or when none of the <see cref="K"/> nearest neighbors' labels are
    /// in this task's candidate pool.
    /// </remarks>
    public string? Route(RegretReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_entriesByTaskId.TryGetValue(key: context.TaskId, value: out var query)) return null;

        return _entries
            .Where(entry => !string.Equals(a: entry.TaskId, b: query.TaskId, comparisonType: StringComparison.Ordinal))
            .Select(entry => (Entry: entry, Similarity: DotProduct(a: query.Embedding, b: entry.Embedding)))
            .OrderByDescending(pair => pair.Similarity)
            .ThenBy(keySelector: pair => pair.Entry.TaskId, comparer: StringComparer.Ordinal)
            .Take(K)
            .Where(pair => context.CandidateModelIds.Contains(pair.Entry.Label))
            .GroupBy(keySelector: pair => pair.Entry.Label, comparer: StringComparer.Ordinal)
            .Select(group => (Model: group.Key, Votes: group.Count(),
                TotalSimilarity: group.Sum(pair => pair.Similarity)))
            .OrderByDescending(candidate => candidate.Votes)
            .ThenByDescending(candidate => candidate.TotalSimilarity)
            .ThenBy(keySelector: candidate => candidate.Model, comparer: StringComparer.Ordinal)
            .Select(candidate => (string?)candidate.Model)
            .FirstOrDefault();
    }

    /// <summary>
    /// The plain dot product of two embeddings — equal to their cosine similarity because
    /// <see cref="Router.Embeddings.EmbeddingResult.Vector"/> is already unit-normalized.
    /// </summary>
    private static double DotProduct(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Count; i++) sum += a[i] * b[i];

        return sum;
    }
}