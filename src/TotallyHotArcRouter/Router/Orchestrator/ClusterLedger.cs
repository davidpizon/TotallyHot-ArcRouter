using TotallyHot.ArcRouter.Models;

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
    /// <param name="judgeRowPolicy">
    /// How a judge-scored entry (<see cref="MemoryEntry.IsJudgeScored"/>) contributes to the ledger's mean
    /// (docs/router/geval-shadow-scoring-plan.md's G3 "still owed" item). Defaults to
    /// <see cref="Models.JudgeRowPolicy.Include"/> so every existing caller that does not pass this
    /// explicitly keeps today's byte-identical behavior; production callers pass the operator's configured
    /// <see cref="Models.RoutingOptions.JudgeScoredRowPolicy"/>.
    /// </param>
    /// <param name="judgeRowWeight">
    /// The multiplier <see cref="Models.JudgeRowPolicy.DownWeight"/> applies; ignored otherwise. See
    /// <see cref="Models.RoutingOptions.JudgeScoredRowWeight"/>.
    /// </param>
    /// <returns>
    /// A map from cluster index to that cluster's per-canonicalized-model <see cref="ClusterModelScore"/>.
    /// A cluster with no assigned entries is present with an empty inner map, not omitted, so a caller can
    /// enumerate every cluster in <paramref name="artifact"/> uniformly. <see cref="ClusterModelScore.ObservationCount"/>
    /// counts entries retained by the judge-row policy (excluded entries are not counted) - it answers
    /// "how many actual observations support this cell", which <see cref="RoutingOptions.ClusterBestMinObservations"/>'s
    /// floor uses directly; <see cref="ClusterModelScore.MeanScore"/> and <see cref="ClusterModelScore.WeightTotal"/>
    /// account for the policy's weight multipliers.
    /// </returns>
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<string, ClusterModelScore>> Build(
        ClusterModelArtifact artifact, IReadOnlyList<MemoryEntry> entries, double assignmentThreshold = 0.5,
        JudgeRowPolicy judgeRowPolicy = JudgeRowPolicy.Include, double judgeRowWeight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(entries);

        var weightedSums = new Dictionary<string, double>[artifact.Centroids.Count];
        var weightTotals = new Dictionary<string, double>[artifact.Centroids.Count];
        var counts = new Dictionary<string, int>[artifact.Centroids.Count];
        for (var c = 0; c < artifact.Centroids.Count; c++)
        {
            weightedSums[c] = new(StringComparer.Ordinal);
            weightTotals[c] = new(StringComparer.Ordinal);
            counts[c] = new(StringComparer.Ordinal);
        }

        foreach (var entry in entries)
        {
            if (entry.TaskEmbedding.Length != artifact.EmbeddingDimension) continue;

            var (nearest, similarity) = NearestCentroid(embedding: entry.TaskEmbedding, centroids: artifact.Centroids);
            if (similarity < assignmentThreshold) continue;

            var weight = JudgeRowWeighting.ResolveWeight(isJudgeScored: entry.IsJudgeScored, policy: judgeRowPolicy,
                judgeRowWeight: judgeRowWeight);
            if (weight is null) continue;

            var modelKey = ModelNameCanonicalizer.Canonicalize(entry.ChosenModel);
            weightedSums[nearest][modelKey] = weightedSums[nearest].GetValueOrDefault(modelKey) + entry.Score * weight.Value;
            weightTotals[nearest][modelKey] = weightTotals[nearest].GetValueOrDefault(modelKey) + weight.Value;
            counts[nearest][modelKey] = counts[nearest].GetValueOrDefault(modelKey) + 1;
        }

        var result = new Dictionary<int, IReadOnlyDictionary<string, ClusterModelScore>>();
        for (var c = 0; c < artifact.Centroids.Count; c++)
        {
            var perModel = new Dictionary<string, ClusterModelScore>(StringComparer.Ordinal);
            foreach (var (modelKey, count) in counts[c])
                perModel[modelKey] = new ClusterModelScore(
                    MeanScore: weightedSums[c][modelKey] / weightTotals[c][modelKey],
                    ObservationCount: count,
                    WeightTotal: weightTotals[c][modelKey]);

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
    public static (int ClusterIndex, double Similarity) AssignNearestCluster(ClusterModelArtifact artifact,
        float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(embedding);
        return NearestCentroid(embedding: embedding, centroids: artifact.Centroids);
    }

    /// <summary>
    /// Returns the learned taxonomy's predicted score for one (cluster, model) cell with
    /// <paramref name="observedScore"/> removed from it first - the held-out prediction Phase T4's
    /// mean-absolute-error comparison scores against, and the exact counterpart of
    /// <see cref="DimensionLedger.PredictLeaveOneOut"/> on the other side of that comparison.
    /// </summary>
    /// <param name="cell">
    /// The ledger cell the observation landed in, or <see langword="null"/> when the cell has no
    /// observations.
    /// </param>
    /// <param name="observedScore">
    /// The observation to exclude, already aggregated into <paramref name="cell"/> by the time
    /// this runs.
    /// </param>
    /// <param name="observedScoreWeight">
    /// The effective weight of <paramref name="observedScore"/> under the policy in effect when
    /// <paramref name="cell"/> was built (1.0 for non-judge rows or when weights don't apply,
    /// or a configured multiplier). Defaults to 1.0 for calls that don't account for weighting.
    /// </param>
    /// <returns>
    /// The mean of the cell's other observations, or <see langword="null"/> when the cell holds only this
    /// one and no honest held-out prediction exists.
    /// </returns>
    /// <remarks>
    /// The contamination this corrects is structural, not incidental: <see cref="Build"/> aggregates the
    /// live <c>memory_entries</c> working set, which by comparison time already contains the very entry
    /// being scored. Both taxonomies are corrected the same way so the comparison stays like-for-like.
    /// When <paramref name="cell"/> was built with weighted means (due to judge-row policy),
    /// <paramref name="observedScoreWeight"/> must match the weight that score received in the aggregation.
    /// </remarks>
    public static double? PredictLeaveOneOut(ClusterModelScore? cell, double observedScore, double observedScoreWeight = 1.0)
    {
        if (cell is null) return null;

        // If only one effective observation (weight-wise) contributed, no leave-one-out is possible
        var remainingWeight = cell.WeightTotal - observedScoreWeight;
        if (remainingWeight <= 0) return null;

        // Recover weighted sum from mean*weight, then subtract the held-out contribution
        var weightedSum = cell.MeanScore * cell.WeightTotal;
        var remainingWeightedSum = weightedSum - observedScore * observedScoreWeight;
        return remainingWeightedSum / remainingWeight;
    }

    /// <summary>Returns the index and cosine similarity of the centroid nearest <paramref name="embedding"/>.</summary>
    private static (int Index, double Similarity) NearestCentroid(float[] embedding, IReadOnlyList<float[]> centroids)
    {
        var best = 0;
        var bestSimilarity = double.NegativeInfinity;
        for (var c = 0; c < centroids.Count; c++)
        {
            var similarity = CosineSimilarity(left: embedding, right: centroids[c]);
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

        if (leftMagnitude <= 0 || rightMagnitude <= 0) return 0;

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    /// <summary>
    /// One (cluster, model) cell's aggregated score from the ledger. <see cref="MeanScore"/> is weighted
    /// when judge-row policies apply; <see cref="ObservationCount"/> always counts raw entries regardless of
    /// policy (answers "how many data points"); and <see cref="WeightTotal"/> stores the sum of weights for
    /// correct leave-one-out prediction with weighted means.
    /// </summary>
    /// <param name="MeanScore">The (possibly weighted) mean score across every observation of this model in this cluster.</param>
    /// <param name="ObservationCount">The unweighted count of observations that contributed to this cell.</param>
    /// <param name="WeightTotal">The sum of weights applied to all observations in this cell.</param>
    public sealed record ClusterModelScore(double MeanScore, int ObservationCount, double WeightTotal);
}