using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Quality.Ingress;

/// <summary>
/// Default ingress: honors the enabled flag and sampling rate, extracts a runnable snippet, and performs
/// a non-blocking enqueue. Never throws — a failure here must not touch the proxy's forward.
/// </summary>
public sealed class QualityIngress : IQualityIngress
{
    private readonly ISignalExtractor _extractor;
    private readonly ILogger<QualityIngress> _logger;
    private readonly QualityOptions _options;
    private readonly IQualityQueue _queue;

    /// <summary>Initializes a new instance of the <see cref="QualityIngress"/> class.</summary>
    /// <param name="extractor">The signal extractor.</param>
    /// <param name="queue">The bounded work queue.</param>
    /// <param name="options">The quality options (enabled flag, sampling rate).</param>
    /// <param name="logger">The logger.</param>
    public QualityIngress(
        ISignalExtractor extractor,
        IQualityQueue queue,
        IOptions<QualityOptions> options,
        ILogger<QualityIngress> logger)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _extractor = extractor;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
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

            var request = _extractor.Extract(new SignalExtractionContext(
                ResponseText: context.ResponseText,
                Prompt: context.Prompt,
                Model: context.Model,
                CorrelationId: context.CorrelationId,
                SessionId: context.SessionId));

            if (request is null) return;

            if (!_queue.TryEnqueue(request))
                _logger.LogDebug(
                    message: "Grading queue full; dropped {Language} request (correlation {CorrelationId}).",
                    request.Language,
                    request.CorrelationId);
        }
        catch (Exception ex)
        {
            // Best-effort by contract: swallow everything so the proxy forward is never affected.
            _logger.LogDebug(exception: ex, message: "Quality ingress failed; the forwarded response was unaffected.");
        }
    }
}