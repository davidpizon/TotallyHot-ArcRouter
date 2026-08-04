using TotallyHot.ArcRouter.Gui.Admin;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's provider/credential/model management. Wraps
/// <see cref="ProviderAdminClient"/> (the tested, platform-agnostic logic in TotallyHot.ArcRouter.Gui.Admin)
/// with the same "singleton + Changed event + best-effort, reachability-tolerant" shape as
/// <see cref="LiveDataStore"/>, so the UI survives tab switches and degrades gracefully when the proxy
/// isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class ProviderAdminStore
{
    /// <summary>
    /// The proxy's plain-HTTP management origin. The management API shares the LLM-forwarding port
    /// (5001), distinct from the TLS gRPC telemetry port (5002) <see cref="LiveDataStore"/> uses.
    /// </summary>
    public const string DefaultManagementAddress = "http://localhost:5001";

    private readonly ProviderAdminClient _client;
    private readonly ILogger<ProviderAdminStore>? _logger;

    /// <summary>Initializes a new instance of the <see cref="ProviderAdminStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="managementAddress">The proxy management origin; defaults to <see cref="DefaultManagementAddress"/>.</param>
    /// <param name="adminToken">
    /// Optional management token override; when null (the default), the token is read from the shared
    /// <c>%LOCALAPPDATA%\TotallyHot.ArcRouter\management-token.txt</c> file the proxy generates (see
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
    public ProviderAdminStore(
        ILogger<ProviderAdminStore>? logger = null,
        string managementAddress = DefaultManagementAddress,
        string? adminToken = null,
        HttpMessageHandler? transport = null)
    {
        _logger = logger;
        var normalized = managementAddress.EndsWith('/') ? managementAddress : managementAddress + "/";
        var httpClient = transport is null ? new HttpClient() : new HttpClient(transport);
        httpClient.BaseAddress = new Uri(normalized);
        _client = new ProviderAdminClient(httpClient, adminToken ?? ManagementTokenReader.TryRead());
    }

    /// <summary>The providers currently known, refreshed after each load or successful edit.</summary>
    public IReadOnlyList<ProviderAdminView> Providers { get; private set; } = [];

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load/edit reached the proxy management API.</summary>
    public bool IsReachable { get; private set; }

    /// <summary>The last load error message, if the management API was unreachable.</summary>
    public string? LastError { get; private set; }

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
            _logger?.LogWarning(ex, "Failed to load providers from the management API.");
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>Adds or edits a provider, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The edit was rejected; the caller (UI) surfaces the message.</exception>
    public Task UpsertProviderAsync(string key, ProviderWriteRequest body, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.UpsertProviderAsync(key, body, cancellationToken));

    /// <summary>Removes a provider along with every model routing to it, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The removal was rejected (e.g. the provider is unknown).</exception>
    public Task RemoveProviderAsync(string key, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.RemoveProviderAsync(key, cancellationToken));

    /// <summary>Adds or edits a model under a provider, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The edit was rejected.</exception>
    public Task UpsertModelAsync(string key, string modelName, ModelWriteRequest body, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.UpsertModelAsync(key, modelName, body, cancellationToken));

    /// <summary>Removes a model, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The removal was rejected.</exception>
    public Task RemoveModelAsync(string key, string modelName, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.RemoveModelAsync(key, modelName, cancellationToken));

    /// <summary>Sets or clears a provider's monthly budget caps, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The edit was rejected (e.g. a negative cap).</exception>
    public Task SetBudgetAsync(string key, ProviderBudgetWriteRequest body, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.SetBudgetAsync(key, body, cancellationToken));

    /// <summary>Switches a provider on or off, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The provider is unknown or the write failed.</exception>
    public Task SetEnabledAsync(string key, ProviderEnabledWriteRequest body, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.SetEnabledAsync(key, body, cancellationToken));

    /// <summary>Switches a model on or off, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The model is unknown or the write failed.</exception>
    public Task SetModelEnabledAsync(string key, string modelName, ModelEnabledWriteRequest body, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.SetModelEnabledAsync(key, modelName, body, cancellationToken));

    /// <summary>Pins (or clears) a model's tool-call dialect, then publishes the updated list.</summary>
    /// <exception cref="ProviderAdminException">The model is unknown, the dialect is unrecognized, or the write failed.</exception>
    public Task SetModelToolDialectAsync(string key, string modelName, ModelToolDialectWriteRequest body, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.SetModelToolDialectAsync(key, modelName, body, cancellationToken));

    /// <summary>
    /// Queries a provider's own model list (live discovery). An independently callable building block - the
    /// Governance UI's "Refresh from endpoint" action uses <see cref="RefreshFromEndpointAsync"/> instead.
    /// </summary>
    public Task<DiscoverModelsResult> DiscoverModelsAsync(string key, CancellationToken cancellationToken = default) =>
        _client.DiscoverModelsAsync(key, cancellationToken);

    /// <summary>
    /// Re-probes a provider's endpoint and runs tool-call dialect detection for its models. An independently
    /// callable building block - the Governance UI's "Refresh from endpoint" action uses
    /// <see cref="RefreshFromEndpointAsync"/> instead, which also reconciles the model list. Does not itself
    /// update <see cref="Providers"/> - the scan's own return value is only the endpoint-flavor result.
    /// </summary>
    /// <exception cref="ProviderAdminException">The provider is unknown, scanning is unavailable, or the request failed.</exception>
    public Task<ProviderEndpointCapabilitiesView> ScanCapabilitiesAsync(string key, CancellationToken cancellationToken = default) =>
        _client.ScanCapabilitiesAsync(key, cancellationToken);

    /// <summary>
    /// The Governance UI's "Refresh from endpoint" action, then publishes the updated list: discovers the
    /// provider's live model list, reconciles it into configuration (adding newly-seen ids as stopped,
    /// flagging previously-configured ones no longer reported - never deleting), then re-scans endpoint
    /// flavors and re-runs dialect detection. All of this happens on the router in one request; this method
    /// just triggers it and publishes the fresh result, the same as every other mutation here.
    /// </summary>
    /// <exception cref="ProviderAdminException">The provider is unknown or the request failed.</exception>
    public Task RefreshFromEndpointAsync(string key, CancellationToken cancellationToken = default) =>
        MutateAsync(() => _client.RefreshFromEndpointAsync(key, cancellationToken));

    /// <summary>
    /// Shared implementation behind the provider mutation methods: runs the given write operation and
    /// publishes its returned provider list, letting a <see cref="ProviderAdminException"/> propagate
    /// untouched so the caller can surface it inline.
    /// </summary>
    private async Task MutateAsync(Func<Task<IReadOnlyList<ProviderAdminView>>> mutation)
    {
        // Success publishes the server's returned list; a ProviderAdminException (e.g. a validation 400)
        // propagates so the UI can show the message inline, leaving the current list untouched.
        Providers = await mutation();
        IsReachable = true;
        IsLoaded = true;
        LastError = null;
        Changed?.Invoke();
    }
}

