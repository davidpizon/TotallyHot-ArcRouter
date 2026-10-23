using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Backfills a transcript row's <c>score</c> column once the verifier's result arrives
/// (docs/router/self-organizing-classification-plan.md Phase T1b) - the second of the transcript store's
/// two writes (insert at request time, this update once scored). Registered alongside
/// <see cref="Router.RouterMemoryScoreObserver"/> and <see cref="Router.EmbeddingMemoryScoreObserver"/> in
/// the fan-out <see cref="Router.CompositeRouterScoreObserver"/>, but only when transcript capture is
/// enabled - see <c>Hosting.ServiceCollectionExtensions</c>.
/// </summary>
public sealed class TranscriptScoreObserver : IQualityScoreObserver
{
    private readonly ILogger<TranscriptScoreObserver> _logger;
    private readonly string? _scorerVersion;
    private readonly ITranscriptStore _store;

    /// <summary>Initializes a new instance of the <see cref="TranscriptScoreObserver"/> class.</summary>
    /// <param name="store">The transcript store to backfill into.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="qualityOptions">
    /// Supplies <see cref="QualityOptions.ScorerVersion"/> so a live non-code grade can stamp the row
    /// and keep the code-only rescan from overwriting it with null. Optional so existing tests compile.
    /// </param>
    public TranscriptScoreObserver(
        ITranscriptStore store,
        ILogger<TranscriptScoreObserver> logger,
        IOptions<QualityOptions>? qualityOptions = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
        _scorerVersion = qualityOptions?.Value.ScorerVersion;
    }

    /// <inheritdoc/>
    public async Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrEmpty(result.RequestCorrelationId))
        {
            _logger.LogDebug("Quality result has no correlation id; skipping transcript score backfill.");
            return;
        }

        var score = Math.Clamp(value: result.UnifiedScore, 0.0, 1.0);
        var scorerVersion = GradingRecipes.IsCodeRecipe(result.RecipeId) ? null : _scorerVersion;
        await _store.UpdateOutcomeAsync(
            correlationId: result.RequestCorrelationId,
            score: score,
            isJudgeScored: result.JudgeScore.HasValue,
            scorerVersion: scorerVersion,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                message: "Backfilled transcript score for correlation {CorrelationId} with score {Score:F3}.",
                result.RequestCorrelationId,
                score);
    }
}