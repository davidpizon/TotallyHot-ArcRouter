using System.Globalization;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// A thin, platform-agnostic HTTP/JSON client for the proxy's <c>/admin/usage/*</c> query surface
/// (Phase 4, §5.15). Mirrors <see cref="ProviderAdminClient"/>'s shape exactly - same base-address/token
/// setup, same error handling - so both clients read as one family; <c>Gui.Services.UsageStore</c> wraps an
/// instance of this the way <c>ProviderAdminStore</c> wraps <see cref="ProviderAdminClient"/>.
/// </summary>
public sealed class UsageQueryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string? _adminToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageQueryClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client to send requests with. Its <see cref="HttpClient.BaseAddress"/> must be set to the
    /// proxy's management origin (e.g. <c>http://localhost:5001/</c>, with a trailing slash).
    /// </param>
    /// <param name="adminToken">
    /// Optional management token; when set, it is sent in the <c>X-Admin-Token</c> header on every request.
    /// </param>
    public UsageQueryClient(HttpClient httpClient, string? adminToken = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _adminToken = adminToken;
    }

    /// <summary>Gets totals for a preset window - the header ticker and summary tiles.</summary>
    /// <param name="window">One of <c>"day"</c>, <c>"week"</c>, <c>"month"</c>, or <c>"all"</c>.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ProviderAdminException">Usage rollups are unavailable or the request failed.</exception>
    public async Task<UsageSummaryView> GetSummaryAsync(string window, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"admin/usage/summary?window={Uri.EscapeDataString(window)}");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<UsageSummaryView>(body);
    }

    /// <summary>Gets the Model Distribution / Cost Analytics chart feed over an explicit range.</summary>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Exclusive range end.</param>
    /// <param name="width">Bucket width: <c>"hour"</c> or <c>"day"</c>.</param>
    /// <param name="groupBy"><c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ProviderAdminException">Usage rollups are unavailable, the range/parameters were rejected, or the request failed.</exception>
    public async Task<IReadOnlyList<UsageRollupBucketView>> GetRollupAsync(
        DateTimeOffset from, DateTimeOffset to, string width, string groupBy, CancellationToken cancellationToken = default)
    {
        var url = "admin/usage/rollup" +
            $"?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&width={Uri.EscapeDataString(width)}&groupBy={Uri.EscapeDataString(groupBy)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<List<UsageRollupBucketView>>(body);
    }

    /// <summary>
    /// Attaches the admin token (if configured), sends the request, and translates transport failures or
    /// non-success responses into a <see cref="ProviderAdminException"/>. Identical to
    /// <see cref="ProviderAdminClient"/>'s private helper of the same shape.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_adminToken))
        {
            request.Headers.TryAddWithoutValidation("X-Admin-Token", _adminToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderAdminException($"Could not reach the proxy management API: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new ProviderAdminException(ExtractErrorMessage(errorBody, statusCode));
        }

        return response;
    }

    private static T Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                ?? throw new ProviderAdminException("The proxy management API returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new ProviderAdminException($"The proxy management API returned an unreadable response: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Pulls the <c>error.message</c> out of the proxy's error envelope
    /// (<c>{ "error": { "message": "..." } }</c>), falling back to the raw body or status code.
    /// </summary>
    private static string ExtractErrorMessage(string body, System.Net.HttpStatusCode statusCode)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error)
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    var text = message.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
            catch (JsonException)
            {
                // Not a JSON envelope; fall through to the raw body below.
            }

            return body;
        }

        return $"The proxy management API returned {(int)statusCode}.";
    }
}
