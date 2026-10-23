using System.Text;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Writes one <c>grade_receipts</c> row per final <see cref="QualityResult"/> (issue #293 plan §2.4).
/// </summary>
public sealed class GradeReceiptRecordObserver : IQualityScoreObserver
{
    private readonly ILogger<GradeReceiptRecordObserver> _logger;
    private readonly JudgeOptions _judgeOptions;
    private readonly PendingConversationWindowCache _pendingWindowCache;
    private readonly QualityOptions _qualityOptions;
    private readonly QuestionHmacHasher _questionHmacHasher;
    private readonly IGradeReceiptStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="GradeReceiptRecordObserver"/> class.</summary>
    /// <param name="store">Where each receipt is persisted.</param>
    /// <param name="pendingWindowCache">Supplies question-size facts and the newest user message for HMAC.</param>
    /// <param name="questionHmacHasher">Computes the keyed question hash.</param>
    /// <param name="qualityOptions">Supplies scorer and weights version stamps.</param>
    /// <param name="judgeOptions">Supplies the judge prompt version stamp.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">Clock used to stamp receipts; defaults to <see cref="TimeProvider.System"/>.</param>
    public GradeReceiptRecordObserver(
        IGradeReceiptStore store,
        PendingConversationWindowCache pendingWindowCache,
        QuestionHmacHasher questionHmacHasher,
        IOptions<QualityOptions> qualityOptions,
        IOptions<JudgeOptions> judgeOptions,
        ILogger<GradeReceiptRecordObserver> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pendingWindowCache);
        ArgumentNullException.ThrowIfNull(questionHmacHasher);
        ArgumentNullException.ThrowIfNull(qualityOptions);
        ArgumentNullException.ThrowIfNull(judgeOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _pendingWindowCache = pendingWindowCache;
        _questionHmacHasher = questionHmacHasher;
        _qualityOptions = qualityOptions.Value;
        _judgeOptions = judgeOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrEmpty(result.Model))
        {
            _logger.LogDebug("Quality result has no model attribution; skipping grade-receipt recording.");
            return;
        }

        if (string.IsNullOrEmpty(result.RequestCorrelationId))
        {
            _logger.LogDebug("Quality result has no correlation id; skipping grade-receipt recording.");
            return;
        }

        _pendingWindowCache.TryTake(correlationId: result.RequestCorrelationId, out var window);

        var newestForHmac = window?.NewestUserMessage;
        var questionHmac = _questionHmacHasher.TryCompute(newestForHmac);
        var promptVersion = result.JudgeScore.HasValue ? _judgeOptions.PromptVersion : null;
        var weightsVersion = string.Equals(result.RecipeId, GradingRecipes.Bootstrap,
            StringComparison.OrdinalIgnoreCase)
            ? GradingRecipes.Bootstrap
            : _qualityOptions.WeightsVersion;
        var judgeModel = result.JudgeScore.HasValue
            ? (string.IsNullOrWhiteSpace(result.JudgeModel) ? _judgeOptions.ModelName : result.JudgeModel)
            : null;

        var receipt = new GradeReceiptRecord(
            CorrelationId: result.RequestCorrelationId,
            CreatedAtUtc: _timeProvider.GetUtcNow(),
            Dimension: result.Dimension,
            Model: result.Model,
            GradeStatus: result.GradeStatus,
            UnifiedScore: Math.Clamp(value: result.UnifiedScore, 0.0, 1.0),
            QuestionHmac: questionHmac,
            QuestionChars: window?.QuestionChars,
            ContextMessages: window?.ContextMessages,
            QuestionTruncated: window?.Truncated ?? false,
            FingerprintRef: null,
            ScorerVersion: _qualityOptions.ScorerVersion,
            PromptVersion: promptVersion,
            WeightsVersion: weightsVersion,
            AbstainedGraders: FormatAbstainedGraders(result),
            IncompleteReason: result.GradeStatus == GradeStatus.Complete ? null : result.IncompleteReason,
            GradeSource: result.GradeSource,
            ResponseHasCode: result.ResponseHasCode,
            JudgeModel: string.IsNullOrWhiteSpace(judgeModel) ? null : judgeModel,
            AnswerModel: result.Model);

        await _store.UpsertAsync(record: receipt, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                message: "Recorded grade receipt for correlation {CorrelationId} with status {GradeStatus}.",
                result.RequestCorrelationId,
                result.GradeStatus);
    }

    /// <summary>Builds the comma-separated abstention list stored on incomplete grades.</summary>
    private static string FormatAbstainedGraders(QualityResult result)
    {
        if (result.GradeStatus == GradeStatus.Complete) return string.Empty;

        if (result.GraderDegradedReasons.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        foreach (var (grader, reason) in result.GraderDegradedReasons.OrderBy(pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (builder.Length > 0) builder.Append(',');
            builder.Append(grader);
            builder.Append(':');
            builder.Append(reason);
        }

        return builder.ToString();
    }
}
