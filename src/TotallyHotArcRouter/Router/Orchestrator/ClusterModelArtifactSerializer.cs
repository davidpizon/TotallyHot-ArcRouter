using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// JSON (de)serialization for <see cref="ClusterModelArtifact"/>, mirroring
/// <see cref="EmbeddingLogRegModelArtifactSerializer"/>'s shape: a separate DTO plus explicit validation,
/// so a malformed or hand-edited artifact is rejected with a controlled <see cref="FormatException"/>
/// rather than an unhandled exception during scoring.
/// </summary>
public static class ClusterModelArtifactSerializer
{
    /// <summary>Serializer options shared by <see cref="Serialize"/> and <see cref="Deserialize"/>: indented so a manually inspected artifact file stays human-readable.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    /// <summary>Serializes <paramref name="artifact"/> to indented JSON.</summary>
    /// <param name="artifact">The artifact to serialize.</param>
    public static string Serialize(ClusterModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var dto = new Dto
        {
            EmbeddingDimension = artifact.EmbeddingDimension,
            Centroids = [.. artifact.Centroids],
            ChosenK = artifact.ChosenK,
            TrainedAtUtc = artifact.TrainedAtUtc,
            ClusterSizes = [.. artifact.ClusterSizes],
            ClusterDimensionHistograms = [.. artifact.ClusterDimensionHistograms.Select(h => new Dictionary<string, int>(h))],
            ClusterTopTerms = [.. artifact.ClusterTopTerms.Select(t => (List<string>)[.. t])],
            TrainedFrom = artifact.TrainedFrom,
            BootstrapTaskCount = artifact.BootstrapTaskCount,
            MemoryEntryCount = artifact.MemoryEntryCount,
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Deserializes an artifact previously produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">The JSON document text.</param>
    /// <exception cref="FormatException">The document is not a valid serialized artifact.</exception>
    public static ClusterModelArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var dto = JsonSerializer.Deserialize<Dto>(json, Options)
            ?? throw new FormatException("The cluster model document deserialized to null.");

        var artifact = new ClusterModelArtifact(
            dto.EmbeddingDimension,
            dto.Centroids,
            dto.ChosenK,
            dto.TrainedAtUtc,
            dto.ClusterSizes,
            [.. dto.ClusterDimensionHistograms.Select(h => (IReadOnlyDictionary<string, int>)h)],
            [.. dto.ClusterTopTerms.Select(t => (IReadOnlyList<string>)t)],
            dto.TrainedFrom,
            dto.BootstrapTaskCount,
            dto.MemoryEntryCount);
        Validate(artifact);
        return artifact;
    }

    /// <summary>
    /// Validates <paramref name="artifact"/>'s structural invariants: a positive embedding dimension, at
    /// least one centroid, every centroid non-null and matching <see cref="ClusterModelArtifact.EmbeddingDimension"/>
    /// with finite-valued components, and every per-cluster list (<see cref="ClusterModelArtifact.ClusterSizes"/>,
    /// <see cref="ClusterModelArtifact.ClusterDimensionHistograms"/>, <see cref="ClusterModelArtifact.ClusterTopTerms"/>)
    /// the same length as <see cref="ClusterModelArtifact.Centroids"/>. Shared by <see cref="Deserialize"/>
    /// and any trainer that builds an artifact directly, since either path must reject a malformed artifact
    /// before it can throw an unhandled exception later during scoring.
    /// </summary>
    /// <param name="artifact">The artifact to validate.</param>
    /// <exception cref="FormatException">The artifact violates one of the invariants above.</exception>
    public static void Validate(ClusterModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (artifact.EmbeddingDimension <= 0)
        {
            throw new FormatException(
                $"The cluster model document's embeddingDimension is {artifact.EmbeddingDimension}, must be positive.");
        }

        if (artifact.Centroids is null || artifact.Centroids.Count == 0)
        {
            throw new FormatException("The cluster model document's centroids is null or empty.");
        }

        foreach (var centroid in artifact.Centroids)
        {
            if (centroid is null)
            {
                throw new FormatException("The cluster model document contains a null centroid.");
            }

            if (centroid.Length != artifact.EmbeddingDimension)
            {
                throw new FormatException(
                    $"A centroid has length {centroid.Length}, expected {artifact.EmbeddingDimension}.");
            }

            if (centroid.Any(value => !float.IsFinite(value)))
            {
                throw new FormatException("The cluster model document contains a non-finite centroid value (NaN or Infinity).");
            }
        }

        var k = artifact.Centroids.Count;
        if (artifact.ClusterSizes is null || artifact.ClusterSizes.Count != k)
        {
            throw new FormatException(
                $"clusterSizes has {artifact.ClusterSizes?.Count ?? 0} entr(y/ies), expected {k} to match centroids.");
        }

        if (artifact.ClusterDimensionHistograms is null || artifact.ClusterDimensionHistograms.Count != k)
        {
            throw new FormatException(
                $"clusterDimensionHistograms has {artifact.ClusterDimensionHistograms?.Count ?? 0} entr(y/ies), expected {k} to match centroids.");
        }

        if (artifact.ClusterTopTerms is null || artifact.ClusterTopTerms.Count != k)
        {
            throw new FormatException(
                $"clusterTopTerms has {artifact.ClusterTopTerms?.Count ?? 0} entr(y/ies), expected {k} to match centroids.");
        }
    }

    /// <summary>The wire shape for <see cref="ClusterModelArtifact"/>.</summary>
    private sealed class Dto
    {
        /// <summary>Gets or sets the embedding dimension the artifact was trained at.</summary>
        [JsonPropertyName("embeddingDimension")]
        public int EmbeddingDimension { get; set; }

        /// <summary>Gets or sets the unit-normalized cluster centroids.</summary>
        [JsonPropertyName("centroids")]
        public List<float[]> Centroids { get; set; } = [];

        /// <summary>Gets or sets the number of clusters the k-sweep selected.</summary>
        [JsonPropertyName("chosenK")]
        public int ChosenK { get; set; }

        /// <summary>Gets or sets when this artifact was trained, in UTC.</summary>
        [JsonPropertyName("trainedAtUtc")]
        public DateTimeOffset TrainedAtUtc { get; set; }

        /// <summary>Gets or sets the per-cluster training sample counts.</summary>
        [JsonPropertyName("clusterSizes")]
        public List<int> ClusterSizes { get; set; } = [];

        /// <summary>Gets or sets the per-cluster heuristic-dimension histograms.</summary>
        [JsonPropertyName("clusterDimensionHistograms")]
        public List<Dictionary<string, int>> ClusterDimensionHistograms { get; set; } = [];

        /// <summary>Gets or sets the per-cluster top TF-IDF-distinguishing terms.</summary>
        [JsonPropertyName("clusterTopTerms")]
        public List<List<string>> ClusterTopTerms { get; set; } = [];

        /// <summary>Gets or sets the human-readable training provenance string.</summary>
        [JsonPropertyName("trainedFrom")]
        public string TrainedFrom { get; set; } = string.Empty;

        /// <summary>Gets or sets the number of OOD bootstrap tasks that contributed to training.</summary>
        [JsonPropertyName("bootstrapTaskCount")]
        public int BootstrapTaskCount { get; set; }

        /// <summary>Gets or sets the number of live memory entries that contributed to training.</summary>
        [JsonPropertyName("memoryEntryCount")]
        public int MemoryEntryCount { get; set; }
    }
}
