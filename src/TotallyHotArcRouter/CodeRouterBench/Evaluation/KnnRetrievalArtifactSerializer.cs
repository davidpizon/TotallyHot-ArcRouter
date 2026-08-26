using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// JSON (de)serialization for <see cref="KnnRetrievalArtifact"/>. A separate DTO is used because the
/// record's <see cref="IReadOnlyList{T}"/> members don't round-trip through <c>System.Text.Json</c>'s
/// default record support without one — the same shape <see cref="LogRegModelArtifactSerializer"/> and
/// <see cref="Router.Orchestrator.EmbeddingLogRegModelArtifactSerializer"/> use for their own artifacts.
/// </summary>
public static class KnnRetrievalArtifactSerializer
{
    /// <summary>Serializer options shared by <see cref="Serialize"/> and <see cref="Deserialize"/>: indented so a manually inspected artifact file stays human-readable.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    /// <summary>Serializes <paramref name="artifact"/> to indented JSON.</summary>
    /// <param name="artifact">The artifact to serialize.</param>
    public static string Serialize(KnnRetrievalArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var dto = new Dto
        {
            EmbeddingDimension = artifact.EmbeddingDimension,
            EmbeddingModel = artifact.EmbeddingModel,
            Entries = [.. artifact.Entries.Select(entry => new EntryDto
            {
                TaskId = entry.TaskId,
                Embedding = [.. entry.Embedding],
                Label = entry.Label,
            })],
            TrainedFrom = artifact.TrainedFrom,
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Deserializes an artifact previously produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">The JSON document text.</param>
    /// <exception cref="FormatException">The document is not a valid serialized artifact.</exception>
    public static KnnRetrievalArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var dto = JsonSerializer.Deserialize<Dto>(json, Options)
            ?? throw new FormatException("The kNN retrieval baseline artifact document deserialized to null.");

        var artifact = new KnnRetrievalArtifact(
            dto.EmbeddingDimension,
            dto.EmbeddingModel,
            [.. dto.Entries.Select(entry => new KnnRetrievalEntry(entry.TaskId, entry.Embedding, entry.Label))],
            dto.TrainedFrom);
        Validate(artifact);
        return artifact;
    }

    /// <summary>
    /// Validates <paramref name="artifact"/>'s structural invariants — a positive embedding dimension, at
    /// least one entry, every entry's embedding matching that dimension with only finite values, and
    /// every entry's task id unique. Shared by <see cref="Deserialize"/> and
    /// <see cref="KnnRetrievalBaseline"/>'s constructor, since the latter can receive an artifact built
    /// directly by <see cref="KnnRetrievalIndexBuilder"/> rather than through <see cref="Deserialize"/> —
    /// either path must reject a malformed artifact before <see cref="KnnRetrievalBaseline.Route"/> can
    /// misbehave on it.
    /// </summary>
    /// <param name="artifact">The artifact to validate.</param>
    /// <exception cref="FormatException">The artifact violates one of the invariants above.</exception>
    public static void Validate(KnnRetrievalArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (artifact.EmbeddingDimension <= 0)
        {
            throw new FormatException(
                $"The kNN retrieval baseline artifact's embeddingDimension is {artifact.EmbeddingDimension}, must be positive.");
        }

        if (artifact.Entries is null || artifact.Entries.Count == 0)
        {
            throw new FormatException("The kNN retrieval baseline artifact has no entries.");
        }

        var seenTaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in artifact.Entries)
        {
            if (entry is null)
            {
                throw new FormatException("The kNN retrieval baseline artifact contains a null entry.");
            }

            if (!seenTaskIds.Add(entry.TaskId))
            {
                throw new FormatException($"The kNN retrieval baseline artifact contains duplicate task id '{entry.TaskId}'.");
            }

            if (entry.Embedding is null || entry.Embedding.Count != artifact.EmbeddingDimension)
            {
                throw new FormatException(
                    $"The kNN retrieval baseline artifact's entry for task '{entry.TaskId}' has embedding length " +
                    $"{entry.Embedding?.Count.ToString() ?? "null"}, expected {artifact.EmbeddingDimension}.");
            }

            if (entry.Embedding.Any(value => !float.IsFinite(value)))
            {
                throw new FormatException(
                    $"The kNN retrieval baseline artifact's entry for task '{entry.TaskId}' contains a non-finite embedding value (NaN or Infinity).");
            }
        }
    }

    /// <summary>The wire shape for <see cref="KnnRetrievalArtifact"/>.</summary>
    private sealed class Dto
    {
        /// <summary>Gets or sets the embedding dimension every entry's vector must match.</summary>
        [JsonPropertyName("embeddingDimension")]
        public int EmbeddingDimension { get; set; }

        /// <summary>Gets or sets the identity of the embedding model that produced every entry's vector.</summary>
        [JsonPropertyName("embeddingModel")]
        public string EmbeddingModel { get; set; } = string.Empty;

        /// <summary>Gets or sets the indexed OOD tasks.</summary>
        [JsonPropertyName("entries")]
        public List<EntryDto> Entries { get; set; } = [];

        /// <summary>Gets or sets the human-readable build provenance string.</summary>
        [JsonPropertyName("trainedFrom")]
        public string TrainedFrom { get; set; } = string.Empty;
    }

    /// <summary>The wire shape for one <see cref="KnnRetrievalEntry"/>.</summary>
    private sealed class EntryDto
    {
        /// <summary>Gets or sets the corpus's <c>task_id</c>.</summary>
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = string.Empty;

        /// <summary>Gets or sets the task's unit-normalized embedding vector.</summary>
        [JsonPropertyName("embedding")]
        public List<float> Embedding { get; set; } = [];

        /// <summary>Gets or sets the canonicalized model id that resolved the task most cheaply.</summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;
    }
}
