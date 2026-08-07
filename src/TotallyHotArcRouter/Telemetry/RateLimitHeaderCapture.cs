using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Captures every upstream response header whose name starts with <c>anthropic-ratelimit-</c>
/// (case-insensitive) and persists it verbatim, mirroring <see cref="IUsageExtractor"/>'s
/// provider-dispatch design. Prefix capture, not a hardcoded header name list, is what makes this generic
/// over both the standard (<c>x-api-key</c>) and subscription-OAuth-unified header families without any
/// per-account configuration - and is what will let a future OpenAI-<c>x-ratelimit-*</c> capture share this
/// same seam with just a second prefix.
/// </summary>
public interface IRateLimitHeaderCapture
{
    /// <summary>
    /// Captures <paramref name="headers"/>'s <c>anthropic-ratelimit-*</c> entries for <paramref name="providerKey"/>.
    /// Best-effort: never throws, and never delays or fails a request that already succeeded upstream.
    /// </summary>
    /// <param name="providerKey">The provider key the response came from.</param>
    /// <param name="headers">The upstream response's headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CaptureAsync(string providerKey, HttpResponseHeaders headers, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRateLimitHeaderCapture" />
public sealed class RateLimitHeaderCapture : IRateLimitHeaderCapture
{
    private const string HeaderPrefix = "anthropic-ratelimit-";

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
            if (!header.Key.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
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

        // Synchronous SQLite write, like every other price-catalog repository call on this path
        // (ProviderBudgetStore.RecordUsageAsync wraps the same kind of call) - offloaded so a slow disk
        // never delays the response already being written to the client.
        try
        {
            _repository.UpsertRateLimitHeaders(providerKey, matched, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            // Best-effort, same contract as RecordUsageAsync: a storage hiccup here must never fail a
            // request that already succeeded upstream.
            _logger?.LogWarning(ex, "Failed to capture rate-limit headers for provider {Provider}.", SanitizeForLog(providerKey));
        }

        return Task.CompletedTask;
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
