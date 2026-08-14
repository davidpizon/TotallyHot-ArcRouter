using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// JSON (de)serialization for <see cref="LogRegModelArtifact"/>. A separate DTO is used because
/// <see cref="LogRegModelArtifact"/>'s <see cref="IReadOnlyDictionary{TKey,TValue}"/>/<see cref="IReadOnlyList{T}"/>
/// members don't round-trip through <c>System.Text.Json</c>'s default record support without one.
/// </summary>
public static class LogRegModelArtifactSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    /// <summary>Serializes <paramref name="artifact"/> to indented JSON.</summary>
    /// <param name="artifact">The artifact to serialize.</param>
    public static string Serialize(LogRegModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var dto = new Dto
        {
            Vocabulary = [.. artifact.Vocabulary],
            InverseDocumentFrequency = [.. artifact.InverseDocumentFrequency],
            ClassWeights = artifact.ClassWeights.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            IsPlaceholder = artifact.IsPlaceholder,
            TrainedFrom = artifact.TrainedFrom,
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Deserializes an artifact previously produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">The JSON document text.</param>
    /// <exception cref="FormatException">The document is not a valid serialized artifact.</exception>
    public static LogRegModelArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var dto = JsonSerializer.Deserialize<Dto>(json, Options)
            ?? throw new FormatException("The logreg voter model document deserialized to null.");

        var artifact = new LogRegModelArtifact(
            dto.Vocabulary,
            dto.InverseDocumentFrequency,
            dto.ClassWeights,
            dto.IsPlaceholder,
            dto.TrainedFrom);
        Validate(artifact);
        return artifact;
    }

    /// <summary>
    /// Validates <paramref name="artifact"/>'s structural invariants - vocabulary non-empty and
    /// duplicate-free, <c>InverseDocumentFrequency</c> matching the vocabulary length with only finite
    /// values, and every class weight vector matching <c>Vocabulary.Count + 1</c> (bias + one per term)
    /// with only finite values. Shared by <see cref="Deserialize"/> and <see cref="LogRegVoter"/>'s
    /// model-artifact constructor, since the latter can receive an artifact built directly rather than
    /// through <see cref="Deserialize"/> - either path must reject a malformed artifact before it can
    /// throw <see cref="IndexOutOfRangeException"/> later during scoring.
    /// </summary>
    /// <param name="artifact">The artifact to validate.</param>
    /// <exception cref="FormatException">The artifact violates one of the invariants above.</exception>
    public static void Validate(LogRegModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (artifact.Vocabulary.Count == 0)
        {
            throw new FormatException("The logreg voter model document has an empty vocabulary.");
        }

        if (artifact.Vocabulary.Distinct(StringComparer.Ordinal).Count() != artifact.Vocabulary.Count)
        {
            throw new FormatException("The logreg voter model document's vocabulary contains duplicate terms.");
        }

        if (artifact.InverseDocumentFrequency.Count != artifact.Vocabulary.Count)
        {
            throw new FormatException(
                $"The logreg voter model document's inverseDocumentFrequency has length " +
                $"{artifact.InverseDocumentFrequency.Count}, expected {artifact.Vocabulary.Count} (one per vocabulary term).");
        }

        if (artifact.InverseDocumentFrequency.Any(value => !double.IsFinite(value)))
        {
            throw new FormatException(
                "The logreg voter model document's inverseDocumentFrequency contains a non-finite value (NaN or Infinity).");
        }

        foreach (var (model, weights) in artifact.ClassWeights)
        {
            if (weights.Length != artifact.Vocabulary.Count + 1)
            {
                throw new FormatException(
                    $"The logreg voter model's weight vector for '{model}' has length {weights.Length}, " +
                    $"expected {artifact.Vocabulary.Count + 1} (vocabulary size + 1 bias term).");
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
        [JsonPropertyName("vocabulary")]
        public List<string> Vocabulary { get; set; } = [];

        [JsonPropertyName("inverseDocumentFrequency")]
        public List<double> InverseDocumentFrequency { get; set; } = [];

        [JsonPropertyName("classWeights")]
        public Dictionary<string, double[]> ClassWeights { get; set; } = [];

        [JsonPropertyName("isPlaceholder")]
        public bool IsPlaceholder { get; set; }

        [JsonPropertyName("trainedFrom")]
        public string TrainedFrom { get; set; } = string.Empty;
    }
}
