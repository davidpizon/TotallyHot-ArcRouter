using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Reconciles Anthropic's own reported organization cost against the local ledger estimate, via
/// <c>GET /v1/organizations/cost_report</c> (<c>docs/router/agent-cost-tracking.md</c> §3.5). Requires a
/// separate, more privileged Admin API key than the per-provider inference key already configured on the
/// <c>anthropic</c> provider. Anthropic's own docs note usage/cost data typically appears within ~5
/// minutes of a request completing, though delays can occasionally be longer - one more reason
/// reconciliation always targets a fully-closed prior day rather than "right now".
/// </summary>
public sealed class AnthropicCostReconciler : IProviderCostReconciler
{
    private const string BaseUrl = "https://api.anthropic.com/v1/organizations/cost_report";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _httpClient;
    private readonly string _adminApiKey;
    private readonly ILogger<AnthropicCostReconciler>? _logger;

    /// <summary>Initializes a new instance of the <see cref="AnthropicCostReconciler"/> class.</summary>
    public AnthropicCostReconciler(HttpClient httpClient, string adminApiKey, ILogger<AnthropicCostReconciler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminApiKey);

        _httpClient = httpClient;
        _adminApiKey = adminApiKey;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Provider => "anthropic";

    /// <inheritdoc />
    public async Task<decimal> GetReportedCostAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var startingAt = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        var endingAt = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);

        decimal total = 0m;
        string? page = null;

        do
        {
            var uri = $"{BaseUrl}?starting_at={Uri.EscapeDataString(startingAt)}&ending_at={Uri.EscapeDataString(endingAt)}" +
                (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");

            using var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
                _httpClient,
                () => BuildRequest(uri),
                logger: _logger,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(body) as JsonObject
                ?? throw new InvalidOperationException("Anthropic cost report response was not a JSON object.");

            total += SumReportResults(root);

            page = root["has_more"] is JsonValue hasMore && hasMore.TryGetValue<bool>(out var more) && more
                ? root["next_page"]?.GetValue<string>()
                : null;
        }
        while (page is not null);

        return total;
    }

    private HttpRequestMessage BuildRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-api-key", _adminApiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        return request;
    }

    /// <summary>
    /// Sums every report entry's <c>results[].amount</c> in one page of the response. Anthropic reports
    /// <c>amount</c> as a decimal string (not a nested object, unlike OpenAI's shape) - parsed directly
    /// with <see cref="CultureInfo.InvariantCulture"/> so a locale-formatted decimal never misparses.
    /// </summary>
    private static decimal SumReportResults(JsonObject root)
    {
        if (root["data"] is not JsonArray entries)
        {
            return 0m;
        }

        decimal sum = 0m;
        foreach (var entryNode in entries)
        {
            if (entryNode is not JsonObject entry || entry["results"] is not JsonArray results)
            {
                continue;
            }

            foreach (var resultNode in results)
            {
                if (resultNode is not JsonObject result || result["amount"] is not JsonValue amountValue)
                {
                    continue;
                }

                if (amountValue.TryGetValue<decimal>(out var numeric))
                {
                    sum += numeric;
                }
                else if (amountValue.TryGetValue<string>(out var text) &&
                    decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                {
                    sum += parsed;
                }
            }
        }

        return sum;
    }
}
