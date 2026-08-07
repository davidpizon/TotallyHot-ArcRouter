using System.Net.Http.Headers;
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
    /// <paramref name="providerKey"/>. Best-effort: never throws, and the returned task completes without
    /// waiting for the SQLite write, so callers on the request path are never delayed by a slow disk.
    /// </summary>
    /// <param name="providerKey">The provider key the response came from.</param>
    /// <param name="headers">The upstream response's headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CaptureAsync(string providerKey, HttpResponseHeaders headers, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRateLimitHeaderCapture" />
public sealed class RateLimitHeaderCapture : IRateLimitHeaderCapture
{
    private static readonly string[] HeaderPrefixes = ["anthropic-ratelimit-", "x-ratelimit-"];

    private readonly PriceCatalogRepository _repository;
    private readonly ILogger<RateLimitHeaderCapture>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitHeaderCapture"/> class.
    /// </summary>
    public RateLimitHeaderCapture(PriceCatalogRepository repository, ILogger<RateLimitHeaderCapture>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _logger = logger;
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

        var observedAt = DateTimeOffset.UtcNow;

        // The repository call is a synchronous SQLite write. Task.Run moves it off the caller's async
        // continuation onto the thread pool, and the exception handling lives inside the queued work so a
        // storage hiccup is logged here rather than becoming an unobserved task exception - the caller is
        // free to not await the returned task without losing error handling.
        return Task.Run(() =>
        {
            try
            {
                _repository.UpsertRateLimitHeaders(providerKey, matched, observedAt);
            }
            catch (Exception ex)
            {
                // Best-effort: a storage hiccup here must never fail a request that already succeeded upstream.
                _logger?.LogWarning(ex, "Failed to capture rate-limit headers for provider {Provider}.", SanitizeForLog(providerKey));
            }
        }, cancellationToken);
    }

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
