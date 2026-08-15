using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// JSON (de)serialization for <see cref="EmbeddingLogRegModelArtifact"/>. A separate DTO is used because
/// <see cref="EmbeddingLogRegModelArtifact"/>'s <see cref="IReadOnlyDictionary{TKey,TValue}"/> member
/// doesn't round-trip through <c>System.Text.Json</c>'s default record support without one - mirrors
/// <see cref="LogRegModelArtifactSerializer"/>'s shape for the TF-IDF artifact.
/// </summary>
public static class EmbeddingLogRegModelArtifactSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    /// <summary>Serializes <paramref name="artifact"/> to indented JSON.</summary>
    /// <param name="artifact">The artifact to serialize.</param>
    public static string Serialize(EmbeddingLogRegModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var dto = new Dto
        {
            EmbeddingDimension = artifact.EmbeddingDimension,
            ClassWeights = artifact.ClassWeights.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            TrainedFrom = artifact.TrainedFrom,
            BootstrapTaskCount = artifact.BootstrapTaskCount,
            MemoryEntryCount = artifact.MemoryEntryCount,
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Deserializes an artifact previously produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">The JSON document text.</param>
    /// <exception cref="FormatException">The document is not a valid serialized artifact.</exception>
    public static EmbeddingLogRegModelArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var dto = JsonSerializer.Deserialize<Dto>(json, Options)
            ?? throw new FormatException("The logreg voter model document deserialized to null.");

        var artifact = new EmbeddingLogRegModelArtifact(
            dto.EmbeddingDimension,
            dto.ClassWeights,
            dto.TrainedFrom,
            dto.BootstrapTaskCount,
            dto.MemoryEntryCount);
        Validate(artifact);
        return artifact;
    }

    /// <summary>
    /// Validates <paramref name="artifact"/>'s structural invariants - a positive embedding dimension, and
    /// every class weight vector non-null, matching <c>EmbeddingDimension + 1</c> (bias + one per
    /// embedding component), and finite-valued. Shared by <see cref="Deserialize"/> and
    /// <see cref="LogRegVoter"/>'s model-artifact constructor, since the latter can receive an artifact
    /// built directly rather than through <see cref="Deserialize"/> - either path must reject a malformed
    /// artifact before it can throw <see cref="IndexOutOfRangeException"/> later during scoring.
    /// </summary>
    /// <param name="artifact">The artifact to validate.</param>
    /// <exception cref="FormatException">The artifact violates one of the invariants above.</exception>
    public static void Validate(EmbeddingLogRegModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (artifact.EmbeddingDimension <= 0)
        {
            throw new FormatException(
                $"The logreg voter model document's embeddingDimension is {artifact.EmbeddingDimension}, must be positive.");
        }

        if (artifact.ClassWeights is null)
        {
            // A JSON document can carry an explicit null for the whole classWeights property (e.g.
            // "classWeights": null); the Dto's property initializer only covers a missing property, not
            // an explicit null, so this can reach here despite ClassWeights being non-nullable.
            throw new FormatException("The logreg voter model document's classWeights is null.");
        }

        foreach (var (model, weights) in artifact.ClassWeights)
        {
            if (weights is null)
            {
                // A JSON document can carry an explicit null value for a classWeights entry (e.g.
                // "model-a": null); accessing weights.Length below would otherwise throw
                // NullReferenceException instead of a controlled FormatException.
                throw new FormatException($"The logreg voter model's weight vector for '{model}' is null.");
            }

            if (weights.Length != artifact.EmbeddingDimension + 1)
            {
                throw new FormatException(
                    $"The logreg voter model's weight vector for '{model}' has length {weights.Length}, " +
                    $"expected {artifact.EmbeddingDimension + 1} (embedding dimension + 1 bias term).");
            }

            if (weights.Any(value => !double.IsFinite(value)))
            {
                throw new FormatException(
                    $"The logreg voter model's weight vector for '{model}' contains a non-finite value (NaN or Infinity).");
            }
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("embeddingDimension")]
        public int EmbeddingDimension { get; set; }

        [JsonPropertyName("classWeights")]
        public Dictionary<string, double[]> ClassWeights { get; set; } = [];

        [JsonPropertyName("trainedFrom")]
        public string TrainedFrom { get; set; } = string.Empty;

        [JsonPropertyName("bootstrapTaskCount")]
        public int BootstrapTaskCount { get; set; }

        [JsonPropertyName("memoryEntryCount")]
        public int MemoryEntryCount { get; set; }
    }
}
