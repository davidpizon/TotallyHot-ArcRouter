using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Reconciles OpenAI's own reported organization cost against the local ledger estimate, via
/// <c>GET /v1/organization/costs</c> (<c>docs/router/agent-cost-tracking.md</c> §3.5). Requires a
/// separate, more privileged Admin API key than the per-provider inference key already configured on
/// the <c>openai</c> provider's <see cref="TotallyHot.ArcRouter.Models.ProviderOptions.Headers"/>.
/// </summary>
public sealed class OpenAiCostReconciler : IProviderCostReconciler
{
    private const string BaseUrl = "https://api.openai.com/v1/organization/costs";

    private readonly HttpClient _httpClient;
    private readonly string _adminApiKey;
    private readonly ILogger<OpenAiCostReconciler>? _logger;

    /// <summary>Initializes a new instance of the <see cref="OpenAiCostReconciler"/> class.</summary>
    public OpenAiCostReconciler(HttpClient httpClient, string adminApiKey, ILogger<OpenAiCostReconciler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminApiKey);

        _httpClient = httpClient;
        _adminApiKey = adminApiKey;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Provider => "openai";

    /// <inheritdoc />
    public async Task<decimal> GetReportedCostAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var startUnix = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var endUnix = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

        decimal total = 0m;
        string? page = null;

        do
        {
            var uri = $"{BaseUrl}?start_time={startUnix}&end_time={endUnix}&bucket_width=1d" +
                (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");

            using var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
                _httpClient,
                () => BuildRequest(uri),
                logger: _logger,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(body) as JsonObject
                ?? throw new InvalidOperationException("OpenAI costs response was not a JSON object.");

            total += SumBucketResults(root);

            page = root["has_more"] is JsonValue hasMore && hasMore.TryGetValue<bool>(out var more) && more
                ? root["next_page"]?.GetValue<string>()
                : null;
        }
        while (page is not null);

        return total;
    }

    /// <summary>Builds the GET request for <paramref name="uri"/>, authorized with the Admin API key.</summary>
    private HttpRequestMessage BuildRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _adminApiKey);
        return request;
    }

    /// <summary>Sums every bucket's <c>results[].amount.value</c> in one page of the response.</summary>
    private static decimal SumBucketResults(JsonObject root)
    {
        if (root["data"] is not JsonArray buckets)
        {
            return 0m;
        }

        decimal sum = 0m;
        foreach (var bucketNode in buckets)
        {
            if (bucketNode is not JsonObject bucket || bucket["results"] is not JsonArray results)
            {
                continue;
            }

            foreach (var resultNode in results)
            {
                if (resultNode is JsonObject result &&
                    result["amount"] is JsonObject amount &&
                    amount["value"] is JsonValue valueNode &&
                    valueNode.TryGetValue<decimal>(out var value))
                {
                    sum += value;
                }
            }
        }

        return sum;
    }
}
