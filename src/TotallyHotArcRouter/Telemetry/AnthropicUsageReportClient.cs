using System.Globalization;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Fetches Anthropic's own reported per-model daily token usage via
/// <c>GET /v1/organizations/usage_report/messages</c> (<c>docs/router/secrets-at-rest-plan.md</c> §8.1).
/// Shaped like <see cref="AnthropicCostReconciler"/>: the same <c>x-api-key</c>/<c>anthropic-version</c>
/// headers, the same <see cref="CostReconciliationRetryPolicy.SendWithRetryAsync"/>, and the same
/// <c>has_more</c>/<c>next_page</c> pagination loop - but reads a token-usage report instead of a cost
/// report, and requests <c>bucket_width=1d</c> with <c>group_by[]=model</c> so pagination is required even
/// within a single day whenever more than one model was used.
/// </summary>
public sealed class AnthropicUsageReportClient
{
    private const string BaseUrl = "https://api.anthropic.com/v1/organizations/usage_report/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _httpClient;
    private readonly string _adminApiKey;
    private readonly ILogger<AnthropicUsageReportClient>? _logger;

    /// <summary>Initializes a new instance of the <see cref="AnthropicUsageReportClient"/> class.</summary>
    public AnthropicUsageReportClient(HttpClient httpClient, string adminApiKey, ILogger<AnthropicUsageReportClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminApiKey);

        _httpClient = httpClient;
        _adminApiKey = adminApiKey;
        _logger = logger;
    }

    /// <summary>
    /// Fetches per-model daily token usage over <c>[startingDay, endingDayExclusive)</c>, paginating until
    /// <c>has_more</c> is false. A row missing an expected numeric field contributes zero for that field
    /// rather than failing the whole fetch, matching <see cref="AnthropicCostReconciler"/>'s tolerance for
    /// an evolving response shape.
    /// </summary>
    public async Task<IReadOnlyList<ReportedUsageRow>> GetUsageReportAsync(
        DateOnly startingDay, DateOnly endingDayExclusive, CancellationToken cancellationToken = default)
    {
        var startingAt = new DateTimeOffset(startingDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        var endingAt = new DateTimeOffset(endingDayExclusive.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);

        var rows = new List<ReportedUsageRow>();
        string? page = null;

        do
        {
            var uri = $"{BaseUrl}?starting_at={Uri.EscapeDataString(startingAt)}&ending_at={Uri.EscapeDataString(endingAt)}" +
                $"&bucket_width=1d&group_by[]=model" +
                (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");

            using var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
                _httpClient,
                () => BuildRequest(uri),
                logger: _logger,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(body) as JsonObject
                ?? throw new InvalidOperationException("Anthropic usage report response was not a JSON object.");

            rows.AddRange(ParseBuckets(root));

            page = root["has_more"] is JsonValue hasMore && hasMore.TryGetValue<bool>(out var more) && more
                ? root["next_page"]?.GetValue<string>()
                : null;
        }
        while (page is not null);

        return rows;
    }

    /// <summary>Builds a GET request for <paramref name="uri"/> with the Admin API key and version headers this endpoint requires.</summary>
    private HttpRequestMessage BuildRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-api-key", _adminApiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        return request;
    }

    /// <summary>Parses one page's <c>data[].results[]</c> into one <see cref="ReportedUsageRow"/> per (bucket day, model).</summary>
    private static IEnumerable<ReportedUsageRow> ParseBuckets(JsonObject root)
    {
        if (root["data"] is not JsonArray buckets)
        {
            yield break;
        }

        foreach (var bucketNode in buckets)
        {
            if (bucketNode is not JsonObject bucket ||
                bucket["starting_at"] is not JsonValue startingAtValue ||
                !startingAtValue.TryGetValue<string>(out var startingAtText) ||
                !DateTimeOffset.TryParse(startingAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var startingAt) ||
                bucket["results"] is not JsonArray results)
            {
                continue;
            }

            var usageDay = DateOnly.FromDateTime(startingAt.UtcDateTime);

            foreach (var resultNode in results)
            {
                if (resultNode is not JsonObject result || result["model"] is not JsonValue modelValue ||
                    !modelValue.TryGetValue<string>(out var model) || string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                yield return new ReportedUsageRow(
                    usageDay,
                    model,
                    InputTokens: ReadLong(result, "uncached_input_tokens"),
                    OutputTokens: ReadLong(result, "output_tokens"),
                    CacheCreationTokens: ReadCacheCreationTokens(result),
                    CacheReadTokens: ReadLong(result, "cache_read_input_tokens"));
            }
        }
    }

    /// <summary>
    /// Sums cache-creation tokens across every duration bucket in <c>cache_creation</c> (e.g.
    /// <c>ephemeral_5m_input_tokens</c>, <c>ephemeral_1h_input_tokens</c>) when present as a nested object,
    /// or falls back to a flat <c>cache_creation_input_tokens</c> field - tolerant of either shape the API
    /// might use, since a single-field total and a per-duration breakdown both amount to the same figure
    /// here (only the total is persisted).
    /// </summary>
    private static long ReadCacheCreationTokens(JsonObject result)
    {
        if (result["cache_creation"] is JsonObject nested)
        {
            long sum = 0;
            foreach (var property in nested)
            {
                if (property.Value is JsonValue value && value.TryGetValue<long>(out var tokens))
                {
                    sum += tokens;
                }
            }

            return sum;
        }

        return ReadLong(result, "cache_creation_input_tokens");
    }

    /// <summary>Reads a named property from <paramref name="result"/> as a <see langword="long"/>, defaulting to 0 when absent or not numeric.</summary>
    private static long ReadLong(JsonObject result, string propertyName) =>
        result[propertyName] is JsonValue value && value.TryGetValue<long>(out var tokens) ? tokens : 0;
}
