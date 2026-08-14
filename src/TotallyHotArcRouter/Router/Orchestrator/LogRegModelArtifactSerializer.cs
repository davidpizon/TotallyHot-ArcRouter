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

        if (dto.Vocabulary.Count == 0)
        {
            throw new FormatException("The logreg voter model document has an empty vocabulary.");
        }

        if (dto.Vocabulary.Distinct(StringComparer.Ordinal).Count() != dto.Vocabulary.Count)
        {
            throw new FormatException("The logreg voter model document's vocabulary contains duplicate terms.");
        }

        if (dto.InverseDocumentFrequency.Count != dto.Vocabulary.Count)
        {
            throw new FormatException(
                $"The logreg voter model document's inverseDocumentFrequency has length " +
                $"{dto.InverseDocumentFrequency.Count}, expected {dto.Vocabulary.Count} (one per vocabulary term).");
        }

        foreach (var (model, weights) in dto.ClassWeights)
        {
            if (weights.Length != dto.Vocabulary.Count + 1)
            {
                throw new FormatException(
                    $"The logreg voter model's weight vector for '{model}' has length {weights.Length}, " +
                    $"expected {dto.Vocabulary.Count + 1} (vocabulary size + 1 bias term).");
            }
        }

        return new LogRegModelArtifact(
            dto.Vocabulary,
            dto.InverseDocumentFrequency,
            dto.ClassWeights,
            dto.IsPlaceholder,
            dto.TrainedFrom);
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
