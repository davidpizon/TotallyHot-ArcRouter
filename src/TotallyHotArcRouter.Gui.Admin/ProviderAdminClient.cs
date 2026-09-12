using System.Globalization;
using Grpc.Core;
using Grpc.Net.Client;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// A thin, platform-agnostic gRPC client for the proxy's <see cref="Contract.ProviderAdminService"/>
/// (docs/router/tracked-todos.md #7 - replaces the earlier plain-HTTP/JSON <c>/admin/*</c> client). Lives
/// in this plain <c>net10.0</c> library (not the Windows-only MAUI Gui project) so its logic is
/// unit-tested in CI; the MAUI <c>ProviderAdminStore</c> wraps an instance of it. Every public method's
/// signature is unchanged from the HTTP-era client - <c>ProviderAdminStore</c> needed no changes for this
/// migration - only the transport underneath moved from JSON-over-HTTP to Protobuf-over-gRPC.
/// </summary>
public sealed class ProviderAdminClient
{
    private readonly string? _adminToken;
    private readonly Contract.ProviderAdminService.ProviderAdminServiceClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderAdminClient"/> class.
    /// </summary>
    /// <param name="channel">
    /// The gRPC channel to send requests over. Must target the proxy's TLS gRPC endpoint (e.g.
    /// <c>https://localhost:5002</c>) - the same channel the telemetry client uses.
    /// </param>
    /// <param name="adminToken">
    /// Optional management token; when set, it is sent in the <c>x-admin-token</c> gRPC metadata entry on
    /// every call (required only when the proxy has <c>Management:Token</c> configured).
    /// </param>
    public ProviderAdminClient(GrpcChannel channel, string? adminToken = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _client = new Contract.ProviderAdminService.ProviderAdminServiceClient(channel);
        _adminToken = adminToken;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server - the generated
    /// client exposes a protected parameterless constructor precisely for this, mirroring
    /// <c>TotallyHot.ArcRouter.Gui.Telemetry.PriceSourceAdminClient</c>'s identical test seam. The caller owns
    /// any channel backing <paramref name="client"/>.
    /// </summary>
    /// <param name="client">The generated client (or test double) to send requests through.</param>
    /// <param name="adminToken">Optional management token; see the primary constructor's remarks.</param>
    public ProviderAdminClient(Contract.ProviderAdminService.ProviderAdminServiceClient client,
        string? adminToken = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _adminToken = adminToken;
    }

    /// <summary>Lists all configured providers and their models.</summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The configured providers.</returns>
    /// <exception cref="ProviderAdminException">The request failed or the proxy returned an error.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync((client, options) =>
            client.ListProvidersAsync(new Contract.ListProvidersRequest(), options), cancellationToken)
            .ConfigureAwait(false);
        return ToViews(response);
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
        var request = new Contract.UpsertProviderRequest { Key = key, ReplaceHeaders = body.Headers is not null };
        if (body.BaseUrl is not null) request.BaseUrl = body.BaseUrl;
        if (body.AuthHeaderName is not null) request.AuthHeaderName = body.AuthHeaderName;
        if (body.IsFree.HasValue) request.IsFree = body.IsFree.Value;
        if (body.Enabled.HasValue) request.Enabled = body.Enabled.Value;
        if (body.ProviderName is not null) request.ProviderName = body.ProviderName;
        if (body.ProviderType is not null) request.ProviderType = body.ProviderType;
        if (body.Headers is not null)
            request.Headers.AddRange(body.Headers.Select(h =>
            {
                var wire = new Contract.HeaderWrite { Name = h.Name ?? string.Empty };
                if (h.Value is not null) wire.Value = h.Value;
                if (h.ValueEnvVar is not null) wire.ValueEnvVar = h.ValueEnvVar;
                if (h.Locked.HasValue) wire.Locked = h.Locked.Value;
                return wire;
            }));

        var response = await CallAsync((client, options) => client.UpsertProviderAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
    }

    /// <summary>Removes a provider by key.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The updated provider list.</returns>
    /// <exception cref="ProviderAdminException">The removal was rejected (e.g. the provider is unknown) or the request failed.</exception>
    public async Task<IReadOnlyList<ProviderAdminView>> RemoveProviderAsync(string key,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync((client, options) =>
            client.RemoveProviderAsync(new Contract.RemoveProviderRequest { Key = key }, options), cancellationToken)
            .ConfigureAwait(false);
        return ToViews(response);
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
        var model = new Contract.ModelWrite();
        if (body.ProviderModelId is not null) model.ProviderModelId = body.ProviderModelId;

        var request = new Contract.UpsertModelRequest { ProviderKey = key, ModelName = modelName, Model = model };
        var response = await CallAsync((client, options) => client.UpsertModelAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var response = await CallAsync((client, options) =>
            client.RemoveModelAsync(new Contract.RemoveModelRequest { ModelName = modelName }, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var request = new Contract.SetModelEnabledRequest { ModelName = modelName, Enabled = body.Enabled };
        var response = await CallAsync((client, options) => client.SetModelEnabledAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var request = new Contract.SetModelToolDialectRequest
        {
            ProviderKey = key, ModelName = modelName, Dialect = body.Dialect ?? string.Empty
        };
        var response = await CallAsync((client, options) => client.SetModelToolDialectAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var budget = new Contract.BudgetWrite();
        if (body.DollarCap.HasValue) budget.DollarCap = body.DollarCap.Value.ToString(CultureInfo.InvariantCulture);
        if (body.TokenCap.HasValue) budget.TokenCap = body.TokenCap.Value;
        if (body.WindowKind is not null) budget.WindowKind = body.WindowKind;
        if (body.WindowHours.HasValue) budget.WindowHours = body.WindowHours.Value;

        var request = new Contract.SetProviderBudgetRequest { ProviderKey = key, Budget = budget };
        var response = await CallAsync((client, options) => client.SetProviderBudgetAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var request = new Contract.SetProviderEnabledRequest { Key = key, Enabled = body.Enabled };
        var response = await CallAsync((client, options) => client.SetProviderEnabledAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var request = new Contract.DiscoverModelsRequest { ProviderKey = key };
        var response = await CallAsync((client, options) => client.DiscoverModelsAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return new DiscoverModelsResult(Supported: response.Supported, Models: response.Models.ToList(),
            Error: response.HasError ? response.Error : null);
    }

    /// <summary>
    /// Probes a provider's endpoint for which API flavors it answers, and - riding on whatever metadata
    /// that exposes - runs tier 1-3 tool-call dialect detection for every model routed to it
    /// (<c>docs/router/tool-call-normalization.md</c> §3.2-3.3). An independently callable building block;
    /// the Governance UI's "Refresh from endpoint" action calls <see cref="RefreshFromEndpointAsync"/>
    /// instead, which also reconciles the model list.
    /// </summary>
    /// <param name="key">The provider key to scan.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>Which API flavors the endpoint answered, and when the scan ran.</returns>
    /// <exception cref="ProviderAdminException">The provider is unknown, scanning is unavailable, or the request failed.</exception>
    public async Task<ProviderEndpointCapabilitiesView> ScanCapabilitiesAsync(string key,
        CancellationToken cancellationToken = default)
    {
        var request = new Contract.ScanCapabilitiesRequest { ProviderKey = key };
        var response = await CallAsync((client, options) => client.ScanCapabilitiesAsync(request, options),
            cancellationToken).ConfigureAwait(false);

        // The RPC returns the full refreshed list (the "full state after the mutation" convention every
        // other mutation on this service follows); this method's own contract - unchanged from the REST-era
        // client - is the single scanned provider's capabilities, so narrow it back down here.
        var provider = response.Providers.SingleOrDefault(p => string.Equals(a: p.Key, b: key,
            comparisonType: StringComparison.OrdinalIgnoreCase));
        if (provider?.EndpointCapabilities is not { } capabilities)
            throw new ProviderAdminException($"The proxy did not report endpoint capabilities for '{key}'.");

        return ToView(capabilities, key);
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
        var request = new Contract.RefreshFromEndpointRequest { ProviderKey = key };
        var response = await CallAsync((client, options) => client.RefreshFromEndpointAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var response = await CallAsync((client, options) =>
            client.ListPriceOverridesAsync(new Contract.ListPriceOverridesRequest(), options), cancellationToken)
            .ConfigureAwait(false);
        return ToViews(response);
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
        var request = new Contract.SetPriceOverrideRequest
        {
            Override = new Contract.PriceOverride
            {
                SourceName = body.SourceName, AggregatorModelKey = body.AggregatorModelKey, ModelName = body.ModelName
            }
        };
        var response = await CallAsync((client, options) => client.SetPriceOverrideAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var request = new Contract.RemovePriceOverrideRequest
        {
            SourceName = sourceName, AggregatorModelKey = aggregatorModelKey
        };
        var response = await CallAsync((client, options) => client.RemovePriceOverrideAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return ToViews(response);
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
        var response = await CallAsync((client, options) =>
            client.GetPriceResolutionAsync(new Contract.GetPriceResolutionRequest(), options), cancellationToken)
            .ConfigureAwait(false);
        return response.Entries.Select(e => new PriceResolutionDiagnosisView(ModelName: e.ModelName,
            Provider: e.Provider, Resolved: e.Resolved, IsApproximate: e.IsApproximate)).ToList();
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
        var request = new Contract.GetRateLimitHistoryRequest { ProviderKey = key, Hours = hours };
        var response = await CallAsync((client, options) => client.GetRateLimitHistoryAsync(request, options),
            cancellationToken).ConfigureAwait(false);

        var dimensions = response.Dimensions.ToDictionary(
            keySelector: kvp => kvp.Key,
            elementSelector: kvp => (IReadOnlyList<RateLimitHistoryPointAdminView>)kvp.Value.Points.Select(p =>
                new RateLimitHistoryPointAdminView(
                    BucketUtc: p.BucketUtc.ToDateTimeOffset(),
                    Remaining: p.HasRemaining ? p.Remaining : null,
                    Limit: p.HasLimit ? p.Limit : null)).ToList());
        return new RateLimitHistoryResponseAdminView(dimensions);
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
        var request = new Contract.SetSecretRequest { Name = AdminApiKeySecretName(provider), Value = value };
        await CallAsync((client, options) => client.SetSecretAsync(request, options), cancellationToken)
            .ConfigureAwait(false);
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
        var request = new Contract.DeleteSecretRequest { Name = AdminApiKeySecretName(provider) };
        await CallAsync((client, options) => client.DeleteSecretAsync(request, options), cancellationToken)
            .ConfigureAwait(false);
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
    /// Attaches the admin token (if configured) as gRPC call metadata, invokes <paramref name="call"/>, and
    /// translates an <see cref="RpcException"/> into a <see cref="ProviderAdminException"/> carrying the
    /// same human-readable message the REST client used to surface.
    /// </summary>
    private async Task<TResponse> CallAsync<TResponse>(
        Func<Contract.ProviderAdminService.ProviderAdminServiceClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
    {
        var options = BuildCallOptions(cancellationToken);
        try
        {
            return await call(_client, options).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw ToProviderAdminException(ex);
        }
    }

    /// <summary>Builds the <see cref="CallOptions"/> shared by every call: the admin-token metadata entry and cancellation.</summary>
    private CallOptions BuildCallOptions(CancellationToken cancellationToken)
    {
        var metadata = new Metadata();
        if (!string.IsNullOrEmpty(_adminToken)) metadata.Add(key: "x-admin-token", value: _adminToken);
        return new CallOptions(headers: metadata, cancellationToken: cancellationToken);
    }

    /// <summary>Translates a gRPC failure into a <see cref="ProviderAdminException"/>, mirroring the REST client's error shape.</summary>
    private static ProviderAdminException ToProviderAdminException(RpcException ex)
    {
        // Unavailable is what Grpc.Net.Client reports for a transport-level failure (connection refused, DNS
        // failure, TLS handshake failure) - the gRPC equivalent of the REST-era client's HttpRequestException
        // catch, not Cancelled (a deadline/client-initiated cancellation, an unrelated condition).
        return ex.StatusCode == StatusCode.Unavailable
            ? new ProviderAdminException(message: $"Could not reach the proxy management API: {ex.Status.Detail}",
                innerException: ex)
            : new ProviderAdminException(ex.Status.Detail);
    }

    private static IReadOnlyList<ProviderAdminView> ToViews(Contract.ProviderListResponse response)
    {
        return response.Providers.Select(ToView).ToList();
    }

    private static ProviderAdminView ToView(Contract.ProviderState provider)
    {
        return new ProviderAdminView(
            Key: provider.Key,
            Name: provider.HasName ? provider.Name : null,
            BaseUrl: provider.BaseUrl,
            AuthHeaderName: provider.AuthHeaderName,
            Models: provider.Models.Select(ToView).ToList(),
            Headers: provider.Headers.Select(ToView).ToList(),
            IsFree: provider.IsFree,
            DollarCap: provider.HasDollarCap ? decimal.Parse(provider.DollarCap, CultureInfo.InvariantCulture) : null,
            TokenCap: provider.HasTokenCap ? provider.TokenCap : null,
            DollarSpent: decimal.Parse(provider.DollarSpent, CultureInfo.InvariantCulture),
            TokensUsed: provider.TokensUsed,
            Enabled: provider.Enabled,
            ProviderType: provider.HasProviderType ? provider.ProviderType : null,
            EndpointCapabilities: provider.EndpointCapabilities is { } capabilities ? ToView(capabilities, provider.Key) : null,
            UsageLastRecordedAtUtc: provider.UsageLastRecordedAtUtc?.ToDateTimeOffset(),
            RateLimit: provider.RateLimit is { } rateLimit ? ToView(rateLimit) : null,
            WindowKind: provider.WindowKind,
            NextResetUtc: provider.NextResetUtc?.ToDateTimeOffset(),
            HasStoredAdminKey: provider.HasStoredAdminKey,
            ReportedUsage: provider.ReportedUsage is { } reportedUsage ? ToView(reportedUsage) : null,
            AdminAction: provider.AdminAction is { } adminAction ? ToView(adminAction) : null,
            LiveTraffic: provider.LiveTraffic is { } liveTraffic ? ToView(liveTraffic) : null);
    }

    private static ModelAdminView ToView(Contract.ModelState model)
    {
        return new ModelAdminView(
            ModelName: model.ModelName,
            ProviderModelId: model.ProviderModelId,
            Dialect: model.HasDialect ? model.Dialect : null,
            Confidence: model.HasConfidence ? model.Confidence : null,
            Enabled: model.Enabled,
            PresentUpstream: model.PresentUpstream);
    }

    private static ProviderHeaderView ToView(Contract.HeaderState header)
    {
        return new ProviderHeaderView(
            Name: header.Name,
            Source: header.Source,
            ValueEnvVar: header.HasValueEnvVar ? header.ValueEnvVar : null,
            Value: header.HasValue ? header.Value : null,
            Locked: header.Locked);
    }

    private static ProviderEndpointCapabilitiesView ToView(Contract.EndpointCapabilitiesState capabilities, string providerKey)
    {
        return new ProviderEndpointCapabilitiesView(
            ProviderKey: providerKey,
            OpenAiCompatible: capabilities.OpenaiCompatible,
            LmStudioNative: capabilities.LmStudioNative,
            OllamaNative: capabilities.OllamaNative,
            AnthropicCompatible: capabilities.AnthropicCompatible,
            ScannedAtUtc: capabilities.ScannedAtUtc.ToDateTimeOffset(),
            ScanError: capabilities.HasScanError ? capabilities.ScanError : null);
    }

    private static ProviderReportedUsageAdminView ToView(Contract.ProviderReportedUsageState reportedUsage)
    {
        var rows = reportedUsage.Rows.Select(r => new ReportedUsageRowAdminView(
            UsageDay: DateOnly.ParseExact(s: r.UsageDay, format: "yyyy-MM-dd", provider: CultureInfo.InvariantCulture),
            Model: r.Model,
            InputTokens: r.InputTokens,
            OutputTokens: r.OutputTokens,
            CacheCreationTokens: r.CacheCreationTokens,
            CacheReadTokens: r.CacheReadTokens)).ToList();
        return new ProviderReportedUsageAdminView(rows, reportedUsage.FetchedAtUtc.ToDateTimeOffset());
    }

    private static ProviderInteractionStatusAdminView ToView(Contract.ProviderInteractionState status)
    {
        var kind = Enum.TryParse<ProviderInteractionKindAdminView>(value: status.Kind, result: out var parsed)
            ? parsed
            : ProviderInteractionKindAdminView.None;
        return new ProviderInteractionStatusAdminView(
            Ok: status.Ok,
            Operation: status.Operation,
            Message: status.HasMessage ? status.Message : null,
            AtUtc: status.AtUtc.ToDateTimeOffset(),
            Kind: kind);
    }

    private static ProviderRateLimitAdminView ToView(Contract.ProviderRateLimitState rateLimit)
    {
        var standardDimensions = new Dictionary<string, RateLimitDimensionAdminView>();
        var projections = new Dictionary<string, RateLimitExhaustionAdminView>();

        foreach (var (name, dimension) in rateLimit.Dimensions)
        {
            standardDimensions[name] = new RateLimitDimensionAdminView(
                Limit: dimension.HasLimit ? dimension.Limit : null,
                Remaining: dimension.HasRemaining ? dimension.Remaining : null,
                ResetAt: dimension.ResetAt?.ToDateTimeOffset());

            if (dimension is { HasTimeToExhaustionSeconds: true, HasBurnRatePerMinute: true })
                projections[name] = new RateLimitExhaustionAdminView(
                    TimeToExhaustion: TimeSpan.FromSeconds(dimension.TimeToExhaustionSeconds),
                    BurnRatePerMinute: dimension.BurnRatePerMinute);
        }

        var unifiedWindows = rateLimit.UnifiedWindows.ToDictionary(
            keySelector: kvp => kvp.Key,
            elementSelector: kvp => new RateLimitWindowAdminView(
                Status: kvp.Value.HasStatus ? kvp.Value.Status : null,
                Remaining: kvp.Value.HasRemaining ? kvp.Value.Remaining : null,
                ResetAt: kvp.Value.ResetAt?.ToDateTimeOffset()));

        // UnifiedStatus/UnifiedResetAt/RepresentativeClaim/RawHeaders are not read by any GUI surface
        // (confirmed at proto-design time) and are deliberately not carried over the wire - see
        // ProviderRateLimitState's remarks in telemetry.proto.
        var snapshot = new RateLimitSnapshotAdminView(
            StandardDimensions: standardDimensions,
            UnifiedStatus: null,
            UnifiedResetAt: null,
            UnifiedWindows: unifiedWindows,
            RepresentativeClaim: null,
            RawHeaders: new Dictionary<string, string>());

        return new ProviderRateLimitAdminView(
            Snapshot: snapshot,
            ObservedAtUtc: rateLimit.ObservedAtUtc.ToDateTimeOffset(),
            IsStale: rateLimit.IsStale,
            Projections: projections);
    }

    private static IReadOnlyList<PriceOverrideView> ToViews(Contract.PriceOverrideListResponse response)
    {
        return response.Overrides.Select(o => new PriceOverrideView(
            SourceName: o.SourceName, AggregatorModelKey: o.AggregatorModelKey, ModelName: o.ModelName)).ToList();
    }
}
