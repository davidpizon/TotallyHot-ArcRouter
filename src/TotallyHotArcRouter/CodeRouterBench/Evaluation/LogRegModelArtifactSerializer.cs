using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// JSON (de)serialization for <see cref="LogRegModelArtifact"/>. A separate DTO is used because
/// <see cref="LogRegModelArtifact"/>'s <see cref="IReadOnlyDictionary{TKey,TValue}"/>/<see cref="IReadOnlyList{T}"/>
/// members don't round-trip through <c>System.Text.Json</c>'s default record support without one.
/// </summary>
public static class LogRegModelArtifactSerializer
{
    /// <summary>
    /// Serializer options shared by <see cref="Serialize"/> and <see cref="Deserialize"/>: indented so a manually
    /// inspected artifact file stays human-readable.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
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
            ClassWeights =
                artifact.ClassWeights.ToDictionary(keySelector: kvp => kvp.Key, elementSelector: kvp => kvp.Value),
            IsPlaceholder = artifact.IsPlaceholder,
            TrainedFrom = artifact.TrainedFrom
        };

        return JsonSerializer.Serialize(value: dto, options: Options);
    }

    /// <summary>Deserializes an artifact previously produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">The JSON document text.</param>
    /// <exception cref="FormatException">The document is not a valid serialized artifact.</exception>
    public static LogRegModelArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var dto = JsonSerializer.Deserialize<Dto>(json: json, options: Options)
                  ?? throw new FormatException("The logreg voter model document deserialized to null.");

        var artifact = new LogRegModelArtifact(
            Vocabulary: dto.Vocabulary,
            InverseDocumentFrequency: dto.InverseDocumentFrequency,
            ClassWeights: dto.ClassWeights,
            IsPlaceholder: dto.IsPlaceholder,
            TrainedFrom: dto.TrainedFrom);
        Validate(artifact);
        return artifact;
    }

    /// <summary>
    /// Validates <paramref name="artifact"/>'s structural invariants - vocabulary non-empty and
    /// duplicate-free, <c>InverseDocumentFrequency</c> matching the vocabulary length with only finite
    /// values, and every class weight vector matching <c>Vocabulary.Count + 1</c> (bias + one per term)
    /// with only finite values. Shared by <see cref="Deserialize"/> and <see cref="LogRegBaseline"/>'s
    /// constructor, since the latter can receive an artifact built directly by <see cref="LogRegTrainer"/>
    /// rather than through <see cref="Deserialize"/> - either path must reject a malformed artifact before
    /// it can throw <see cref="IndexOutOfRangeException"/> later during scoring.
    /// </summary>
    /// <param name="artifact">The artifact to validate.</param>
    /// <exception cref="FormatException">The artifact violates one of the invariants above.</exception>
    public static void Validate(LogRegModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (artifact.Vocabulary.Count == 0)
            throw new FormatException("The logreg voter model document has an empty vocabulary.");

        if (artifact.Vocabulary.Any(string.IsNullOrWhiteSpace))
            // A null/blank term would later become a Dictionary<string, int> key in LogRegVoter's
            // vocabulary index, which throws ArgumentNullException (null) or silently collides with every
            // other blank entry (whitespace) - reject it here as a format error instead.
            throw new FormatException("The logreg voter model document's vocabulary contains a null or blank term.");

        if (artifact.Vocabulary.Distinct(StringComparer.Ordinal).Count() != artifact.Vocabulary.Count)
            throw new FormatException("The logreg voter model document's vocabulary contains duplicate terms.");

        if (artifact.InverseDocumentFrequency.Count != artifact.Vocabulary.Count)
            throw new FormatException(
                $"The logreg voter model document's inverseDocumentFrequency has length " +
                $"{artifact.InverseDocumentFrequency.Count}, expected {artifact.Vocabulary.Count} (one per vocabulary term).");

        if (artifact.InverseDocumentFrequency.Any(value => !double.IsFinite(value)))
            throw new FormatException(
                "The logreg voter model document's inverseDocumentFrequency contains a non-finite value (NaN or Infinity).");

        foreach (var (model, weights) in artifact.ClassWeights)
        {
            if (weights is null)
                // A JSON document can carry an explicit null value for a classWeights entry (e.g.
                // "model-a": null); accessing weights.Length below would otherwise throw
                // NullReferenceException instead of a controlled FormatException.
                throw new FormatException($"The logreg voter model's weight vector for '{model}' is null.");

            if (weights.Length != artifact.Vocabulary.Count + 1)
                throw new FormatException(
                    $"The logreg voter model's weight vector for '{model}' has length {weights.Length}, " +
                    $"expected {artifact.Vocabulary.Count + 1} (vocabulary size + 1 bias term).");

            if (weights.Any(value => !double.IsFinite(value)))
                throw new FormatException(
                    $"The logreg voter model's weight vector for '{model}' contains a non-finite value (NaN or Infinity).");
        }
    }

    /// <summary>
    /// The wire shape for <see cref="LogRegModelArtifact"/>, needed because the record's
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/>/<see cref="IReadOnlyList{T}"/> members don't round-trip through
    /// <c>System.Text.Json</c>'s default record support.
    /// </summary>
    private sealed class Dto
    {
        /// <summary>Gets or sets the fixed TF-IDF vocabulary, in index order.</summary>
        [JsonPropertyName("vocabulary")]
        public List<string> Vocabulary { get; set; } = [];

        /// <summary>Gets or sets the per-term inverse document frequency weights, same index order as <see cref="Vocabulary"/>.</summary>
        [JsonPropertyName("inverseDocumentFrequency")]
        public List<double> InverseDocumentFrequency { get; set; } = [];

        /// <summary>Gets or sets the per-class weight vectors, keyed by canonicalized model id.</summary>
        [JsonPropertyName("classWeights")]
        public Dictionary<string, double[]> ClassWeights { get; set; } = [];

        /// <summary>Gets or sets whether this artifact is a hand-built placeholder rather than a real training run.</summary>
        [JsonPropertyName("isPlaceholder")]
        public bool IsPlaceholder { get; set; }

        /// <summary>Gets or sets the human-readable training provenance string.</summary>
        [JsonPropertyName("trainedFrom")]
        public string TrainedFrom { get; set; } = string.Empty;
    }
}