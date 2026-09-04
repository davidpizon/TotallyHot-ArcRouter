using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// JSON (de)serialization for <see cref="EmbeddingLogRegModelArtifact"/>. A separate DTO is used because
/// <see cref="EmbeddingLogRegModelArtifact"/>'s <see cref="IReadOnlyDictionary{TKey,TValue}"/> member
/// doesn't round-trip through <c>System.Text.Json</c>'s default record support without one - mirrors
/// <see cref="CodeRouterBench.Evaluation.LogRegModelArtifactSerializer"/>'s shape for the TF-IDF artifact.
/// </summary>
public static class EmbeddingLogRegModelArtifactSerializer
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
    public static string Serialize(EmbeddingLogRegModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var dto = new Dto
        {
            EmbeddingDimension = artifact.EmbeddingDimension,
            EmbeddingModel = artifact.EmbeddingModel,
            ClassWeights =
                artifact.ClassWeights.ToDictionary(keySelector: kvp => kvp.Key, elementSelector: kvp => kvp.Value),
            TrainedFrom = artifact.TrainedFrom,
            BootstrapTaskCount = artifact.BootstrapTaskCount,
            MemoryEntryCount = artifact.MemoryEntryCount
        };

        return JsonSerializer.Serialize(value: dto, options: Options);
    }

    /// <summary>Deserializes an artifact previously produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">The JSON document text.</param>
    /// <exception cref="FormatException">The document is not a valid serialized artifact.</exception>
    public static EmbeddingLogRegModelArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var dto = JsonSerializer.Deserialize<Dto>(json: json, options: Options)
                  ?? throw new FormatException("The logreg voter model document deserialized to null.");

        var artifact = new EmbeddingLogRegModelArtifact(
            EmbeddingDimension: dto.EmbeddingDimension,
            ClassWeights: dto.ClassWeights,
            TrainedFrom: dto.TrainedFrom,
            BootstrapTaskCount: dto.BootstrapTaskCount,
            MemoryEntryCount: dto.MemoryEntryCount,
            EmbeddingModel: dto.EmbeddingModel);
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
            throw new FormatException(
                $"The logreg voter model document's embeddingDimension is {artifact.EmbeddingDimension}, must be positive.");

        if (artifact.ClassWeights is null)
            // A JSON document can carry an explicit null for the whole classWeights property (e.g.
            // "classWeights": null); the Dto's property initializer only covers a missing property, not
            // an explicit null, so this can reach here despite ClassWeights being non-nullable.
            throw new FormatException("The logreg voter model document's classWeights is null.");

        foreach (var (model, weights) in artifact.ClassWeights)
        {
            if (weights is null)
                // A JSON document can carry an explicit null value for a classWeights entry (e.g.
                // "model-a": null); accessing weights.Length below would otherwise throw
                // NullReferenceException instead of a controlled FormatException.
                throw new FormatException($"The logreg voter model's weight vector for '{model}' is null.");

            if (weights.Length != artifact.EmbeddingDimension + 1)
                throw new FormatException(
                    $"The logreg voter model's weight vector for '{model}' has length {weights.Length}, " +
                    $"expected {artifact.EmbeddingDimension + 1} (embedding dimension + 1 bias term).");

            if (weights.Any(value => !double.IsFinite(value)))
                throw new FormatException(
                    $"The logreg voter model's weight vector for '{model}' contains a non-finite value (NaN or Infinity).");
        }
    }

    /// <summary>
    /// The wire shape for <see cref="EmbeddingLogRegModelArtifact"/>, needed because the record's
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> member doesn't round-trip through <c>System.Text.Json</c>'s default
    /// record support.
    /// </summary>
    private sealed class Dto
    {
        /// <summary>Gets or sets the embedding dimension the artifact was trained at.</summary>
        [JsonPropertyName("embeddingDimension")]
        public int EmbeddingDimension { get; init; }

        /// <summary>Gets or sets the per-class weight vectors, keyed by canonicalized model id.</summary>
        [JsonPropertyName("classWeights")]
        public Dictionary<string, double[]> ClassWeights { get; init; } = [];

        /// <summary>Gets or sets the human-readable training provenance string.</summary>
        [JsonPropertyName("trainedFrom")]
        public string TrainedFrom { get; init; } = string.Empty;

        /// <summary>Gets or sets the number of OOD bootstrap tasks that contributed to training.</summary>
        [JsonPropertyName("bootstrapTaskCount")]
        public int BootstrapTaskCount { get; init; }

        /// <summary>Gets or sets the number of live memory entries that contributed to training.</summary>
        [JsonPropertyName("memoryEntryCount")]
        public int MemoryEntryCount { get; init; }

        /// <summary>
        /// Gets or sets the identity of the embedding model this artifact was fitted against, or null for a
        /// pre-provenance artifact.
        /// </summary>
        [JsonPropertyName("embeddingModel")]
        public string? EmbeddingModel { get; init; }
    }
}