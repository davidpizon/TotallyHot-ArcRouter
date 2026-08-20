namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// A trained self-organizing cluster model (docs/router/self-organizing-classification-plan.md Phase
/// T2e): the centroids <see cref="SphericalKMeansTrainer"/> chose, plus enough provenance to explain how
/// they were chosen and to name each cluster for an operator reading the admin surface. Trained
/// per-installation from the operator's own synced OOD corpus and live traffic, and never checked in -
/// mirrors <see cref="EmbeddingLogRegModelArtifact"/>'s lifecycle exactly.
/// </summary>
/// <param name="EmbeddingDimension">
/// The embedding dimension this artifact was trained at. Changing <c>EmbeddingOptions.EmbeddingDimension</c>
/// or the embedding model URL invalidates a trained artifact; a consumer refuses to score a
/// differently-sized embedding and abstains rather than silently misindexing centroids.
/// </param>
/// <param name="Centroids">The unit-normalized cluster centroids, one per cluster, each of length <see cref="EmbeddingDimension"/>.</param>
/// <param name="ChosenK">The number of clusters <see cref="SphericalKMeansTrainer"/>'s sweep selected.</param>
/// <param name="TrainedAtUtc">When this artifact was trained, in UTC.</param>
/// <param name="ClusterSizes">The number of training samples assigned to each cluster, parallel to <see cref="Centroids"/>.</param>
/// <param name="ClusterDimensionHistograms">
/// Per-cluster counts of the heuristic classifier's dimension label across that cluster's member samples
/// (<see cref="ClusterTrainingSample.Dimension"/>), parallel to <see cref="Centroids"/>. Populated
/// independently of transcript capture (<see cref="Router.MemoryEntry.Dimension"/> is tagged going
/// forward regardless), so this histogram is always available even when transcripts are disabled.
/// </param>
/// <param name="ClusterTopTerms">
/// The top TF-IDF-distinguishing terms for each cluster, parallel to <see cref="Centroids"/>, computed only
/// when transcript capture is enabled (transcripts are the only source of prompt text to tokenize). Empty
/// for a given cluster - or for every cluster, when transcripts are disabled - rather than null, so a
/// consumer can always enumerate it uniformly.
/// </param>
/// <param name="TrainedFrom">
/// A human-readable provenance string: source mix (OOD bootstrap task count vs. live memory entry count),
/// the k-selection sweep's outcome (<see cref="SphericalKMeansTrainer.TrainResult.KSelectionProvenance"/>),
/// and the training date.
/// </param>
/// <param name="BootstrapTaskCount">The number of OOD bootstrap tasks (Phase T2d) that contributed to this artifact, or 0 if none.</param>
/// <param name="MemoryEntryCount">The number of live <c>memory_entries</c> rows that contributed to this artifact, or 0 if none.</param>
public sealed record ClusterModelArtifact(
    int EmbeddingDimension,
    IReadOnlyList<float[]> Centroids,
    int ChosenK,
    DateTimeOffset TrainedAtUtc,
    IReadOnlyList<int> ClusterSizes,
    IReadOnlyList<IReadOnlyDictionary<string, int>> ClusterDimensionHistograms,
    IReadOnlyList<IReadOnlyList<string>> ClusterTopTerms,
    string TrainedFrom,
    int BootstrapTaskCount,
    int MemoryEntryCount)
{
    /// <summary>
    /// Returns a human-readable name for cluster <paramref name="clusterIndex"/>, preferring its top TF-IDF
    /// terms (e.g. <c>"mostly bug_fixing: sql, migration, schema"</c>) when available, falling back to the
    /// dominant entry in its dimension histogram alone when transcripts are disabled, and finally to a bare
    /// index when neither signal exists yet.
    /// </summary>
    /// <param name="clusterIndex">The cluster's index into <see cref="Centroids"/>.</param>
    public string DescribeCluster(int clusterIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clusterIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(clusterIndex, Centroids.Count);

        var dominantDimension = ClusterDimensionHistograms[clusterIndex].Count == 0
            ? null
            : ClusterDimensionHistograms[clusterIndex].OrderByDescending(kvp => kvp.Value).First().Key;

        var topTerms = ClusterTopTerms[clusterIndex];
        if (topTerms.Count > 0 && dominantDimension is not null)
        {
            return $"mostly {dominantDimension}: {string.Join(", ", topTerms)}";
        }

        if (dominantDimension is not null)
        {
            return $"mostly {dominantDimension}";
        }

        return $"cluster {clusterIndex}";
    }
}
