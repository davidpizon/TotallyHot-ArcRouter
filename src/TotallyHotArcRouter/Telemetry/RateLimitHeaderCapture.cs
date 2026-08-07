using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Captures every upstream response header whose name starts with <c>anthropic-ratelimit-</c> or
/// <c>x-ratelimit-</c> (case-insensitive) and persists it verbatim, mirroring <see cref="IUsageExtractor"/>'s
/// provider-dispatch design. Prefix capture, not a hardcoded header name list, is what makes this generic
/// over Anthropic's standard (<c>x-api-key</c>) and subscription-OAuth-unified header families, and
/// OpenAI's <c>x-ratelimit-*</c> family (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §6.2),
/// without any per-account or per-provider configuration.
/// </summary>
public interface IRateLimitHeaderCapture
{
    /// <summary>
    /// Captures <paramref name="headers"/>'s <c>anthropic-ratelimit-*</c>/<c>x-ratelimit-*</c> entries for
    /// <paramref name="providerKey"/>. Best-effort: the capture itself never throws (a storage failure is
    /// logged and swallowed) and never blocks the caller, so a caller that doesn't await the returned task
    /// (as <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/> doesn't, on the request path) is never
    /// delayed by the SQLite write; a caller that does await it sees the write actually complete, or observes
    /// <paramref name="cancellationToken"/> by throwing <see cref="OperationCanceledException"/> if it's
    /// canceled first - that only stops the caller's wait, not the queued write itself.
    /// </summary>
    /// <param name="providerKey">The provider key the response came from.</param>
    /// <param name="headers">The upstream response's headers.</param>
    /// <param name="cancellationToken">Cancels waiting on the returned task; does not cancel the capture itself.</param>
    Task CaptureAsync(string providerKey, HttpResponseHeaders headers, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRateLimitHeaderCapture" />
public sealed class RateLimitHeaderCapture : IRateLimitHeaderCapture, IDisposable
{
    private static readonly string[] HeaderPrefixes = ["anthropic-ratelimit-", "x-ratelimit-"];

    // Generous relative to normal traffic (one entry per response with matching headers): the queue only
    // approaches this under sustained backlog, which is exactly the condition the bound exists to cap.
    private const int QueueCapacity = 1024;

    // Upper bound on how long Dispose blocks the shutdown path draining the consumer loop. Best-effort: if
    // a queue's worth of writes hasn't drained in this long, shutdown proceeds anyway rather than hanging.
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly PriceCatalogRepository _repository;
    private readonly ILogger<RateLimitHeaderCapture>? _logger;
    private readonly Channel<CaptureItem> _channel;
    private readonly Task _consumerLoop;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitHeaderCapture"/> class and starts its single
    /// background consumer.
    /// </summary>
    public RateLimitHeaderCapture(PriceCatalogRepository repository, ILogger<RateLimitHeaderCapture>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _logger = logger;

        // Bounded, not unbounded: if the SQLite write path ever falls behind a request path that can
        // enqueue far faster than one consumer can drain, an unbounded queue grows without limit and can
        // OOM the process. The default FullMode (Wait) only blocks the awaitable WriteAsync; CaptureAsync
        // below always calls the non-blocking TryWrite, which under Wait simply returns false the instant
        // the channel is full rather than waiting - so a full queue never stalls the caller, and CaptureAsync
        // can treat that false as "drop this capture" instead of back-pressure.
        _channel = Channel.CreateBounded<CaptureItem>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // One dedicated long-lived task processes every capture serially. This is the only Task.Run this
        // class ever performs (started once here, not once per response) - the request path only ever
        // enqueues, so it can't spawn thread-pool churn under load the way a Task.Run-per-call version did.
        _consumerLoop = Task.Run(ConsumeAsync);
    }

    /// <inheritdoc />
    public Task CaptureAsync(string providerKey, HttpResponseHeaders headers, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || headers is null)
        {
            return Task.CompletedTask;
        }

        var matched = new List<RateLimitHeaderRow>();
        foreach (var header in headers)
        {
            if (!Array.Exists(HeaderPrefixes, prefix => header.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = string.Join(", ", header.Value);
            matched.Add(new RateLimitHeaderRow(header.Key, value));
        }

        if (matched.Count == 0)
        {
            return Task.CompletedTask;
        }

        // The returned task completes when the background consumer actually finishes the write, not when
        // it's merely enqueued - so a caller that awaits it (the test suite) observes the persisted row,
        // while ProxyMiddleware's fire-and-forget call on the request path never waits on it.
        // RunContinuationsAsynchronously keeps an awaiting caller's continuation off the single consumer
        // thread, so one slow caller can't stall the queue behind it.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new CaptureItem(providerKey, matched, DateTimeOffset.UtcNow, completion)))
        {
            // Either the queue is full (a sustained backlog - see QueueCapacity) or the channel is
            // completed (shutting down). Both are best-effort drops, not throws: nothing was queued, so
            // nothing will ever complete this TCS from ConsumeAsync - complete it here instead.
            _logger?.LogDebug("Rate-limit header capture queue is full or closed; dropping capture for provider {Provider}.", SanitizeForLog(providerKey));
            completion.TrySetResult();
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Drains <see cref="_channel"/> for the lifetime of this instance, writing one capture at a time.</summary>
    private async Task ConsumeAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _repository.UpsertRateLimitHeaders(item.ProviderKey, item.Headers, item.ObservedAtUtc);
            }
            catch (Exception ex)
            {
                // Best-effort: a storage hiccup here must never fail a request that already succeeded upstream.
                _logger?.LogWarning(ex, "Failed to capture rate-limit headers for provider {Provider}.", SanitizeForLog(item.ProviderKey));
            }
            finally
            {
                // Never TrySetException: the contract is "never throws" even for a caller that awaits the
                // returned task, and the failure above (if any) was already logged and swallowed.
                item.Completion.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Stops accepting new captures and waits (briefly) for the consumer loop to drain whatever was already
    /// queued, so a normal shutdown of this singleton doesn't silently drop the last captured headers.
    /// </summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try
        {
            _consumerLoop.Wait(DisposeDrainTimeout);
        }
        catch (AggregateException)
        {
            // ConsumeAsync catches every exception around its own work, so this can only wrap a cancellation
            // of the wait itself - nothing actionable, and Dispose must not throw.
        }
    }

    /// <summary>One provider's matched headers, queued for the background consumer to persist.</summary>
    private readonly record struct CaptureItem(
        string ProviderKey,
        IReadOnlyList<RateLimitHeaderRow> Headers,
        DateTimeOffset ObservedAtUtc,
        TaskCompletionSource Completion);

    /// <summary>Strips CR/LF from a client-controlled provider key so it cannot forge additional log lines.</summary>
    private static string SanitizeForLog(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");
}

/// <summary>
/// Safe no-op default for callers that don't opt into rate-limit header capture (e.g. tests constructing
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/> directly without a DI container) - mirrors
/// <see cref="NullSpendTracker"/>'s "fresh, private, harmless default" pattern.
/// </summary>
public sealed class NullRateLimitHeaderCapture : IRateLimitHeaderCapture
{
    /// <summary>The shared, stateless no-op instance.</summary>
    public static readonly NullRateLimitHeaderCapture Instance = new();

    private NullRateLimitHeaderCapture()
    {
    }

    /// <inheritdoc />
    public Task CaptureAsync(string providerKey, HttpResponseHeaders headers, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
