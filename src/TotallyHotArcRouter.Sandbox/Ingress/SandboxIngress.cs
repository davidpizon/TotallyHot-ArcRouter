using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Ingress;

/// <summary>
/// Default ingress: honors the enabled flag and sampling rate, extracts a runnable snippet, and performs
/// a non-blocking enqueue. Never throws — a failure here must not touch the proxy's forward.
/// </summary>
public sealed class SandboxIngress : ISandboxIngress
{
    private readonly ISignalExtractor _extractor;
    private readonly ISandboxQueue _queue;
    private readonly SandboxOptions _options;
    private readonly ILogger<SandboxIngress> _logger;

    /// <summary>Initializes a new instance of the <see cref="SandboxIngress"/> class.</summary>
    /// <param name="extractor">The signal extractor.</param>
    /// <param name="queue">The bounded work queue.</param>
    /// <param name="options">The sandbox options (enabled flag, sampling rate).</param>
    /// <param name="logger">The logger.</param>
    public SandboxIngress(
        ISignalExtractor extractor,
        ISandboxQueue queue,
        IOptions<SandboxOptions> options,
        ILogger<SandboxIngress> logger)
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

    /// <inheritdoc />
    public void TryIngest(SandboxIngestContext context)
    {
        try
        {
            if (context is null || !_options.Enabled)
            {
                return;
            }

            if (_options.SamplingRate < 1.0 && Random.Shared.NextDouble() >= _options.SamplingRate)
            {
                return;
            }

            var request = _extractor.Extract(new SignalExtractionContext(
                ResponseText: context.ResponseText,
                Prompt: context.Prompt,
                Model: context.Model,
                CorrelationId: context.CorrelationId,
                SessionId: context.SessionId));

            if (request is null)
            {
                return;
            }

            if (!_queue.TryEnqueue(request))
            {
                _logger.LogDebug(
                    "Sandbox queue full; dropped {Language} request (correlation {CorrelationId}).",
                    request.Language,
                    request.CorrelationId);
            }
        }
        catch (Exception ex)
        {
            // Best-effort by contract: swallow everything so the proxy forward is never affected.
            _logger.LogDebug(ex, "Sandbox ingress failed; the forwarded response was unaffected.");
        }
    }
}

