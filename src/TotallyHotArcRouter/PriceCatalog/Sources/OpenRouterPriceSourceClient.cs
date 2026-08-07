using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.PriceCatalog.Sources;

/// <summary>
/// Fetches OpenRouter's public <c>/api/v1/models</c> catalog and normalizes it. OpenRouter publishes
/// per-token prices as JSON strings, keyed by a compound <c>provider/model</c> id - this converts to
/// per-million (D2), splits the id to recover the provider (D7), and skips entries it cannot map rather
/// than guessing.
/// </summary>
/// <remarks>
/// Endpoint verified 2026-07-16 (see <c>docs/router/model-price-catalog.md</c>'s "Still open" section):
/// <c>GET https://openrouter.ai/api/v1/models</c>, public, no auth. The response is
/// <c>{ "data": [ { "id", "pricing": { "prompt", "completion", "input_cache_read", ... } } ] }</c>. Every
/// <c>pricing</c> value is a decimal-string in USD per single token (e.g. <c>"0.000005"</c>), not a number
/// and not per-million - both conversions happen here. OpenRouter publishes no batch rates, so
/// <see cref="TotallyHot.ArcRouter.PriceCatalog.Sources.NormalizedPrice.BatchInputPrice"/> and
/// <see cref="TotallyHot.ArcRouter.PriceCatalog.Sources.NormalizedPrice.BatchOutputPrice"/> are always
/// <see langword="null"/> from this source - "not offered", exactly D7's meaning.
/// </remarks>
public sealed class OpenRouterPriceSourceClient : IPriceSourceClient
{
    /// <summary>The canonical published catalog URL, used when no override is configured.</summary>
    public const string DefaultUrl = "https://openrouter.ai/api/v1/models";

    // Per-token -> per-million: OpenRouter publishes cost per single token; the whole system stores USD per
    // 1,000,000 tokens (D2). This is the one place that conversion happens for this source.
    private const decimal TokensPerMillion = 1_000_000m;

    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly ILogger<OpenRouterPriceSourceClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenRouterPriceSourceClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The shared client from <see cref="PriceSourceRegistry"/>, already carrying the <c>X-Title</c> and
    /// <c>HTTP-Referer</c> attribution headers - the convention these headers originate from.
    /// </param>
    /// <param name="url">The catalog URL (an operator override, or <see cref="DefaultUrl"/>).</param>
    /// <param name="logger">The logger.</param>
    public OpenRouterPriceSourceClient(HttpClient httpClient, string url, ILogger<OpenRouterPriceSourceClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _url = url;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PriceCatalogOptions.OpenRouterSourceName;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NormalizedPrice>> FetchAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(_url, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return Normalize(document.RootElement);
    }

    /// <summary>
    /// Normalizes a parsed OpenRouter catalog document. Split out from the fetch so it can be unit-tested
    /// against a fixture payload without any network I/O.
    /// </summary>
    public IReadOnlyList<NormalizedPrice> Normalize(JsonElement root)
    {
        var prices = new List<NormalizedPrice>();
        var skipped = 0;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("OpenRouter response had no 'data' array; nothing to normalize.");
            return prices;
        }

        foreach (var model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                skipped++;
                continue;
            }

            var id = idElement.GetString();

            // The id is "provider/model" (e.g. "anthropic/claude-opus-4"). Provider is mandatory under D7:
            // without it the row cannot be keyed. An id with no separator, or an empty provider segment, is
            // skipped rather than guessed at.
            var separatorIndex = id?.IndexOf('/', StringComparison.Ordinal) ?? -1;
            if (string.IsNullOrEmpty(id) || separatorIndex <= 0 || separatorIndex == id.Length - 1)
            {
                skipped++;
                continue;
            }

            var provider = id[..separatorIndex];

            if (!model.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            var standardInput = ReadPerMillion(pricing, "prompt");
            var standardOutput = ReadPerMillion(pricing, "completion");

            if (standardInput is null && standardOutput is null)
            {
                // No token pricing at all (e.g. a moderation-only or free-tier stub). Nothing to store.
                skipped++;
                continue;
            }

            prices.Add(new NormalizedPrice(
                ModelIdentifier: id,
                Provider: provider,
                StandardInputPrice: standardInput,
                StandardOutputPrice: standardOutput,
                CachedInputPrice: ReadPerMillion(pricing, "input_cache_read"),
                BatchInputPrice: null,
                BatchOutputPrice: null,
                CacheWriteInputPrice: ReadPerMillion(pricing, "input_cache_write")));
        }

        if (skipped > 0)
        {
            _logger.LogInformation(
                "OpenRouter normalization skipped {SkippedCount} unmappable entries, kept {KeptCount}.",
                skipped,
                prices.Count);
        }

        return prices;
    }

    /// <summary>
    /// Parses a per-token cost field published as a decimal string and converts it to USD per 1,000,000
    /// tokens. Absent, non-string, or unparsable values stay absent rather than being coerced to zero, while
    /// a genuine "0" string round-trips to a real, deliberate zero.
    /// </summary>
    // OpenRouter publishes pricing fields as decimal STRINGS ("0.000005"), not JSON numbers - unlike
    // LiteLLM's per-token fields. Absent, non-string, or unparsable stays absent (never coerced to zero,
    // per D7), and a "0" string round-trips to a real, deliberate zero rather than "not offered".
    private static decimal? ReadPerMillion(JsonElement pricing, string fieldName)
    {
        if (!pricing.TryGetProperty(fieldName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text) ||
            !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken))
        {
            return null;
        }

        return perToken * TokensPerMillion;
    }
}

