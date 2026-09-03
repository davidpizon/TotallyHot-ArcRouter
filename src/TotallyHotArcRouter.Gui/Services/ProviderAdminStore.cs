using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Admin;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's provider/credential/model management. Wraps
/// <see cref="ProviderAdminClient"/> (the tested, platform-agnostic logic in TotallyHot.ArcRouter.Gui.Admin)
/// with the same "singleton + Changed event + best-effort, reachability-tolerant" shape as
/// <see cref="LiveDataStore"/>, so the UI survives tab switches and degrades gracefully when the proxy
/// isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class ProviderAdminStore : IDisposable
{
    /// <summary>
    /// The proxy's plain-HTTP management origin. The management API shares the LLM-forwarding port
    /// (5001), distinct from the TLS gRPC telemetry port (5002) <see cref="LiveDataStore"/> uses.
    /// </summary>
    public const string DefaultManagementAddress = "http://localhost:5001";

    private readonly ProviderAdminClient _client;
    private readonly ILogger<ProviderAdminStore>? _logger;

    // This store always builds its own HttpClient (even over a caller-supplied transport), so it always
    // owns that client's lifetime - see Dispose. Mirrors UpdateStore's _ownedHttpClient.
    private readonly HttpClient _ownedHttpClient;

    private readonly ConcurrentDictionary<string, RateLimitHistoryResponseAdminView> _rateLimitHistory = new();
    private readonly ToastService? _toasts;

    /// <summary>Initializes a new instance of the <see cref="ProviderAdminStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="managementAddress">The proxy management origin; defaults to <see cref="DefaultManagementAddress"/>.</param>
    /// <param name="adminToken">
    /// Optional management token override; when null (the default), the token is read from the shared
    /// <c>%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt</c> file the proxy generates (see
    /// <see cref="ManagementTokenReader"/>). The REST <c>/admin/*</c> API requires this token by default.
    /// </param>
    /// <param name="transport">
    /// The HTTP transport to send through; <see langword="null"/> (the default, and always the case in
    /// production) uses the framework's own. This exists so tests can render the Governance tab against a
    /// canned provider list: without it the store builds its own <see cref="HttpClient"/> and the only
    /// reachable state in a test process is "connection refused", which leaves the entire loaded UI - the
    /// provider cards, the dialogs, every mutation - unexercised. <see cref="ProviderAdminClient"/> already
    /// takes its <see cref="HttpClient"/> from the caller for the same reason; this extends that seam the
    /// one level up that <c>ProvidersAdmin</c> needs.
    /// </param>
    /// <param name="toasts">
    /// App-wide error-toast notifications; <see langword="null"/> (a test's default) simply skips raising
    /// toasts. See <see cref="ToastService"/>.
    /// </param>
    public ProviderAdminStore(
        ILogger<ProviderAdminStore>? logger = null,
        string managementAddress = DefaultManagementAddress,
        string? adminToken = null,
        HttpMessageHandler? transport = null,
        ToastService? toasts = null)
    {
        _logger = logger;
        _toasts = toasts;
        var normalized = managementAddress.EndsWith('/') ? managementAddress : managementAddress + "/";

        // disposeHandler: false for a caller-supplied transport. HttpClient's single-argument constructor
        // defaults to disposing its handler, which would reach past this store's own client and dispose a
        // test's handler out from under it. This store owns the client it built; it never owns the
        // transport it was handed.
        var httpClient = transport is null ? new HttpClient() : new HttpClient(handler: transport, false);
        httpClient.BaseAddress = new Uri(normalized);
        _ownedHttpClient = httpClient;
        _client = new ProviderAdminClient(httpClient: httpClient,
            adminToken: adminToken ?? ManagementTokenReader.TryRead());
    }

    /// <summary>The providers currently known, refreshed after each load or successful edit.</summary>
    public IReadOnlyList<ProviderAdminView> Providers { get; private set; } = [];

    /// <summary>
    /// The configured price overrides (§5.7's operator-override rung), refreshed after each load or
    /// successful edit via <see cref="LoadPriceOverridesAsync"/>. Empty until that is called at least
    /// once - the Governance price-overrides pane loads it independently of <see cref="Providers"/> since
    /// it is a separate sub-view.
    /// </summary>
    public IReadOnlyList<PriceOverrideView> PriceOverrides { get; private set; } = [];

    /// <summary>
    /// Every configured model's current price-resolution state, refreshed by <see cref="LoadPriceOverridesAsync"/>
    /// alongside <see cref="PriceOverrides"/> - the pane's read-only diagnosis view.
    /// </summary>
    public IReadOnlyList<PriceResolutionDiagnosisView> PriceResolutionDiagnosis { get; private set; } = [];

    /// <summary>
    /// Each provider's rate-limit trend-chart history, keyed by provider key, refreshed by
    /// <see cref="LoadRateLimitHistoryAsync"/>. A provider absent here simply hasn't been loaded yet - the
    /// card renders no chart rather than a loading state, since the surrounding provider list has already
    /// loaded by the time this is fetched. Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// because <c>ProvidersAdmin.razor</c> fires <see cref="LoadRateLimitHistoryAsync"/> once per provider,
    /// fire-and-forget - several can complete around the same time and write here concurrently, which a
    /// plain <see cref="Dictionary{TKey,TValue}"/> does not tolerate.
    /// </summary>
    public IReadOnlyDictionary<string, RateLimitHistoryResponseAdminView> RateLimitHistory => _rateLimitHistory;

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load/edit reached the proxy management API.</summary>
    public bool IsReachable { get; private set; }

    /// <summary>The last load error message, if the management API was unreachable.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Disposes the <see cref="HttpClient"/> this store built for itself. A caller-supplied
    /// <c>transport</c> is deliberately left alone - see the constructor's <c>disposeHandler</c> note.
    /// Registered as a DI singleton in <c>MauiProgram</c>, so the container invokes this at shutdown.
    /// </summary>
    public void Dispose()
    {
        _ownedHttpClient.Dispose();
    }

    /// <summary>Raised after <see cref="Providers"/>, <see cref="IsReachable"/>, or <see cref="LastError"/> change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the provider list. Connection failures are swallowed and surfaced via
    /// <see cref="IsReachable"/>/<see cref="LastError"/> rather than thrown, so the tab renders an
    /// "unreachable" state instead of crashing when the proxy isn't running.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Providers = await _client.GetProvidersAsync(cancellationToken);
            IsReachable = true;
            LastError = null;
        }
        catch (ProviderAdminException ex)
        {
            IsReachable = false;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex, message: "Failed to load providers from the management API.");
            _toasts?.ShowError(title: "Providers unreachable", message: ex.Message);
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>Adds or edits a provider, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The edit was rejected; the caller (UI) surfaces the message.</exception>
    public Task UpsertProviderAsync(string key, ProviderWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            _client.UpsertProviderAsync(key: key, body: body, cancellationToken: cancellationToken));
    }

    /// <summary>Removes a provider along with every model routing to it, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The removal was rejected (e.g. the provider is unknown).</exception>
    public Task RemoveProviderAsync(string key, CancellationToken cancellationToken = default)
    {
        return MutateAsync(() => _client.RemoveProviderAsync(key: key, cancellationToken: cancellationToken));
    }

    /// <summary>Adds or edits a model under a provider, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The edit was rejected.</exception>
    public Task UpsertModelAsync(string key, string modelName, ModelWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            _client.UpsertModelAsync(key: key, modelName: modelName, body: body, cancellationToken: cancellationToken));
    }

    /// <summary>Removes a model, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The removal was rejected.</exception>
    public Task RemoveModelAsync(string key, string modelName, CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            _client.RemoveModelAsync(key: key, modelName: modelName, cancellationToken: cancellationToken));
    }

    /// <summary>Sets or clears a provider's monthly budget caps, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The edit was rejected (e.g. a negative cap).</exception>
    public Task SetBudgetAsync(string key, ProviderBudgetWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() => _client.SetBudgetAsync(key: key, body: body, cancellationToken: cancellationToken));
    }

    /// <summary>Switches a provider on or off, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The provider is unknown or the write failed.</exception>
    public Task SetEnabledAsync(string key, ProviderEnabledWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() => _client.SetEnabledAsync(key: key, body: body, cancellationToken: cancellationToken));
    }

    /// <summary>Switches a model on or off, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The model is unknown or the write failed.</exception>
    public Task SetModelEnabledAsync(string key, string modelName, ModelEnabledWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            _client.SetModelEnabledAsync(key: key, modelName: modelName, body: body,
                cancellationToken: cancellationToken));
    }

    /// <summary>Pins (or clears) a model's tool-call dialect, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The model is unknown, the dialect is unrecognized, or the write failed.</exception>
    public Task SetModelToolDialectAsync(string key, string modelName, ModelToolDialectWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            _client.SetModelToolDialectAsync(key: key, modelName: modelName, body: body,
                cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Stores a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md §7), then
    /// reloads the provider list so <see cref="ProviderAdminView.HasStoredAdminKey"/> reflects it. Only
    /// <c>openai</c> and <c>anthropic</c> are recognized.
    /// </summary>
    /// <exception cref="ProviderAdminException">The provider is unrecognized, the store is unavailable, or the write failed.</exception>
    public async Task SetAdminApiKeyAsync(string provider, string value, CancellationToken cancellationToken = default)
    {
        await _client.SetAdminApiKeyAsync(provider: provider, value: value, cancellationToken: cancellationToken);
        await LoadAsync(cancellationToken);
    }

    /// <summary>Clears a provider's stored reconciliation Admin API key, then reloads the provider list.</summary>
    /// <exception cref="ProviderAdminException">The provider is unrecognized, the store is unavailable, or the write failed.</exception>
    public async Task DeleteAdminApiKeyAsync(string provider, CancellationToken cancellationToken = default)
    {
        await _client.DeleteAdminApiKeyAsync(provider: provider, cancellationToken: cancellationToken);
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Queries a provider's own model list (live discovery). An independently callable building block - the
    /// Governance UI's "Refresh from endpoint" action uses <see cref="RefreshFromEndpointAsync"/> instead.
    /// </summary>
    public Task<DiscoverModelsResult> DiscoverModelsAsync(string key, CancellationToken cancellationToken = default)
    {
        return _client.DiscoverModelsAsync(key: key, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Re-probes a provider's endpoint and runs tool-call dialect detection for its models. An independently
    /// callable building block - the Governance UI's "Refresh from endpoint" action uses
    /// <see cref="RefreshFromEndpointAsync"/> instead, which also reconciles the model list. Does not itself
    /// update <see cref="Providers"/> - the scan's own return value is only the endpoint-flavor result.
    /// </summary>
    /// <exception cref="ProviderAdminException">The provider is unknown, scanning is unavailable, or the request failed.</exception>
    public Task<ProviderEndpointCapabilitiesView> ScanCapabilitiesAsync(string key,
        CancellationToken cancellationToken = default)
    {
        return _client.ScanCapabilitiesAsync(key: key, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The Governance UI's "Refresh from endpoint" action, then publishes the updated list: discovers the
    /// provider's live model list, reconciles it into configuration (adding newly-seen ids as stopped,
    /// flagging previously-configured ones no longer reported - never deleting), then re-scans endpoint
    /// flavors and re-runs dialect detection. All of this happens on the router in one request; this method
    /// just triggers it and publishes the fresh result, the same as every other mutation here.
    /// </summary>
    /// <remarks>
    /// This is the one call where a rejected credential (e.g. an expired API key) does not surface as a
    /// thrown <see cref="ProviderAdminException"/> - the request itself still succeeds, since the router
    /// noticed the discovery/scan failed and simply left the model list untouched (see
    /// <c>ManagementFacade.RefreshFromEndpointAsync</c>). The failure instead travels back inside the
    /// refreshed provider's own <see cref="ProviderAdminView.AdminAction"/>, which this method checks
    /// after the mutation to raise the toast the caller would otherwise never see.
    /// </remarks>
    /// <exception cref="ProviderAdminException">The provider is unknown or the request failed outright.</exception>
    public async Task RefreshFromEndpointAsync(string key, CancellationToken cancellationToken = default)
    {
        await MutateAsync(() => _client.RefreshFromEndpointAsync(key: key, cancellationToken: cancellationToken));

        var provider = Providers.FirstOrDefault(p =>
            string.Equals(a: p.Key, b: key, comparisonType: StringComparison.OrdinalIgnoreCase));
        if (provider?.AdminAction is { Ok: false } failure)
            _toasts?.ShowError(title: $"{provider.Name ?? provider.Key}: {failure.Operation} failed",
                message: failure.Message ?? "Unknown error.");
    }

    /// <summary>
    /// Shared implementation behind the provider mutation methods: runs the given write operation and
    /// publishes its returned provider list, letting a <see cref="ProviderAdminException"/> propagate
    /// untouched (after raising a toast) so the caller can also surface it inline.
    /// </summary>
    private async Task MutateAsync(Func<Task<IReadOnlyList<ProviderAdminView>>> mutation)
    {
        try
        {
            // Success publishes the server's returned list; a ProviderAdminException (e.g. a validation 400)
            // propagates so the UI can show the message inline, leaving the current list untouched.
            Providers = await mutation();
            IsReachable = true;
            IsLoaded = true;
            LastError = null;
            Changed?.Invoke();
        }
        catch (ProviderAdminException ex)
        {
            // Every admin pane's mutations flow through this one method, so this is the single place a
            // toast covers a rejected save/refresh/toggle/etc. app-wide - not just the Providers pane.
            _toasts?.ShowError(title: "Action failed", message: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Loads one provider's rate-limit trend-chart history and caches it in <see cref="RateLimitHistory"/>.
    /// Best-effort and per-provider: a failure (e.g. the proxy has no price-catalog repository wired up, so
    /// history is unavailable) is swallowed and simply leaves that provider absent from the cache, rather
    /// than surfacing as a store-wide reachability failure the way <see cref="LoadAsync"/> does - one
    /// provider's missing history shouldn't blank the whole Providers pane.
    /// </summary>
    public async Task LoadRateLimitHistoryAsync(string key, double hours = 6.0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _rateLimitHistory[key] =
                await _client.GetRateLimitHistoryAsync(key: key, hours: hours, cancellationToken: cancellationToken);
            Changed?.Invoke();
        }
        catch (ProviderAdminException ex)
        {
            _logger?.LogDebug(exception: ex, message: "Failed to load rate-limit history for provider {Provider}.",
                key);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // ProviderAdminClient.SendAsync only wraps HttpRequestException into ProviderAdminException;
            // a request timeout surfaces as a raw TaskCanceledException instead. Called fire-and-forget
            // from ProvidersAdmin.razor, so letting this escape would become an unobserved task exception
            // rather than the best-effort no-op this method promises.
            _logger?.LogDebug(exception: ex, message: "Timed out loading rate-limit history for provider {Provider}.",
                key);
        }
    }

    /// <summary>
    /// Loads the price-override list. Same reachability-tolerant shape as <see cref="LoadAsync"/>, kept
    /// separate since the price-overrides pane is a distinct Governance sub-view that shouldn't force a
    /// provider reload (or vice versa).
    /// </summary>
    public async Task LoadPriceOverridesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            PriceOverrides = await _client.GetPriceOverridesAsync(cancellationToken);
            PriceResolutionDiagnosis = await _client.GetPriceResolutionDiagnosisAsync(cancellationToken);
            IsReachable = true;
            LastError = null;
        }
        catch (ProviderAdminException ex)
        {
            IsReachable = false;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex, message: "Failed to load price overrides from the management API.");
            _toasts?.ShowError(title: "Price overrides unreachable", message: ex.Message);
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Adds or replaces a price override, then publishes the updated override list.</summary>
    /// <exception cref="ProviderAdminException">The edit was rejected (e.g. an unconfigured model) or the request failed.</exception>
    public Task SetPriceOverrideAsync(PriceOverrideWriteRequest body, CancellationToken cancellationToken = default)
    {
        return MutatePriceOverridesAsync(
            mutation: () => _client.SetPriceOverrideAsync(body: body, cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);
    }

    /// <summary>Removes a price override, then publishes the updated override list.</summary>
    /// <exception cref="ProviderAdminException">No override matched, or the request failed.</exception>
    public Task RemovePriceOverrideAsync(string sourceName, string aggregatorModelKey,
        CancellationToken cancellationToken = default)
    {
        return MutatePriceOverridesAsync(
            mutation: () => _client.RemovePriceOverrideAsync(sourceName: sourceName,
                aggregatorModelKey: aggregatorModelKey,
                cancellationToken: cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task MutatePriceOverridesAsync(Func<Task<IReadOnlyList<PriceOverrideView>>> mutation,
        CancellationToken cancellationToken)
    {
        try
        {
            PriceOverrides = await mutation();
            // An override can change whether a model resolves (or whether the resolved price is approximate),
            // so the diagnosis view has to be refreshed alongside the override list itself, not just on the
            // next explicit LoadPriceOverridesAsync call.
            PriceResolutionDiagnosis = await _client.GetPriceResolutionDiagnosisAsync(cancellationToken);
            IsReachable = true;
            LastError = null;
            Changed?.Invoke();
        }
        catch (ProviderAdminException ex)
        {
            _toasts?.ShowError(title: "Action failed", message: ex.Message);
            throw;
        }
    }
}