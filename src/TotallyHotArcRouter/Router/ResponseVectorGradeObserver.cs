using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Writes one sealed response vector when a grader scored the reply and the grade path still holds
/// non-blank response text. <see cref="QualityResult.JudgeScore"/> or <see cref="QualityResult.LocalScore"/>
/// is that score. A reply with neither, or whose text is already gone, writes no row and does not open a
/// session file. The embed uses the frozen BGE session on text the judge cache already truncated. It does
/// not recompute a missing question vector.
/// </summary>
public sealed class ResponseVectorGradeObserver : IQualityScoreObserver
{
    private readonly IEmbeddingClient _embeddings;
    private readonly ILogger<ResponseVectorGradeObserver> _logger;
    private readonly PendingResponseTextCache _responseText;
    private readonly IResponseVectorStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="ResponseVectorGradeObserver"/> class.</summary>
    /// <param name="embeddings">The frozen BGE session. This is not a second embedder.</param>
    /// <param name="responseText">Response text the grade path already holds. Peeked, not taken.</param>
    /// <param name="store">The sealed response-vector store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">Clock for the row timestamp. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ResponseVectorGradeObserver(
        IEmbeddingClient embeddings,
        PendingResponseTextCache responseText,
        IResponseVectorStore store,
        ILogger<ResponseVectorGradeObserver> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(responseText);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _embeddings = embeddings;
        _responseText = responseText;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.JudgeScore is null && result.LocalScore is null) return;
        if (string.IsNullOrWhiteSpace(result.RequestCorrelationId)) return;
        if (!_responseText.TryPeek(correlationId: result.RequestCorrelationId, text: out var text) ||
            string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug(
                "Response text unavailable for correlation {CorrelationId}; no response vector was stored.",
                result.RequestCorrelationId);
            return;
        }

        var embedding = await _embeddings.EmbedAsync(text: text, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _store.UpsertAsync(
            correlationId: result.RequestCorrelationId,
            embeddingModel: _embeddings.ModelIdentity,
            vector: embedding.Vector,
            createdAtUtc: _timeProvider.GetUtcNow(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
