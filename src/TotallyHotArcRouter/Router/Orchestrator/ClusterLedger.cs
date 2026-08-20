namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// Recomputes the per-(cluster, model) score ledger from <c>memory_entries</c> after every retrain -
/// "ledger-as-view", docs/router/self-organizing-classification-plan.md Phase T2f's answer to cluster
/// drift. Cluster ids are meaningless across retrains (a pile can split or merge), so the ledger is never
/// incrementally owned by a long-lived cluster identity; instead, every entry is re-assigned to its
/// nearest <em>current</em> centroid and aggregated fresh each time this runs. This sidesteps split/merge
/// bookkeeping entirely rather than trying to solve it, at the cost of an O(entries x clusters) pass - cheap
/// at the FIFO-bounded working-set sizes this codebase already caps <see cref="EmbeddingMemory"/> at.
/// </summary>
public static class ClusterLedger
{
    /// <summary>One cluster's aggregated observations for one model.</summary>
    /// <param name="MeanScore">The mean Verifier score across every observation of this model in this cluster.</param>
    /// <param name="ObservationCount">The number of observations behind <see cref="MeanScore"/>.</param>
    public sealed record ClusterModelScore(double MeanScore, int ObservationCount);

    /// <summary>
    /// Assigns every entry in <paramref name="entries"/> to its nearest centroid in <paramref name="artifact"/>
    /// (skipping any entry whose embedding dimension disagrees with the artifact's, or whose best
    /// similarity falls below <paramref name="assignmentThreshold"/>), then aggregates a mean score per
    /// (cluster index, canonicalized model) pair.
    /// </summary>
    /// <param name="artifact">The trained cluster model to assign against.</param>
    /// <param name="entries">The memory entries to aggregate.</param>
    /// <param name="assignmentThreshold">
    /// The minimum cosine similarity an entry's nearest centroid must reach for the entry to be assigned;
    /// an entry below this is excluded from every cluster's ledger, mirroring the "unclustered" abstention
    /// <see cref="TotallyHot.ArcRouter.Router.Orchestrator.ClusterModelArtifact"/>'s eventual voter applies
    /// at vote time (Phase T3).
    /// </param>
    /// <returns>
    /// A map from cluster index to that cluster's per-canonicalized-model <see cref="ClusterModelScore"/>.
    /// A cluster with no assigned entries is present with an empty inner map, not omitted, so a caller can
    /// enumerate every cluster in <paramref name="artifact"/> uniformly.
    /// </returns>
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<string, ClusterModelScore>> Build(
        ClusterModelArtifact artifact, IReadOnlyList<MemoryEntry> entries, double assignmentThreshold = 0.5)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(entries);

        var sums = new Dictionary<string, double>[artifact.Centroids.Count];
        var counts = new Dictionary<string, int>[artifact.Centroids.Count];
        for (var c = 0; c < artifact.Centroids.Count; c++)
        {
            sums[c] = new Dictionary<string, double>(StringComparer.Ordinal);
            counts[c] = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        foreach (var entry in entries)
        {
            if (entry.TaskEmbedding.Length != artifact.EmbeddingDimension)
            {
                continue;
            }

            var (nearest, similarity) = NearestCentroid(entry.TaskEmbedding, artifact.Centroids);
            if (similarity < assignmentThreshold)
            {
                continue;
            }

            var modelKey = Models.ModelNameCanonicalizer.Canonicalize(entry.ChosenModel);
            sums[nearest][modelKey] = sums[nearest].GetValueOrDefault(modelKey) + entry.Score;
            counts[nearest][modelKey] = counts[nearest].GetValueOrDefault(modelKey) + 1;
        }

        var result = new Dictionary<int, IReadOnlyDictionary<string, ClusterModelScore>>();
        for (var c = 0; c < artifact.Centroids.Count; c++)
        {
            var perModel = new Dictionary<string, ClusterModelScore>(StringComparer.Ordinal);
            foreach (var (modelKey, count) in counts[c])
            {
                perModel[modelKey] = new ClusterModelScore(sums[c][modelKey] / count, count);
            }

            result[c] = perModel;
        }

        return result;
    }

    /// <summary>
    /// Returns the index and cosine similarity of the cluster centroid in <paramref name="artifact"/> nearest
    /// <paramref name="embedding"/> - the same assignment rule <see cref="Build"/> uses per training entry,
    /// exposed for a caller (e.g. <see cref="ClusterBestVoter"/>, Phase T3) that must assign one embedding at
    /// vote time rather than aggregate a whole training set.
    /// </summary>
    /// <param name="artifact">The trained cluster model to assign against.</param>
    /// <param name="embedding">The embedding to assign to its nearest centroid.</param>
    public static (int ClusterIndex, double Similarity) AssignNearestCluster(ClusterModelArtifact artifact, float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(embedding);
        return NearestCentroid(embedding, artifact.Centroids);
    }

    /// <summary>
    /// Returns the learned taxonomy's predicted score for one (cluster, model) cell with
    /// <paramref name="observedScore"/> removed from it first - the held-out prediction Phase T4's
    /// mean-absolute-error comparison scores against, and the exact counterpart of
    /// <see cref="DimensionLedger.PredictLeaveOneOut"/> on the other side of that comparison.
    /// </summary>
    /// <param name="cell">The ledger cell the observation landed in, or <see langword="null"/> when the cell has no observations.</param>
    /// <param name="observedScore">The observation to exclude, already aggregated into <paramref name="cell"/> by the time this runs.</param>
    /// <returns>
    /// The mean of the cell's other observations, or <see langword="null"/> when the cell holds only this
    /// one and no honest held-out prediction exists.
    /// </returns>
    /// <remarks>
    /// The contamination this corrects is structural, not incidental: <see cref="Build"/> aggregates the
    /// live <c>memory_entries</c> working set, which by comparison time already contains the very entry
    /// being scored. Both taxonomies are corrected the same way so the comparison stays like-for-like.
    /// </remarks>
    public static double? PredictLeaveOneOut(ClusterModelScore? cell, double observedScore) =>
        cell is null ? null : DimensionLedger.LeaveOneOutMean(cell.MeanScore, cell.ObservationCount, observedScore);

    /// <summary>Returns the index and cosine similarity of the centroid nearest <paramref name="embedding"/>.</summary>
    private static (int Index, double Similarity) NearestCentroid(float[] embedding, IReadOnlyList<float[]> centroids)
    {
        var best = 0;
        var bestSimilarity = double.NegativeInfinity;
        for (var c = 0; c < centroids.Count; c++)
        {
            var similarity = CosineSimilarity(embedding, centroids[c]);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = c;
            }
        }

        return (best, bestSimilarity);
    }

    /// <summary>
    /// Computes cosine similarity between two equal-length vectors. Both embeddings and stored centroids
    /// are already unit-normalized (<see cref="SphericalKMeansTrainer"/>), so this reduces to a plain dot
    /// product, exactly as <see cref="EmbeddingMemory.CosineSimilarity"/> already exploits.
    /// </summary>
    private static double CosineSimilarity(float[] left, float[] right)
    {
        double dot = 0, leftMagnitude = 0, rightMagnitude = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftMagnitude += (double)left[i] * left[i];
            rightMagnitude += (double)right[i] * right[i];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }
}
