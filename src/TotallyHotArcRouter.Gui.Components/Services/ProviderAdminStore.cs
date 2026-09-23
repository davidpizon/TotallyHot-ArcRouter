using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's provider/credential/model management. Wraps
/// <see cref="ProviderAdminClient"/> in the shared <see cref="AdminStoreBase{TClient}"/> shape so the UI
/// survives tab switches and degrades gracefully when the proxy isn't running. Toast notifications are a
/// one-line wrap around load/mutation failure, not a reason to stay off the seam. Registered in
/// the WASM host's composition root.
/// </summary>
public sealed class ProviderAdminStore : AdminStoreBase<ProviderAdminClient>
{
    private readonly ConcurrentDictionary<string, RateLimitHistoryResponseAdminView> _rateLimitHistory = new();
    private readonly ToastService? _toasts;

    /// <summary>Initializes a new instance of the <see cref="ProviderAdminStore"/> class.</summary>
    /// <param name="channelProvider">
    /// Supplies the shared call invoker this store's client is constructed over. Required unless
    /// <paramref name="client"/> is supplied, in which case it is never consulted.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="client">
    /// A pre-built client to use instead of constructing one from <paramref name="channelProvider"/>;
    /// <see langword="null"/> (the default, and always the case in production) builds one. This exists so
    /// tests can render the Governance tab against a canned provider list: without it the only reachable
    /// state in a test process is "unavailable", which leaves the entire loaded UI unexercised.
    /// </param>
    /// <param name="toasts">
    /// App-wide error-toast notifications; <see langword="null"/> (a test's default) simply skips raising
    /// toasts. See <see cref="ToastService"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="channelProvider"/> and <paramref name="client"/> are both null.</exception>
    public ProviderAdminStore(
        IRouterChannelProvider? channelProvider = null,
        ILogger<ProviderAdminStore>? logger = null,
        ProviderAdminClient? client = null,
        ToastService? toasts = null)
        : base(client: ResolveClient(channelProvider, client), logger: logger)
    {
        _toasts = toasts;
        ServerAddress = client is null ? channelProvider?.ServerAddress : null;
    }

    /// <summary>
    /// Resolves the client this store drives: a caller-supplied instance, or one constructed over
    /// <paramref name="channelProvider"/>'s shared invoker.
    /// </summary>
    private static ProviderAdminClient ResolveClient(IRouterChannelProvider? channelProvider,
        ProviderAdminClient? client)
    {
        if (client is not null) return client;

        ArgumentNullException.ThrowIfNull(channelProvider);
        return new ProviderAdminClient(channelProvider.CallInvoker);
    }

    /// <summary>
    /// The proxy endpoint this store's client talks to, so the unreachable state can name the address it
    /// actually failed to reach rather than assuming the default. <see langword="null"/> when constructed
    /// over a caller-supplied client, whose endpoint this store has no way to know.
    /// </summary>
    public string? ServerAddress { get; }

    /// <summary>The providers currently known, refreshed after each load or successful edit.</summary>
    public IReadOnlyList<ProviderAdminView> Providers { get; private set; } = [];

    /// <summary>
    /// Add-provider templates from <c>ModelRouting:Providers</c>, loaded alongside <see cref="Providers"/>.
    /// Empty when the template request fails; the dialog still offers <c>Other</c>. A failure is
    /// distinguished from a genuinely empty catalog by <see cref="TemplatesUnavailable"/>.
    /// </summary>
    public IReadOnlyList<ProviderTemplates.ProviderEditorTemplate> Templates { get; private set; } = [];

    /// <summary>
    /// Whether the last provider load could not read the template catalog. The dialog uses this to keep
    /// a stored provider type across a save: an empty <see cref="Templates"/> list alone would resolve
    /// every existing key to <c>Other</c> and persist that downgrade.
    /// </summary>
    public bool TemplatesUnavailable { get; private set; }

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

    /// <summary>
    /// Loads the provider list. Connection failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the tab renders an "unreachable" state instead of crashing when the proxy
    /// isn't running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Logger?.LogDebug("Refreshing provider agent list.");
        return LoadGuardedAsync(
            async ct =>
            {
                Providers = await Client.GetProvidersAsync(ct);
                try
                {
                    Templates = await Client.GetProviderTemplatesAsync(ct);
                    TemplatesUnavailable = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Templates = [];
                    TemplatesUnavailable = true;
                    Logger?.LogWarning(exception: ex, message: "Failed to load add-provider templates.");
                }
            },
            "load the providers",
            cancellationToken,
            onFailure: () => _toasts?.ShowError(title: "Providers unreachable",
                message: LastError ?? "Unknown error."));
    }

    /// <summary>Adds or edits a provider, then publishes the updated list.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="body">The provider fields to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The edit was rejected; the caller (UI) surfaces the message.</exception>
    public Task UpsertProviderAsync(string key, ProviderWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        Logger?.LogDebug("Updating provider {ProviderKey} with URL {ProviderUrl}.", key, body.BaseUrl);
        return MutateAsync(() =>
            Client.UpsertProviderAsync(key: key, body: body, cancellationToken: cancellationToken));
    }

    /// <summary>Removes a provider along with every model routing to it, then publishes the updated list.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The removal was rejected (e.g. the provider is unknown).</exception>
    public Task RemoveProviderAsync(string key, CancellationToken cancellationToken = default)
    {
        return MutateAsync(() => Client.RemoveProviderAsync(key: key, cancellationToken: cancellationToken));
    }

    /// <summary>Adds or edits a model under a provider, then publishes the updated list.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="body">The model fields to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The edit was rejected.</exception>
    public Task UpsertModelAsync(string key, string modelName, ModelWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            Client.UpsertModelAsync(key: key, modelName: modelName, body: body,
                cancellationToken: cancellationToken));
    }

    /// <summary>Removes a model, then publishes the updated list.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The removal was rejected.</exception>
    public Task RemoveModelAsync(string key, string modelName, CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            Client.RemoveModelAsync(key: key, modelName: modelName, cancellationToken: cancellationToken));
    }

    /// <summary>Sets or clears a provider's monthly budget caps, then publishes the updated list.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="body">The caps to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The edit was rejected (e.g. a negative cap).</exception>
    public Task SetBudgetAsync(string key, ProviderBudgetWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() => Client.SetBudgetAsync(key: key, body: body, cancellationToken: cancellationToken));
    }

    /// <summary>Switches a provider on or off, then publishes the updated list.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="body">The new on/off state.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The provider is unknown or the write failed.</exception>
    public Task SetEnabledAsync(string key, ProviderEnabledWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() => Client.SetEnabledAsync(key: key, body: body, cancellationToken: cancellationToken));
    }

    /// <summary>Switches a model on or off, then publishes the updated list.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="body">The new on/off state.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The model is unknown or the write failed.</exception>
    public Task SetModelEnabledAsync(string key, string modelName, ModelEnabledWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            Client.SetModelEnabledAsync(key: key, modelName: modelName, body: body,
                cancellationToken: cancellationToken));
    }

    /// <summary>Pins (or clears) a model's tool-call dialect, then publishes the updated list.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="body">The dialect to pin, or a null/empty dialect to clear the pin.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The model is unknown, the dialect is unrecognized, or the write failed.</exception>
    public Task SetModelToolDialectAsync(string key, string modelName, ModelToolDialectWriteRequest body,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(() =>
            Client.SetModelToolDialectAsync(key: key, modelName: modelName, body: body,
                cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Stores a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md §7), then
    /// reloads the provider list so <see cref="ProviderAdminView.HasStoredAdminKey"/> reflects it. Only
    /// <c>openai</c> and <c>anthropic</c> are recognized.
    /// </summary>
    /// <param name="provider">The reconciliation provider key.</param>
    /// <param name="value">The Admin API key to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The provider is unrecognized, the store is unavailable, or the write failed.</exception>
    public async Task SetAdminApiKeyAsync(string provider, string value, CancellationToken cancellationToken = default)
    {
        await Client.SetAdminApiKeyAsync(provider: provider, value: value, cancellationToken: cancellationToken);
        await LoadAsync(cancellationToken);
    }

    /// <summary>Clears a provider's stored reconciliation Admin API key, then reloads the provider list.</summary>
    /// <param name="provider">The reconciliation provider key.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The provider is unrecognized, the store is unavailable, or the write failed.</exception>
    public async Task DeleteAdminApiKeyAsync(string provider, CancellationToken cancellationToken = default)
    {
        await Client.DeleteAdminApiKeyAsync(provider: provider, cancellationToken: cancellationToken);
        await LoadAsync(cancellationToken);
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
    /// thrown <see cref="GrpcAdminException"/> - the request itself still succeeds, since the router
    /// noticed the discovery/scan failed and simply left the model list untouched (see
    /// <c>ManagementFacade.RefreshFromEndpointAsync</c>). The failure instead travels back inside the
    /// refreshed provider's own <see cref="ProviderAdminView.AdminAction"/>, which this method checks
    /// after the mutation to raise the toast the caller would otherwise never see.
    /// </remarks>
    /// <param name="key">The provider key to refresh.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="GrpcAdminException">The provider is unknown or the request failed outright.</exception>
    public async Task RefreshFromEndpointAsync(string key, CancellationToken cancellationToken = default)
    {
        await MutateAsync(() => Client.RefreshFromEndpointAsync(key: key, cancellationToken: cancellationToken));

        var provider = Providers.FirstOrDefault(p =>
            string.Equals(a: p.Key, b: key, comparisonType: StringComparison.OrdinalIgnoreCase));
        if (provider?.AdminAction is { Ok: false } failure)
            _toasts?.ShowError(title: $"{provider.Name ?? provider.Key}: {failure.Operation} failed",
                message: failure.Message ?? "Unknown error.");
    }

    /// <summary>
    /// Shared implementation behind the provider mutation methods: runs the given write operation and
    /// publishes its returned provider list, letting a <see cref="GrpcAdminException"/> propagate
    /// untouched (after raising a toast) so the caller can also surface it inline.
    /// </summary>
    private async Task MutateAsync(Func<Task<IReadOnlyList<ProviderAdminView>>> mutation)
    {
        try
        {
            Providers = await mutation();
            RecordSuccess();
            NotifyChanged();
        }
        catch (GrpcAdminException ex)
        {
            _toasts?.ShowError(title: "Action failed", message: ex.Message);
            RecordFailure(exception: ex, description: "a provider operation");
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
    /// <param name="key">The provider key.</param>
    /// <param name="hours">How far back to look, in hours (default 6).</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    public async Task LoadRateLimitHistoryAsync(string key, double hours = 6.0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _rateLimitHistory[key] =
                await Client.GetRateLimitHistoryAsync(key: key, hours: hours, cancellationToken: cancellationToken);
            NotifyChanged();
        }
        catch (GrpcAdminException ex)
        {
            Logger?.LogDebug(exception: ex, message: "Failed to load rate-limit history for provider {Provider}.",
                key);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Called fire-and-forget from ProvidersAdmin.razor, so a timeout that is not the caller's
            // cancellation must not become an unobserved task exception.
            Logger?.LogDebug(exception: ex, message: "Timed out loading rate-limit history for provider {Provider}.",
                key);
        }
    }

    /// <summary>
    /// Loads the price-override list. Same reachability-tolerant shape as <see cref="LoadAsync"/>, kept
    /// separate since the price-overrides pane is a distinct Governance sub-view that shouldn't force a
    /// provider reload (or vice versa). Does not mark the store loaded: that flag is the provider list's.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadPriceOverridesAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct =>
            {
                PriceOverrides = await Client.GetPriceOverridesAsync(ct);
                PriceResolutionDiagnosis = await Client.GetPriceResolutionDiagnosisAsync(ct);
            },
            "load the price overrides",
            cancellationToken,
            onFailure: () => _toasts?.ShowError(title: "Price overrides unreachable",
                message: LastError ?? "Unknown error."),
            marksLoaded: false);
    }

    /// <summary>Adds or replaces a price override, then publishes the updated override list.</summary>
    /// <param name="body">The override to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">The edit was rejected (e.g. an unconfigured model) or the request failed.</exception>
    public Task SetPriceOverrideAsync(PriceOverrideWriteRequest body, CancellationToken cancellationToken = default)
    {
        return MutatePriceOverridesAsync(
            mutation: () => Client.SetPriceOverrideAsync(body: body, cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);
    }

    /// <summary>Removes a price override, then publishes the updated override list.</summary>
    /// <param name="sourceName">The aggregator source the override applies to.</param>
    /// <param name="aggregatorModelKey">The source's own model key the override matches.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="GrpcAdminException">No override matched, or the request failed.</exception>
    public Task RemovePriceOverrideAsync(string sourceName, string aggregatorModelKey,
        CancellationToken cancellationToken = default)
    {
        return MutatePriceOverridesAsync(
            mutation: () => Client.RemovePriceOverrideAsync(sourceName: sourceName,
                aggregatorModelKey: aggregatorModelKey,
                cancellationToken: cancellationToken), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Shared implementation behind the price-override mutation methods: runs the write, refreshes
    /// diagnosis, and publishes. An override can change whether a model resolves (or whether the resolved
    /// price is approximate), so the diagnosis view has to be refreshed alongside the override list itself.
    /// </summary>
    private async Task MutatePriceOverridesAsync(Func<Task<IReadOnlyList<PriceOverrideView>>> mutation,
        CancellationToken cancellationToken)
    {
        try
        {
            PriceOverrides = await mutation();
            PriceResolutionDiagnosis = await Client.GetPriceResolutionDiagnosisAsync(cancellationToken);
            RecordSuccess(marksLoaded: false);
            NotifyChanged();
        }
        catch (GrpcAdminException ex)
        {
            _toasts?.ShowError(title: "Action failed", message: ex.Message);
            RecordFailure(exception: ex, description: "a price-override operation");
            throw;
        }
    }
}
