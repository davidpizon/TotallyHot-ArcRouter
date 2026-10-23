using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Quality.Ingress;

/// <summary>
/// Default ingress: honors the enabled flag and sampling rate, extracts a runnable snippet or falls back
/// to a bootstrap (judge-only) request for unfenced prose, and performs a non-blocking enqueue. Never
/// throws — a failure here must not touch the proxy's forward.
/// </summary>
public sealed class QualityIngress : IQualityIngress
{
    private readonly IQualityScoreAggregator? _aggregator;
    private readonly ISignalExtractor _extractor;
    private readonly ILogger<QualityIngress> _logger;
    private readonly QualityOptions _options;
    private readonly IQualityQueue _queue;

    /// <summary>Initializes a new instance of the <see cref="QualityIngress"/> class.</summary>
    /// <param name="extractor">The signal extractor.</param>
    /// <param name="queue">The bounded work queue.</param>
    /// <param name="options">The quality options (enabled flag, sampling rate).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregator">
    /// Receives terminal Incomplete grades that cannot be enqueued (queue full, no text, empty). Optional
    /// so existing tests that construct this type directly keep compiling.
    /// </param>
    public QualityIngress(
        ISignalExtractor extractor,
        IQualityQueue queue,
        IOptions<QualityOptions> options,
        ILogger<QualityIngress> logger,
        IQualityScoreAggregator? aggregator = null)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _extractor = extractor;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
        _aggregator = aggregator;
    }

    /// <inheritdoc/>
    public void TryIngest(QualityIngestContext context)
    {
        try
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            // Nullable annotations are a compile-time contract, not a runtime guarantee - this is a public
            // entry point and a caller compiled without nullable checks, or against an older signature,
            // can still hand it null. The guard is what turns that into a no-op instead of a throw.
            if (context is null || !_options.Enabled) return;

            if (_options.SamplingRate < 1.0 && Random.Shared.NextDouble() >= _options.SamplingRate) return;

            var text = context.ResponseText ?? string.Empty;

            // Tool-only bodies stay uningested until the tool-loop phase (issue #304 phase 6).
            if (context.HasToolCalls && string.IsNullOrWhiteSpace(text)) return;

            if (string.IsNullOrEmpty(text))
            {
                SubmitTerminal(context, IncompleteReasons.NoText);
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                SubmitTerminal(context, IncompleteReasons.Empty);
                return;
            }

            var request = _extractor.Extract(new SignalExtractionContext(
                ResponseText: text,
                Prompt: context.Prompt,
                Model: context.Model,
                CorrelationId: context.CorrelationId,
                SessionId: context.SessionId)) ?? CreateBootstrapRequest(context, text);

            if (_queue.TryEnqueue(request)) return;

            _logger.LogDebug(
                message: "Grading queue full; dropped {Language} request (correlation {CorrelationId}).",
                request.Language,
                request.CorrelationId);
            SubmitTerminal(context, IncompleteReasons.QueueFull);
        }
        catch (Exception ex)
        {
            // Best-effort by contract: swallow everything so the proxy forward is never affected.
            _logger.LogDebug(exception: ex, message: "Quality ingress failed; the forwarded response was unaffected.");
        }
    }

    /// <summary>
    /// Builds the ingress-only bootstrap request for unfenced prose. Does not infer a
    /// <c>RouterDimension</c>: that recipe is not a topic name (ADR-0027).
    /// </summary>
    private static QualityRequest CreateBootstrapRequest(QualityIngestContext context, string text)
    {
        return new QualityRequest(
            Code: string.Empty,
            Language: CodeLanguage.Unknown,
            Prompt: context.Prompt ?? string.Empty,
            Dimension: string.Empty,
            Model: context.Model,
            CorrelationId: context.CorrelationId,
            SessionId: context.SessionId,
            ResponseText: text,
            RecipeId: GradingRecipes.Bootstrap);
    }

    /// <summary>
    /// Writes a terminal Incomplete receipt off the enqueue path so a drop still leaves an audit trail.
    /// Fire-and-forget: the proxy forward must not wait on the observer.
    /// </summary>
    private void SubmitTerminal(QualityIngestContext context, string reason)
    {
        if (_aggregator is null) return;

        var result = new QualityResult
        {
            RequestCorrelationId = context.CorrelationId,
            SessionId = context.SessionId,
            Model = context.Model,
            QuestionPresent = !string.IsNullOrWhiteSpace(context.Prompt),
            RecipeId = GradingRecipes.Bootstrap,
            IncompleteReason = reason,
            GradeStatus = GradeStatus.Incomplete
        };

        _ = SubmitTerminalAsync(result);
    }

    /// <summary>Submits a terminal Incomplete result, swallowing observer failures.</summary>
    private async Task SubmitTerminalAsync(QualityResult result)
    {
        try
        {
            await _aggregator!.SubmitAsync(result: result).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                exception: ex,
                message: "Submitting a terminal incomplete grade for correlation {CorrelationId} failed.",
                result.RequestCorrelationId);
        }
    }
}
