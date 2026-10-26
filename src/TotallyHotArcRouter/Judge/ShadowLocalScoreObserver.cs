using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Writes <see cref="QualityResult.LocalScore"/> beside the teacher score once an accepted artifact
/// matches the current teacher model, prompt version, and BGE id. The teacher score stays the grade
/// of record. This observer does not call the embedder, does not read the pending question-vector
/// cache, and does not change <see cref="QualityResult.GradeSource"/>. It is registered after
/// <see cref="ResponseVectorGradeObserver"/>, which has already stored the response vector, and
/// immediately before <see cref="GradeReceiptRecordObserver"/>.
/// </summary>
public sealed class ShadowLocalScoreObserver : IQualityScoreObserver
{
    private readonly IOptionsMonitor<EmbeddingOptions> _embedding;
    private readonly IOptionsMonitor<JudgeOptions> _judge;
    private readonly ILogger<ShadowLocalScoreObserver> _logger;
    private readonly IOptionsMonitor<QualityOptions> _quality;
    private readonly GraderEpochLabelQuery _query;
    private readonly GraderEpochArtifactStore _store;
    private readonly SqliteResponseVectorStore _vectors;

    /// <summary>Initializes a new instance of the <see cref="ShadowLocalScoreObserver"/> class.</summary>
    /// <param name="store">The accepted artifact and its sealed reservoir. Failed candidates are not loaded.</param>
    /// <param name="query">Reads the question vector from <c>memory_entries</c>. It does not re-embed.</param>
    /// <param name="vectors">Opens the response vector. A different model id is left on disk; an authentication failure is deleted by this store.</param>
    /// <param name="judge">The current teacher model and prompt version.</param>
    /// <param name="quality">The current code weights version, which selects the code artifact.</param>
    /// <param name="embedding">The current BGE identity and vector length. This is not an embedding client.</param>
    /// <param name="logger">Logs one debug line per stamp. The line has no question text and no vector.</param>
    public ShadowLocalScoreObserver(
        GraderEpochArtifactStore store,
        GraderEpochLabelQuery query,
        SqliteResponseVectorStore vectors,
        IOptionsMonitor<JudgeOptions> judge,
        IOptionsMonitor<QualityOptions> quality,
        IOptionsMonitor<EmbeddingOptions> embedding,
        ILogger<ShadowLocalScoreObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _query = query;
        _vectors = vectors;
        _judge = judge;
        _quality = quality;
        _embedding = embedding;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        var weights = GraderEpochKinds.WeightsVersionThatWillBeStored(
            recipeId: result.RecipeId, codeWeightsVersion: _quality.CurrentValue.WeightsVersion);
        Apply(result, weights);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stamps <paramref name="result"/> for a <c>weights_version</c> the receipt observer would store,
    /// or leaves the local fields null when that string is neither bootstrap nor the current code
    /// weights version. The live path only produces those two strings; tests pass a third string here.
    /// </summary>
    /// <param name="result">The final grade. <see cref="QualityResult.JudgeScore"/> is not replaced.</param>
    /// <param name="weightsVersion">The <c>weights_version</c> that will be stored on the receipt.</param>
    internal void Apply(QualityResult result, string weightsVersion)
    {
        var kind = GraderEpochKinds.TryForWeights(weightsVersion, _quality.CurrentValue.WeightsVersion);
        if (kind is null) return;

        var judge = _judge.CurrentValue;
        var embedding = _embedding.CurrentValue;
        var bge = embedding.ResolveModelIdentity();
        var artifact = _store.TryReadAccepted(kind);
        if (artifact is null || !artifact.MatchesTeacher(teacherModel: judge.ModelName,
                promptVersion: judge.PromptVersion, bgeModelId: bge))
            return;

        if (result.LocalScore is not null &&
            string.Equals(result.LocalGraderVersion, artifact.ArtifactId, StringComparison.Ordinal))
            return;

        if (!_vectors.TryReadEmbeddingModel(result.RequestCorrelationId, out var responseModel)) return;
        if (!string.Equals(responseModel, bge, StringComparison.Ordinal)) return;
        if (!_vectors.TryOpen(result.RequestCorrelationId, out var response)) return;
        if (!GraderEpochVectors.IsUsable(response, artifact.EmbeddingDimension)) return;

        _query.TryReadQuestion(result.RequestCorrelationId, out var questionModel, out var questionVector);
        var question = GraderEpochVectors.ResolveQuestion(flag: result.QuestionVectorMissing, vector: questionVector,
            modelId: questionModel, bgeModelId: bge, dimension: artifact.EmbeddingDimension);
        double? syntax = GradingRecipes.IsCodeRecipe(result.RecipeId)
            ? (result.SyntaxValid ? 1.0 : 0.0)
            : null;
        var input = new GraderEpochFeatureInput
        {
            QuestionMissing = question.Missing,
            QuestionVector = question.Vector,
            AnalysisMissing = result.AnalysisScore is null,
            AnalysisScore = result.AnalysisScore,
            SyntaxMissing = syntax is null,
            SyntaxScore = syntax,
            ResponseHasCode = result.ResponseHasCode,
            LengthMissing = result.ResponseLengthChars is null,
            LengthChars = result.ResponseLengthChars,
            ResponseVector = response
        };
        var score = artifact.Score(input);
        if (score is null) return;

        result.LocalScore = score;
        result.LocalGraderVersion = artifact.ArtifactId;
        result.LocalConfidence = Confidence(artifact, input);
        _logger.LogDebug(
            message: "Shadow score for correlation {CorrelationId} artifact {ArtifactId} is {LocalScore}.",
            result.RequestCorrelationId,
            artifact.ArtifactId,
            score);
    }

    /// <summary>
    /// The row's cosine to its nearest reservoir neighbor when the floor is non-null and the question
    /// vector is usable. A null floor stores null, never 0, even when the cosine could be computed.
    /// </summary>
    private double? Confidence(GraderEpochArtifact artifact, GraderEpochFeatureInput input)
    {
        if (artifact.CosineFloor is null || input.QuestionMissing || input.QuestionVector is null) return null;
        return GraderEpochConfidence.Nearest(input.QuestionVector, _store.OpenReservoir(artifact));
    }
}
