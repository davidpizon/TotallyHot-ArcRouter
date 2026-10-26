using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Phase 8 local-grader takeover (issue #304). Scores the reply before the teacher is enqueued, from
/// the response text the grade path already holds. It does not call the judge, does not take the
/// question-vector cache, and does not open a session file.
/// </summary>
public sealed class LocalGradeTakeover : ILocalGradeTakeover
{
    private readonly IEmbeddingClient _embeddings;
    private readonly IOptionsMonitor<EmbeddingOptions> _embeddingOptions;
    private readonly IOptionsMonitor<GraderEpochOptions> _epoch;
    private readonly IOptionsMonitor<JudgeOptions> _judge;
    private readonly ILogger<LocalGradeTakeover> _logger;
    private readonly IOptionsMonitor<QualityOptions> _quality;
    private readonly PendingValueCache<float[]> _questions;
    private readonly PendingResponseTextCache _responseText;
    private readonly GraderEpochArtifactStore _store;
    private readonly TimeProvider _time;
    private readonly IResponseVectorStore _vectors;

    /// <summary>Initializes a new instance of the <see cref="LocalGradeTakeover"/> class.</summary>
    /// <param name="store">The accepted artifact and its reservoir. A missing pointer leaves the row unhandled.</param>
    /// <param name="epoch">Takeover switch, spot-check rate, and the bars. Read on each call, not cached.</param>
    /// <param name="judge">The current teacher model and prompt version. A mismatch leaves the row unhandled.</param>
    /// <param name="quality">The current code weights version, which selects the code artifact.</param>
    /// <param name="embeddingOptions">The current BGE identity and the only legal vector length.</param>
    /// <param name="embeddings">The frozen BGE session. This is not a second embedder.</param>
    /// <param name="responseText">Response text the grade path already holds. Peeked, not taken.</param>
    /// <param name="questions">The pending question-vector cache. Peeked, not taken.</param>
    /// <param name="vectors">Seals the response vector before the shadow observer runs.</param>
    /// <param name="logger">Debug lines for a skip, a low-confidence call, a draw, and a closed failure.</param>
    /// <param name="timeProvider">Clock for the stored vector. Defaults to <see cref="TimeProvider.System"/>.</param>
    public LocalGradeTakeover(
        GraderEpochArtifactStore store,
        IOptionsMonitor<GraderEpochOptions> epoch,
        IOptionsMonitor<JudgeOptions> judge,
        IOptionsMonitor<QualityOptions> quality,
        IOptionsMonitor<EmbeddingOptions> embeddingOptions,
        IEmbeddingClient embeddings,
        PendingResponseTextCache responseText,
        PendingValueCache<float[]> questions,
        IResponseVectorStore vectors,
        ILogger<LocalGradeTakeover> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(embeddingOptions);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(responseText);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _epoch = epoch;
        _judge = judge;
        _quality = quality;
        _embeddingOptions = embeddingOptions;
        _embeddings = embeddings;
        _responseText = responseText;
        _questions = questions;
        _vectors = vectors;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<LocalGradeDecision> ApplyAsync(QualityResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var weights = GraderEpochKinds.WeightsVersionThatWillBeStored(
            recipeId: result.RecipeId, codeWeightsVersion: _quality.CurrentValue.WeightsVersion);
        var kind = GraderEpochKinds.TryForWeights(weights, _quality.CurrentValue.WeightsVersion);
        if (kind is null) return new LocalGradeDecision(false, false, null);

        var judge = _judge.CurrentValue;
        var embedding = _embeddingOptions.CurrentValue;
        var bge = embedding.ResolveModelIdentity();
        var artifact = _store.TryReadAccepted(kind);
        if (artifact is null
            || !artifact.MatchesTeacher(teacherModel: judge.ModelName, promptVersion: judge.PromptVersion, bgeModelId: bge)
            || artifact.FeatureSchemaVersion != GraderEpochArtifact.SchemaVersion)
            return new LocalGradeDecision(false, false, null);

        if (!_responseText.TryPeek(correlationId: result.RequestCorrelationId, text: out var text) ||
            string.IsNullOrWhiteSpace(text))
        {
            LogClosed(result.RequestCorrelationId, "no_response_text");
            return new LocalGradeDecision(false, false, null);
        }

        float[] response;
        try
        {
            var embedded = await _embeddings.EmbedAsync(text: text, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            response = embedded.Vector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(exception: ex,
                message: "Local grader failed closed for correlation {CorrelationId} reason {Reason}.",
                result.RequestCorrelationId,
                "embed_failed");
            return new LocalGradeDecision(false, false, null);
        }

        await _vectors.UpsertAsync(
            correlationId: result.RequestCorrelationId,
            embeddingModel: _embeddings.ModelIdentity,
            vector: response,
            createdAtUtc: _time.GetUtcNow(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!GraderEpochVectors.IsUsable(response, artifact.EmbeddingDimension))
        {
            LogClosed(result.RequestCorrelationId, "unusable_vector");
            return new LocalGradeDecision(false, false, null);
        }

        var question = ReadQuestion(result.RequestCorrelationId, artifact.EmbeddingDimension);
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
        if (score is null) return new LocalGradeDecision(false, false, null);

        result.LocalScore = score;
        result.LocalGraderVersion = artifact.ArtifactId;
        result.LocalConfidence = Confidence(artifact, question);
        return Decide(result: result, kind: kind, artifact: artifact, score: score.Value);
    }

    private LocalGradeDecision Decide(QualityResult result, string kind, GraderEpochArtifact artifact, double score)
    {
        var options = _epoch.CurrentValue;
        var lowConfidence = artifact.CosineFloor is not null
                            && result.LocalConfidence is not null
                            && result.LocalConfidence < artifact.CosineFloor;
        var drawn = !lowConfidence && SpotCheckDraw.IsDrawn(result.RequestCorrelationId, options.SpotCheckRate);
        result.SpotCheck = drawn;

        string? reason = null;
        bool callTeacher;
        bool allowLocal;
        if (lowConfidence)
        {
            callTeacher = true;
            reason = GradeSources.LowConfidence;
            allowLocal = false;
            _logger.LogDebug(
                message: "Local grader called the teacher for low confidence on correlation {CorrelationId} kind {Kind} artifact {ArtifactId}.",
                result.RequestCorrelationId,
                kind,
                artifact.ArtifactId);
        }
        else if (drawn)
        {
            callTeacher = true;
            reason = GradeSources.SpotCheck;
            allowLocal = true;
            _logger.LogDebug(
                message: "Local grader drew a spot-check for correlation {CorrelationId} kind {Kind} artifact {ArtifactId}.",
                result.RequestCorrelationId,
                kind,
                artifact.ArtifactId);
        }
        else
        {
            callTeacher = false;
            allowLocal = true;
        }

        if (_store.TryReadRollbackMarker(kind) is not null)
        {
            callTeacher = true;
            if (!drawn) allowLocal = false;
        }

        if (!options.TakeoverEnabled)
        {
            callTeacher = true;
            allowLocal = false;
        }
        else if (callTeacher == false)
        {
            _logger.LogDebug(
                message: "Local grader skipped the teacher for correlation {CorrelationId} kind {Kind} artifact {ArtifactId} local score {LocalScore}.",
                result.RequestCorrelationId,
                kind,
                artifact.ArtifactId,
                score);
        }

        result.TeacherCallReason = reason;
        result.AllowLocalGradeOfRecord = allowLocal;
        return new LocalGradeDecision(true, callTeacher, reason);
    }

    private (bool Missing, float[]? Vector) ReadQuestion(string correlationId, int dimension)
    {
        if (_questions.TryPeek(correlationId: correlationId, value: out var peeked)
            && GraderEpochVectors.IsUsable(peeked, dimension))
            return (Missing: false, Vector: peeked);

        return (Missing: true, Vector: null);
    }

    private double? Confidence(GraderEpochArtifact artifact, (bool Missing, float[]? Vector) question)
    {
        if (artifact.CosineFloor is null || question.Missing || question.Vector is null) return null;
        return GraderEpochConfidence.Nearest(question.Vector, _store.OpenReservoir(artifact));
    }

    private void LogClosed(string correlationId, string reason)
    {
        _logger.LogDebug(
            message: "Local grader failed closed for correlation {CorrelationId} reason {Reason}.",
            correlationId,
            reason);
    }
}
