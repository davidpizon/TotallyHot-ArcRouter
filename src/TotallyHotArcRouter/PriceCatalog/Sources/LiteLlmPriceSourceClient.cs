using System.Text.Json;

namespace TotallyHot.ArcRouter.PriceCatalog.Sources;

/// <summary>
/// Fetches LiteLLM's public <c>model_prices_and_context_window.json</c> and normalizes it. LiteLLM
/// publishes per-token figures keyed by its own model name, each carrying a <c>litellm_provider</c>
/// field - this converts to per-million (D2), carries the provider through (D7), and skips entries it
/// cannot map rather than guessing.
/// </summary>
/// <remarks>
/// LiteLLM is the only active source today (see <c>docs/router/model-price-catalog.md</c>'s "Current
/// scope"). Its JSON is a flat object of <c>{ modelName: { input_cost_per_token, output_cost_per_token,
/// litellm_provider, ... } }</c>, plus a <c>sample_spec</c> documentation sentinel that is not a model.
/// </remarks>
public sealed class LiteLlmPriceSourceClient : IPriceSourceClient
{
    /// <summary>The canonical published catalog URL, used when no override is configured.</summary>
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    // Per-token -> per-million: LiteLLM publishes cost per single token; the whole system stores USD per
    // 1,000,000 tokens (D2). This is the one place that conversion happens.
    private const decimal TokensPerMillion = 1_000_000m;

    // A documentation-only entry in the JSON, not a real model.
    private const string SampleSpecKey = "sample_spec";

    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly ILogger<LiteLlmPriceSourceClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteLlmPriceSourceClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The shared client from <see cref="PriceSourceRegistry"/>, already carrying the <c>X-Title</c> and
    /// <c>HTTP-Referer</c> attribution headers.
    /// </param>
    /// <param name="url">The catalog URL (an operator override, or <see cref="DefaultUrl"/>).</param>
    /// <param name="logger">The logger.</param>
    public LiteLlmPriceSourceClient(HttpClient httpClient, string url, ILogger<LiteLlmPriceSourceClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _url = url;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PriceCatalogOptions.LiteLlmSourceName;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NormalizedPrice>> FetchAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(_url, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return Normalize(document.RootElement);
    }

    /// <summary>
    /// Normalizes a parsed LiteLLM catalog document. Split out from the fetch so it can be unit-tested
    /// against a fixture payload without any network I/O.
    /// </summary>
    public IReadOnlyList<NormalizedPrice> Normalize(JsonElement root)
    {
        var prices = new List<NormalizedPrice>();
        var skipped = 0;

        foreach (var entry in root.EnumerateObject())
        {
            if (string.Equals(entry.Name, SampleSpecKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            var model = entry.Value;

            // Provider is mandatory under D7: without it the row cannot be keyed. An entry that omits it
            // (or offers no standard rate to store) is skipped rather than guessed.
            if (!model.TryGetProperty("litellm_provider", out var providerElement) ||
                providerElement.ValueKind != JsonValueKind.String)
            {
                skipped++;
                continue;
            }

            var provider = providerElement.GetString();
            if (string.IsNullOrWhiteSpace(provider))
            {
                skipped++;
                continue;
            }

            var standardInput = ReadPerMillion(model, "input_cost_per_token");
            var standardOutput = ReadPerMillion(model, "output_cost_per_token");

            if (standardInput is null && standardOutput is null)
            {
                // No token pricing at all (e.g. a non-priced capability stub). Nothing to store.
                skipped++;
                continue;
            }

            prices.Add(new NormalizedPrice(
                ModelIdentifier: entry.Name,
                Provider: provider,
                StandardInputPrice: standardInput,
                StandardOutputPrice: standardOutput,
                CachedInputPrice: ReadPerMillion(model, "cache_read_input_token_cost"),
                BatchInputPrice: ReadPerMillion(model, "input_cost_per_token_batches"),
                BatchOutputPrice: ReadPerMillion(model, "output_cost_per_token_batches"),
                CacheWriteInputPrice: ReadPerMillion(model, "cache_creation_input_token_cost")));
        }

        if (skipped > 0)
        {
            _logger.LogInformation(
                "LiteLLM normalization skipped {SkippedCount} unmappable entries, kept {KeptCount}.",
                skipped,
                prices.Count);
        }

        return prices;
    }

    /// <summary>
    /// Reads a per-token cost field and converts it to USD per 1,000,000 tokens. Returns null when the
    /// field is absent or not a number, so absent stays absent rather than being coerced to zero.
    /// </summary>
    // Reads a per-token cost field and converts to USD per 1,000,000 tokens (D2). Returns null when the
    // field is absent or not a number, so absent stays absent (never coerced to zero, per D7).
    private static decimal? ReadPerMillion(JsonElement model, string fieldName)
    {
        if (!model.TryGetProperty(fieldName, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDecimal(out var perToken))
        {
            return null;
        }

        return perToken * TokensPerMillion;
    }
}

