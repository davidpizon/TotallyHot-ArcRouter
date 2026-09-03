using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// A thin, platform-agnostic HTTP/JSON client for the proxy's <c>/admin/*</c> provider-management API.
/// Lives in this plain <c>net10.0</c> library (not the Windows-only MAUI Gui project) so its logic is
/// unit-tested in CI; the MAUI <c>ProviderAdminStore</c> wraps an instance of it. All requests and
/// responses use the ASP.NET Core minimal-API "web" JSON conventions (camelCase, case-insensitive).
/// </summary>
public sealed class ProviderAdminClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _adminToken;

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderAdminClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client to send requests with. Its <see cref="HttpClient.BaseAddress"/> must be set to the
    /// proxy's management origin (e.g. <c>http://localhost:5001/</c>, with a trailing slash so relative
    /// paths resolve correctly).
    /// </param>
    /// <param name="adminToken">
    /// Optional management token; when set, it is sent in the <c>X-Admin-Token</c> header on every request
    /// (required only when the proxy has <c>Management:Token</c> configured).
    /// </param>
    public ProviderAdminClient(HttpClient httpClient, string? adminToken = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _adminToken = adminToken;
    }

    /// <summary>Lists all configured providers and their models.</summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The configured providers.</returns>
    /// <exception cref="ProviderAdminException">The request failed or the proxy returned an error.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Get, requestUri: "admin/providers");
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Adds or replaces a provider by key.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="body">The provider fields to write.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list.</returns>
    /// <exception cref="ProviderAdminException">The edit was rejected (e.g. validation) or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> UpsertProviderAsync(string key, ProviderWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Put, requestUri: $"admin/providers/{Escape(key)}")
            { Content = JsonBody(body) };
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes a provider by key.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list.</returns>
    /// <exception cref="ProviderAdminException">The removal was rejected (e.g. the provider is unknown) or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> RemoveProviderAsync(string key,
        CancellationToken cancellationToken = default)
    {
        using var request =
            new HttpRequestMessage(method: HttpMethod.Delete, requestUri: $"admin/providers/{Escape(key)}");
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Adds or replaces a model under a provider.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="body">The model fields to write.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list.</returns>
    /// <exception cref="ProviderAdminException">The edit was rejected or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> UpsertModelAsync(string key, string modelName,
        ModelWriteRequest body, CancellationToken cancellationToken = default)
    {
        using var request =
            new HttpRequestMessage(method: HttpMethod.Put,
                    requestUri: $"admin/providers/{Escape(key)}/models/{Escape(modelName)}")
                { Content = JsonBody(body) };
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes a model under a provider.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list.</returns>
    /// <exception cref="ProviderAdminException">The removal was rejected or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> RemoveModelAsync(string key, string modelName,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Delete,
            requestUri: $"admin/providers/{Escape(key)}/models/{Escape(modelName)}");
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Switches a model on or off - the per-model twin of <see cref="SetEnabledAsync"/>.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="body">The new on/off state.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list, now carrying the new state.</returns>
    /// <exception cref="ProviderAdminException">The model is unknown or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> SetModelEnabledAsync(string key, string modelName,
        ModelEnabledWriteRequest body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Put,
                requestUri: $"admin/providers/{Escape(key)}/models/{Escape(modelName)}/enabled")
            { Content = JsonBody(body) };
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pins how a model expresses tool calls, overriding automatic detection - the equivalent of LiteLLM's
    /// <c>register_model(..., supports_function_calling=…)</c>.
    /// </summary>
    /// <param name="key">The provider key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="body">The dialect to pin, or a null/empty dialect to clear the pin and resume detection.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list, now carrying the pinned dialect.</returns>
    /// <exception cref="ProviderAdminException">The model is unknown, the dialect is unrecognized, or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> SetModelToolDialectAsync(string key, string modelName,
        ModelToolDialectWriteRequest body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Put,
                requestUri: $"admin/providers/{Escape(key)}/models/{Escape(modelName)}/tool-dialect")
            { Content = JsonBody(body) };
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Sets or clears a provider's monthly budget caps.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="body">The caps to write (null clears a dimension; both null removes the budget).</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list, now carrying the new caps and current-month spend.</returns>
    /// <exception cref="ProviderAdminException">The edit was rejected (e.g. a negative cap) or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> SetBudgetAsync(string key, ProviderBudgetWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        using var request =
            new HttpRequestMessage(method: HttpMethod.Put, requestUri: $"admin/providers/{Escape(key)}/budget")
                { Content = JsonBody(body) };
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Switches a provider on or off.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="body">The new on/off state.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list, now carrying the new state.</returns>
    /// <exception cref="ProviderAdminException">The provider is unknown or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> SetEnabledAsync(string key, ProviderEnabledWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        using var request =
            new HttpRequestMessage(method: HttpMethod.Put, requestUri: $"admin/providers/{Escape(key)}/enabled")
                { Content = JsonBody(body) };
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Queries a provider's own model list (live discovery).</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>
    /// The discovery result (which reports <see cref="DiscoverModelsResult.Supported"/> when the provider has no
    /// OpenAI-shaped endpoint).
    /// </returns>
    /// <exception cref="ProviderAdminException">The request itself failed (e.g. unknown provider, transport error).</exception>
    public async Task<DiscoverModelsResult> DiscoverModelsAsync(string key,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Post,
            requestUri: $"admin/providers/{Escape(key)}/discover-models");
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<DiscoverModelsResult>(body);
    }

    /// <summary>
    /// Probes a provider's endpoint for which API flavors it answers, and - riding on whatever metadata
    /// that exposes - runs tier 1-3 tool-call dialect detection for every model routed to it
    /// (<c>docs/router/tool-call-normalization.md</c> §3.2-3.3). An independently callable building block;
    /// the Governance UI's "Refresh from endpoint" action calls <see cref="RefreshFromEndpointAsync"/>
    /// instead, which also reconciles the model list. The caller should reload the provider list afterward
    /// (e.g. via <c>ProviderAdminStore.LoadAsync</c>) to see any newly-detected
    /// <see cref="ModelAdminView.Dialect"/> values, since this call itself returns only the endpoint-flavor
    /// result, not the updated snapshot.
    /// </summary>
    /// <param name="key">The provider key to scan.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>Which API flavors the endpoint answered, and when the scan ran.</returns>
    /// <exception cref="ProviderAdminException">The provider is unknown, scanning is unavailable, or the request failed.</exception>
    public async Task<ProviderEndpointCapabilitiesView> ScanCapabilitiesAsync(string key,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Post,
            requestUri: $"admin/providers/{Escape(key)}/scan-capabilities");
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<ProviderEndpointCapabilitiesView>(body);
    }

    /// <summary>
    /// The Governance UI's "Refresh from endpoint" action: discovers the provider's live model list,
    /// reconciles it into configuration (adding newly-seen ids as stopped, flagging previously-configured
    /// ones no longer reported - never deleting), then re-scans endpoint flavors and re-runs dialect
    /// detection. One round trip in place of separately calling <see cref="DiscoverModelsAsync"/> and
    /// <see cref="ScanCapabilitiesAsync"/> - the reconciliation itself only happens on the router.
    /// </summary>
    /// <param name="key">The provider key to refresh.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list, carrying any newly-added/flagged models and refreshed capability data.</returns>
    /// <exception cref="ProviderAdminException">The provider is unknown or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> RefreshFromEndpointAsync(string key,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Post,
            requestUri: $"admin/providers/{Escape(key)}/refresh-from-endpoint");
        return await SendForProvidersAsync(request: request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists every configured price override (§5.7's operator-override rung), for the Governance
    /// price-overrides pane's read-only diagnosis view.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The configured overrides.</returns>
    /// <exception cref="ProviderAdminException">Overrides are unavailable or the request failed.</exception>
    public async Task<IReadOnlyList<PriceOverrideView>> GetPriceOverridesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Get, requestUri: "admin/price-overrides");
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<List<PriceOverrideView>>(body);
    }

    /// <summary>Adds or replaces a price override.</summary>
    /// <param name="body">
    /// The override to write; <see cref="PriceOverrideWriteRequest.ModelName"/> must name an
    /// already-configured model.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated override list.</returns>
    /// <exception cref="ProviderAdminException">The edit was rejected (e.g. an unknown model) or the request failed.</exception>
    public async Task<IReadOnlyList<PriceOverrideView>> SetPriceOverrideAsync(PriceOverrideWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Put, requestUri: "admin/price-overrides")
            { Content = JsonBody(body) };
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<List<PriceOverrideView>>(responseBody);
    }

    /// <summary>Removes a price override.</summary>
    /// <param name="sourceName">The aggregator source the override applies to.</param>
    /// <param name="aggregatorModelKey">The source's own model key the override matches.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated override list.</returns>
    /// <exception cref="ProviderAdminException">No override matched, overrides are unavailable, or the request failed.</exception>
    public async Task<IReadOnlyList<PriceOverrideView>> RemovePriceOverrideAsync(string sourceName,
        string aggregatorModelKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            method: HttpMethod.Delete,
            requestUri:
            $"admin/price-overrides?sourceName={Escape(sourceName)}&aggregatorModelKey={Escape(aggregatorModelKey)}");
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<List<PriceOverrideView>>(body);
    }

    /// <summary>
    /// Gets, per configured model, whether the catalog currently resolves a price for it and via an exact
    /// or approximate match - the Governance price-overrides pane's read-only diagnosis view.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The resolution state of every configured model.</returns>
    /// <exception cref="ProviderAdminException">The price catalog is unavailable or the request failed.</exception>
    public async Task<IReadOnlyList<PriceResolutionDiagnosisView>> GetPriceResolutionDiagnosisAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Get, requestUri: "admin/price-resolution");
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<List<PriceResolutionDiagnosisView>>(body);
    }

    /// <summary>
    /// Gets a provider's rate-limit remaining-over-time history, per dimension - the Providers card's trend
    /// chart data source (§5.9).
    /// </summary>
    /// <param name="key">The provider key.</param>
    /// <param name="hours">How far back to look, in hours (default 6).</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The per-dimension history series.</returns>
    /// <exception cref="ProviderAdminException">The provider is unknown, history is unavailable, or the request failed.</exception>
    public async Task<RateLimitHistoryResponseAdminView> GetRateLimitHistoryAsync(string key, double hours = 6.0,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            method: HttpMethod.Get,
            requestUri:
            $"admin/providers/{Escape(key)}/rate-limit-history?hours={hours.ToString(CultureInfo.InvariantCulture)}");
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<RateLimitHistoryResponseAdminView>(body);
    }

    /// <summary>
    /// Stores <paramref name="provider"/>'s reconciliation Admin API key in the proxy's protected secret
    /// store (docs/router/secrets-at-rest-plan.md §7), taking effect on the next reconciliation cycle with
    /// no restart required. Only <c>openai</c> and <c>anthropic</c> are recognized.
    /// </summary>
    /// <param name="provider">The reconciliation provider key (<c>openai</c> or <c>anthropic</c>).</param>
    /// <param name="value">The Admin API key to store.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ProviderAdminException">The provider is unrecognized, the store is unavailable, or the request failed.</exception>
    public async Task SetAdminApiKeyAsync(string provider, string value, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Put,
            requestUri: $"admin/secrets/{Escape(AdminApiKeySecretName(provider))}")
        {
            Content = JsonBody(new SecretWriteRequest(value))
        };
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears <paramref name="provider"/>'s stored reconciliation Admin API key, the counterpart to
    /// <see cref="SetAdminApiKeyAsync"/>.
    /// </summary>
    /// <param name="provider">The reconciliation provider key (<c>openai</c> or <c>anthropic</c>).</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ProviderAdminException">The provider is unrecognized, the store is unavailable, or the request failed.</exception>
    public async Task DeleteAdminApiKeyAsync(string provider, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method: HttpMethod.Delete,
            requestUri: $"admin/secrets/{Escape(AdminApiKeySecretName(provider))}");
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The protected-store name for a provider's reconciliation Admin API key, matching <c>ManagementFacade</c>'s
    /// naming convention (docs/router/secrets-at-rest-plan.md §3).
    /// </summary>
    private static string AdminApiKeySecretName(string provider)
    {
        return $"reconciliation:{provider}:admin-key";
    }

    /// <summary>
    /// Sends a request expected to return a provider snapshot and unwraps its
    /// <see cref="ProvidersSnapshot.Providers"/> list.
    /// </summary>
    private async Task<IReadOnlyList<ProviderAdminView>> SendForProvidersAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response =
            await SendAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize<ProvidersSnapshot>(body).Providers;
    }

    /// <summary>
    /// Attaches the admin token (if configured), sends the request, and translates transport failures or
    /// non-success responses into a <see cref="ProviderAdminException"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_adminToken))
            request.Headers.TryAddWithoutValidation(name: "X-Admin-Token", value: _adminToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request: request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderAdminException(message: $"Could not reach the proxy management API: {ex.Message}",
                innerException: ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new ProviderAdminException(ExtractErrorMessage(body: errorBody, statusCode: statusCode));
        }

        return response;
    }

    /// <summary>Serializes a request body to a JSON <see cref="StringContent"/> using the client's web-JSON conventions.</summary>
    private static StringContent JsonBody<T>(T value)
    {
        return new StringContent(content: JsonSerializer.Serialize(value: value, options: JsonOptions),
            encoding: Encoding.UTF8,
            mediaType: "application/json");
    }

    /// <summary>
    /// Deserializes a response body, translating an empty body or malformed JSON into a
    /// <see cref="ProviderAdminException"/> rather than letting a null-reference or raw <see cref="JsonException"/> surface to
    /// the caller.
    /// </summary>
    private static T Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json: body, options: JsonOptions)
                   ?? throw new ProviderAdminException("The proxy management API returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new ProviderAdminException(
                message: $"The proxy management API returned an unreadable response: {ex.Message}", innerException: ex);
        }
    }

    /// <summary>
    /// Pulls the <c>error.message</c> out of the proxy's error envelope
    /// (<c>{ "error": { "message": "..." } }</c>), falling back to the raw body or status code.
    /// </summary>
    private static string ExtractErrorMessage(string body, HttpStatusCode statusCode)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty(propertyName: "error", value: out var error)
                    && error.TryGetProperty(propertyName: "message", value: out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    var text = message.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
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

    /// <summary>URL-escapes a path segment (e.g. a provider key) for safe inclusion in a request URI.</summary>
    private static string Escape(string segment)
    {
        return Uri.EscapeDataString(segment);
    }
}