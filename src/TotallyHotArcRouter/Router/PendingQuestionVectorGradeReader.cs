using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Stamps <see cref="QualityResult.QuestionVectorMissing"/> from the pending question-vector cache
/// without removing the entry. <see cref="EmbeddingMemoryScoreObserver"/> stays the only
/// <see cref="PendingValueCache{T}.TryTake"/> of that cache, so the kNN write still finds a hit after
/// this reader has run. A miss is expired, evicted, or never stored; those three are not distinguished.
/// This reader does not embed, does not open a session file, and does not write a zero vector.
/// </summary>
public sealed class PendingQuestionVectorGradeReader : IQualityScoreObserver
{
    private readonly ILogger<PendingQuestionVectorGradeReader> _logger;
    private readonly PendingValueCache<float[]> _pendingQuestionVectors;

    /// <summary>Initializes a new instance of the <see cref="PendingQuestionVectorGradeReader"/> class.</summary>
    /// <param name="pendingQuestionVectors">The request-path cache of frozen BGE question vectors.</param>
    /// <param name="logger">The logger.</param>
    public PendingQuestionVectorGradeReader(
        PendingValueCache<float[]> pendingQuestionVectors,
        ILogger<PendingQuestionVectorGradeReader> logger)
    {
        ArgumentNullException.ThrowIfNull(pendingQuestionVectors);
        ArgumentNullException.ThrowIfNull(logger);
        _pendingQuestionVectors = pendingQuestionVectors;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        var missing = string.IsNullOrWhiteSpace(result.RequestCorrelationId) ||
                      !_pendingQuestionVectors.TryPeek(correlationId: result.RequestCorrelationId, value: out _);
        result.QuestionVectorMissing = missing;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                message: "Question vector missing {QuestionVectorMissing} for correlation {CorrelationId}.",
                missing,
                result.RequestCorrelationId);

        return Task.CompletedTask;
    }
}
