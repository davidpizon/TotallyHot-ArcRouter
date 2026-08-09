using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The single security boundary and source of truth for provider/model/budget management, shared by the
/// REST <c>/admin/*</c> API (<see cref="ProviderAdminEndpoints"/>) and the MCP provider tools
/// (<c>TotallyHot.ArcRouter.Mcp.Tools.ProviderMcpTools</c>). Every read this facade returns is a masked
/// projection: a literal API key or a literal custom-header value is never present in anything it hands
/// back, on either surface - <see cref="HeaderView.Source"/>/<see cref="HeaderView.ValueEnvVar"/> are the
/// only credential-shaped information exposed. Every write accepts the same "blank preserves what's
/// already stored" rule for each custom header's value, since a caller can never have received the literal
/// value to resend it in the first place.
/// </summary>
public sealed class ManagementFacade
{
    // How long a post-save capability scan may take before it is abandoned. The scan is a convenience -
    // the operator can always re-run it explicitly - so it must never make saving a provider feel hung.
    // Bounded-and-awaited rather than fire-and-forget: a detached task in ASP.NET has no request scope and
    // its failures go unobserved, and awaiting keeps the behavior deterministic enough to test.
    private static readonly TimeSpan CapabilityScanBudget = TimeSpan.FromSeconds(3);

    // The explicit scan's own, more generous budget. It still needs one: the probes are sequential and the
    // injected HttpClient uses the default 100s timeout, so an upstream that accepts connections but never
    // responds would otherwise hang this admin endpoint for roughly three times that. More generous than
    // the post-save budget because here the operator asked for the scan and is waiting for a real answer,
    // so a slow-but-alive local server should get the chance to finish.
    private static readonly TimeSpan ExplicitCapabilityScanBudget = TimeSpan.FromSeconds(15);

    // Dialect resolution's own budget, applied across every model on a provider rather than per model, so a
    // provider serving thirty models cannot turn one scan into thirty sequential timeouts. Deliberately
    // under ExplicitCapabilityScanBudget: the endpoint flavors are what the operator actually asked for,
    // and dialect detection is the free extra that rides along on the metadata those flavors expose.
    private static readonly TimeSpan DialectResolutionBudget = TimeSpan.FromSeconds(10);

    private readonly IProviderConfigStore _store;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly HttpClient _httpClient;
    private readonly ProviderBudgetStore? _budgetStore;
    private readonly ProviderEndpointScanner? _endpointScanner;
    private readonly ToolCallCapabilityStore? _capabilityStore;
    private readonly PriceCatalogRepository? _priceCatalogRepository;
    private readonly ModelAliasOverrideStore? _overrideStore;
    private readonly IUsageRollupStore? _rollupStore;

    // Constructed here rather than injected, unlike the endpoint scanner beside it. The resolver's only
    // dependencies are the HTTP client and environment accessor this facade already holds, so injecting it
    // would mean threading a seventh optional parameter through ProxyHostedService and ProxyServer to hand
    // it two things already present at the destination. Its own unit tests construct it directly, and the
    // tests that exercise it through this facade drive it the same way they drive model discovery and the
    // endpoint scan: by stubbing the HttpMessageHandler behind the injected client.
    private readonly ModelDialectResolver _dialectResolver;

    // Config default (§5.9); overridable via the constructor's rateLimitStalenessThreshold parameter.
    private static readonly TimeSpan DefaultRateLimitStalenessThreshold = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _rateLimitStalenessThreshold;

    // How far back BuildExhaustionProjections looks for an "earlier" observation to pair with the current
    // snapshot. Short deliberately: a burn rate measured over the last half hour reflects current traffic,
    // not a stale average diluted by a quiet period earlier in the retention window.
    private static readonly TimeSpan ProjectionLookback = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementFacade"/> class.
    /// </summary>
    /// <param name="store">The writable provider/model configuration store.</param>
    /// <param name="environment">Accessor used to resolve provider credentials for model discovery.</param>
    /// <param name="httpClient">HTTP client used to query a provider's live model list.</param>
    /// <param name="budgetStore">
    /// Optional per-provider budget store. When null, providers report no caps/spend and budget edits
    /// answer <see cref="ManagementErrorType.Unavailable"/>.
    /// </param>
    /// <param name="endpointScanner">
    /// Optional endpoint-flavor scanner. When null, capability scanning is unavailable and
    /// <see cref="ScanCapabilitiesAsync"/> answers <see cref="ManagementErrorType.Unavailable"/>.
    /// </param>
    /// <param name="capabilityStore">
    /// Optional store the scan results are persisted to. Null has the same effect as a null scanner - both
    /// are needed for a scan to be meaningful.
    /// </param>
    /// <param name="priceCatalogRepository">
    /// Optional price-catalog repository, read for each Anthropic provider's captured
    /// <c>anthropic-ratelimit-*</c> header snapshot (<c>docs/router/anthropic-reported-usage-plan.md</c> §5).
    /// When null, every provider's <see cref="ProviderView.RateLimit"/> is <see langword="null"/> - "no
    /// rate-limit data observed yet", identical to a repository with no captured headers for that provider.
    /// </param>
    /// <param name="overrideStore">
    /// Optional operator price-override store backing <c>PUT/DELETE /admin/price-overrides</c> (§5.7). When
    /// null, override reads/writes answer <see cref="ManagementErrorType.Unavailable"/>.
    /// </param>
    /// <param name="rollupStore">
    /// Optional Phase 4 rollup store backing <c>GET /admin/usage/summary</c> and <c>GET /admin/usage/rollup</c>
    /// (§5.15). When null, both answer <see cref="ManagementErrorType.Unavailable"/>.
    /// </param>
    /// <param name="rateLimitStalenessThreshold">
    /// How old a captured rate-limit snapshot may be before <see cref="ProviderRateLimitView.IsStale"/> is
    /// set (§5.9). Defaults to 15 minutes.
    /// </param>
    public ManagementFacade(
        IProviderConfigStore store,
        IEnvironmentVariableProvider environment,
        HttpClient httpClient,
        ProviderBudgetStore? budgetStore = null,
        ProviderEndpointScanner? endpointScanner = null,
        ToolCallCapabilityStore? capabilityStore = null,
        PriceCatalogRepository? priceCatalogRepository = null,
        ModelAliasOverrideStore? overrideStore = null,
        IUsageRollupStore? rollupStore = null,
        TimeSpan? rateLimitStalenessThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(httpClient);

        _store = store;
        _environment = environment;
        _httpClient = httpClient;
        _budgetStore = budgetStore;
        _endpointScanner = endpointScanner;
        _capabilityStore = capabilityStore;
        _priceCatalogRepository = priceCatalogRepository;
        _overrideStore = overrideStore;
        _rollupStore = rollupStore;
        _rateLimitStalenessThreshold = rateLimitStalenessThreshold ?? DefaultRateLimitStalenessThreshold;
        _dialectResolver = new ModelDialectResolver(httpClient, environment);
    }

    /// <summary>Lists every configured provider, masked, with its models and (if a budget store is present) its budget.</summary>
    public ProvidersResponse ListProviders() => BuildProvidersResponse();

    /// <summary>Adds or replaces a provider by key, merging over any existing provider (see <see cref="MergeProvider"/>).</summary>
    public async Task<ManagementResult<ProvidersResponse>> UpsertProviderAsync(
        string key, ProviderWriteRequest request, CancellationToken cancellationToken = default)
    {
        var current = _store.Snapshot.Options;
        current.Providers.TryGetValue(key, out var existing);
        var provider = MergeProvider(request, existing);
        var result = await MutateAsync(() => _store.UpsertProviderAsync(key, provider, cancellationToken)).ConfigureAwait(false);

        // Refresh the endpoint-capability record for the provider that was just saved, so tier-1 dialect
        // detection has something to read without the operator having to trigger a scan by hand. Strictly
        // best-effort: it runs only after the save has already succeeded, is bounded by
        // CapabilityScanBudget, and swallows everything - a provider that is merely unreachable right now
        // must still be configurable.
        if (result.Success)
        {
            await TryScanCapabilitiesAsync(key, provider, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Re-probes a provider's endpoint flavors on demand and persists the result
    /// (<c>POST /admin/providers/{key}/scan-capabilities</c>).
    /// </summary>
    /// <remarks>
    /// The explicit counterpart to the automatic scan on save: useful after starting a local server that was
    /// down when the provider was added, or after upgrading one that has since gained a native API. Unlike
    /// the save-time scan, a failure here is reported to the caller rather than swallowed - the operator
    /// asked for the scan, so they should see why it did not work.
    /// </remarks>
    /// <param name="key">The provider key to scan.</param>
    /// <param name="cancellationToken">Cancels the probes.</param>
    public async Task<ManagementResult<ProviderEndpointCapabilities>> ScanCapabilitiesAsync(
        string key, CancellationToken cancellationToken = default)
    {
        if (_endpointScanner is null || _capabilityStore is null)
        {
            return ManagementResult<ProviderEndpointCapabilities>.Fail(
                ManagementErrorType.Unavailable, "Endpoint capability scanning is not available.");
        }

        if (!_store.Snapshot.Options.Providers.TryGetValue(key, out var provider))
        {
            return ManagementResult<ProviderEndpointCapabilities>.Fail(
                ManagementErrorType.NotFound, $"Provider '{key}' not found.");
        }

        var capabilities = await ScanAndPersistCapabilitiesAsync(
            key, provider, ExplicitCapabilityScanBudget, cancellationToken).ConfigureAwait(false);

        // Tier 1-3 dialect detection for every model on this provider, now that the flags saying which
        // native metadata APIs are reachable have just been refreshed. Best-effort and non-blocking on the
        // result: the operator asked which flavors the endpoint answers, and that question has been
        // answered above regardless of what detection manages to learn.
        await TryResolveDialectsAsync(key, provider, capabilities, ModelsFor(key), cancellationToken).ConfigureAwait(false);

        return ManagementResult<ProviderEndpointCapabilities>.Ok(capabilities);
    }

    /// <summary>
    /// Pins how one model expresses tool calls, at <see cref="DetectionConfidence.Operator"/>, so no
    /// automatic scan or live observation can overwrite it
    /// (<c>PUT /admin/providers/{key}/models/{modelName}/tool-dialect</c>).
    /// </summary>
    /// <remarks>
    /// The equivalent of LiteLLM's <c>register_model(..., supports_function_calling=…)</c>: a way for a
    /// human to state what a model does when detection gets it wrong. Until this existed the only route was
    /// editing SQLite by hand.
    /// <para>
    /// <b>The case that motivated it is not hypothetical.</b> An intermittently-native model - one that
    /// emits a real <c>tool_calls</c> field on some replies and free text on others - gets recorded as
    /// <c>openai-native</c> at <see cref="DetectionConfidence.Observed"/> the first time it happens to
    /// succeed. Rule 2 then stops arming it, so no later reply is ever inspected, so no contrary evidence is
    /// ever recorded: the misclassification is self-sealing and every subsequent free-text reply reaches the
    /// client as raw JSON. Observed live on <c>qwen2.5-coder-7b-instruct-ghidra-v2</c>. Pinning
    /// <c>constrained</c> is the fix.
    /// </para>
    /// <para>
    /// Passing <see langword="null"/> clears the pin, restoring automatic detection - which is why this is
    /// not a one-way door. Clearing writes nothing rather than writing a lower-confidence row, so the next
    /// scan or live observation reclassifies from scratch.
    /// </para>
    /// </remarks>
    /// <param name="key">The provider key serving the model.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="request">The dialect to pin, or a null/empty dialect to clear the pin.</param>
    public ManagementResult<ProvidersResponse> SetModelToolDialect(
        string key, string modelName, ModelToolDialectWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_capabilityStore is null)
        {
            return ManagementResult<ProvidersResponse>.Fail(
                ManagementErrorType.Unavailable, "Tool-call capability overrides are not available.");
        }

        if (!_store.Snapshot.Options.Providers.ContainsKey(key))
        {
            return ManagementResult<ProvidersResponse>.Fail(
                ManagementErrorType.NotFound, $"Provider '{key}' not found.");
        }

        if (!ModelsFor(key).Any(m => string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase)))
        {
            return ManagementResult<ProvidersResponse>.Fail(
                ManagementErrorType.NotFound, $"Model '{modelName}' not found on provider '{key}'.");
        }

        if (string.IsNullOrWhiteSpace(request.Dialect))
        {
            _capabilityStore.ClearModelCapability(key, modelName);
            return ManagementResult<ProvidersResponse>.Ok(BuildProvidersResponse());
        }

        // Rejected rather than stored, unlike a row read back from the database. A name this build does not
        // know degrades to "not scanned" when found on disk, which is the right call for a row a *newer*
        // build wrote - but accepting one here would let a typo silently disable tool calling for the model
        // while the UI showed the pin as applied.
        if (!ToolCallDialectRegistry.TryGet(request.Dialect, out _))
        {
            return ManagementResult<ProvidersResponse>.Fail(
                ManagementErrorType.InvalidRequest,
                $"Unknown tool-call dialect '{request.Dialect}'.");
        }

        _capabilityStore.TryRecordModelCapability(new ModelToolCapability(
            key,
            modelName,
            request.Dialect,
            DetectionConfidence.Operator,
            Evidence: "Set by an operator."));

        return ManagementResult<ProvidersResponse>.Ok(BuildProvidersResponse());
    }

    /// <summary>
    /// Probes <paramref name="provider"/>'s endpoint for which API flavors it answers and persists the
    /// result. The shared scan-then-persist core of <see cref="ScanCapabilitiesAsync"/>,
    /// <see cref="TryScanCapabilitiesAsync"/>, and <see cref="RefreshFromEndpointAsync"/> - callers differ
    /// only in budget and in what they do with cancellation/failure around it, not in the probe itself.
    /// Assumes <see cref="_endpointScanner"/>/<see cref="_capabilityStore"/> are non-null; every caller has
    /// already checked that.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> itself (not just the budget) was canceled - propagated rather
    /// than swallowed, so an aborted caller never persists a result no probe actually established. See the
    /// callers for how each one handles that.
    /// </exception>
    private async Task<ProviderEndpointCapabilities> ScanAndPersistCapabilitiesAsync(
        string key, ProviderOptions provider, TimeSpan budgetDuration, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(budgetDuration);

        var capabilities = await _endpointScanner!.ScanAsync(key, provider, budget.Token).ConfigureAwait(false);

        // A canceled caller means we stopped asking, not that the provider failed to answer. The scanner is
        // deliberately fail-open and reports cancellation as an all-false payload with a ScanError, so
        // persisting it here would let a client disconnect overwrite a previously-good record with a result
        // no probe ever actually established. The budget elapsing is different and still recorded: that is a
        // real observation that the endpoint did not respond in time.
        cancellationToken.ThrowIfCancellationRequested();

        _capabilityStore!.SetProviderCapabilities(capabilities);
        return capabilities;
    }

    /// <summary>
    /// Runs a bounded, entirely best-effort capability scan after a provider save. Never throws and never
    /// affects the save's outcome.
    /// </summary>
    private async Task TryScanCapabilitiesAsync(
        string key, ProviderOptions provider, CancellationToken cancellationToken)
    {
        if (_endpointScanner is null || _capabilityStore is null)
        {
            return;
        }

        try
        {
            var capabilities = await ScanAndPersistCapabilitiesAsync(
                key, provider, CapabilityScanBudget, cancellationToken).ConfigureAwait(false);

            // Same best-effort spirit, and inside the same try: a provider save must not start depending on
            // a metadata probe succeeding.
            await TryResolveDialectsAsync(key, provider, capabilities, ModelsFor(key), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately swallowing everything, including cancellation. The provider has already been
            // persisted; surfacing a scan problem here would turn a successful save into a failed request
            // and leave the caller unsure whether their configuration was stored.
        }
    }

    /// <summary>Removes a provider by key, cascading to every model that routes to it. Rejected (404-shaped) if unknown. Historical metrics are retained.</summary>
    public async Task<ManagementResult<ProvidersResponse>> RemoveProviderAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.Providers.ContainsKey(key))
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.NotFound, $"Provider '{key}' not found.");
        }

        return await MutateAsync(() => _store.RemoveProviderAsync(key, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Adds or replaces a model route under a provider.</summary>
    /// <remarks>
    /// Editing an already-configured model preserves its current <see cref="ModelRouteEntry.Enabled"/>/
    /// <see cref="ModelRouteEntry.PresentUpstream"/> - this generic path has no way to express a change to
    /// either (that's <see cref="SetModelEnabledAsync"/> and the scan-driven reconciliation in
    /// <see cref="RefreshFromEndpointAsync"/>), so it must not silently reset them the way rebuilding the
    /// whole entry from just <see cref="ModelWriteRequest"/> would. <see cref="ModelRouteEntry"/> is a plain
    /// class with no <c>with</c>-expression support, so this is a manual field-by-field carry-forward rather
    /// than a copy expression. A genuinely new model defaults both flags <see langword="true"/>, matching
    /// today's behavior for a manually-added model (as opposed to one auto-added by a scan, which starts
    /// <see langword="false"/> - see <see cref="RefreshFromEndpointAsync"/>).
    /// </remarks>
    public async Task<ManagementResult<ProvidersResponse>> UpsertModelAsync(
        string providerKey, string modelName, ModelWriteRequest request, CancellationToken cancellationToken = default)
    {
        var existing = _store.Snapshot.Options.ModelList
            .FirstOrDefault(m => string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

        var entry = new ModelRouteEntry
        {
            ModelName = modelName,
            Provider = providerKey,
            ProviderModelId = string.IsNullOrWhiteSpace(request.ProviderModelId) ? modelName : request.ProviderModelId,
            Enabled = existing?.Enabled ?? true,
            PresentUpstream = existing?.PresentUpstream ?? true
        };

        var result = await MutateAsync(() => _store.UpsertModelAsync(entry, cancellationToken)).ConfigureAwait(false);

        // Classify the model that was just added, so the request path has a dialect before the model's first
        // real request rather than having to learn one from it. Uses the provider's already-scanned endpoint
        // flags - no scan is triggered here, since adding a model says nothing new about the provider.
        if (result.Success && _store.Snapshot.Options.Providers.TryGetValue(providerKey, out var provider))
        {
            await TryResolveDialectsAsync(
                providerKey,
                provider,
                _capabilityStore?.GetProviderCapabilities(providerKey),
                [entry],
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Every model route that forwards to <paramref name="providerKey"/>.</summary>
    private IReadOnlyList<ModelRouteEntry> ModelsFor(string providerKey) =>
        _store.Snapshot.Options.ModelList
            .Where(model => string.Equals(model.Provider, providerKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Runs tier 1-3 dialect detection over <paramref name="models"/> and records whatever it learns. Never
    /// throws and never affects the caller's outcome.
    /// </summary>
    /// <remarks>
    /// A model the resolver cannot classify is skipped rather than recorded as anything - the deliberate
    /// design from <c>tool-call-normalization.md</c> §3.2, since a missing row means "forward natively and
    /// classify from the first real response" while a wrong row arms the wrong scanner against every
    /// response the model produces. Writes go through
    /// <see cref="ToolCallCapabilityStore.TryRecordModelCapability"/>, whose confidence gate is what keeps
    /// a re-scan from overwriting something a live observation or an operator already established.
    /// </remarks>
    private async Task TryResolveDialectsAsync(
        string providerKey,
        ProviderOptions provider,
        ProviderEndpointCapabilities? endpointCapabilities,
        IReadOnlyList<ModelRouteEntry> models,
        CancellationToken cancellationToken)
    {
        if (_capabilityStore is null || models.Count == 0)
        {
            return;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(DialectResolutionBudget);

            foreach (var model in models)
            {
                // Checked per model rather than relying on the probes to fail fast, so a budget that has
                // already elapsed stops the loop instead of firing a doomed request for every remaining
                // model.
                if (budget.IsCancellationRequested)
                {
                    break;
                }

                var capability = await _dialectResolver.ResolveAsync(
                    providerKey,
                    provider,
                    endpointCapabilities,
                    model.ModelName,
                    model.ProviderModelId,
                    budget.Token).ConfigureAwait(false);

                // The caller aborting is not an observation about this model, and unlike the endpoint scan
                // there is no record here worth degrading - so stop rather than persist a partial pass.
                cancellationToken.ThrowIfCancellationRequested();

                if (capability is not null)
                {
                    _capabilityStore.TryRecordModelCapability(capability);
                }
            }
        }
        catch (Exception)
        {
            // Deliberately swallowing everything, including cancellation. Detection is an optimization over
            // Phase 4's live observation, which classifies any model this misses - so a failure here costs
            // one request's worth of scanning, never correctness, and must not fail the save that triggered
            // it.
        }
    }

    /// <summary>Removes a model route by name.</summary>
    public async Task<ManagementResult<ProvidersResponse>> RemoveModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.ModelList.Any(m => string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase)))
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.NotFound, $"Model '{modelName}' not found.");
        }

        return await MutateAsync(() => _store.RemoveModelAsync(modelName, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Switches a model on or off - the per-model twin of <see cref="SetEnabledAsync"/>. A dedicated route
    /// rather than a field on <see cref="UpsertModelAsync"/>'s write request, for the same reason: that path
    /// rebuilds the entry from a request shape that says nothing about <see cref="ModelRouteEntry.Enabled"/>,
    /// so it always preserves whatever was already stored rather than ever setting it - this is the only
    /// write path that can actually change it. Enforced immediately on the next request via
    /// <see cref="TotallyHot.ArcRouter.Proxy.IModelRouteResolver.IsModelEnabled"/>, no restart needed.
    /// </summary>
    public async Task<ManagementResult<ProvidersResponse>> SetModelEnabledAsync(
        string modelName, ModelEnabledWriteRequest request, CancellationToken cancellationToken = default)
    {
        var existing = _store.Snapshot.Options.ModelList
            .FirstOrDefault(m => string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.NotFound, $"Model '{modelName}' not found.");
        }

        var entry = new ModelRouteEntry
        {
            ModelName = existing.ModelName,
            Provider = existing.Provider,
            ProviderModelId = existing.ProviderModelId,
            Enabled = request.Enabled,
            PresentUpstream = existing.PresentUpstream
        };

        return await MutateAsync(() => _store.UpsertModelAsync(entry, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Sets or clears a provider's monthly budget caps. A null cap clears that dimension; both null removes the budget.</summary>
    public ManagementResult<ProvidersResponse> SetBudget(string providerKey, ProviderBudgetWriteRequest request)
    {
        if (_budgetStore is null)
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.Unavailable, "Budget storage is not available.");
        }

        if (!_store.Snapshot.Options.Providers.ContainsKey(providerKey))
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.NotFound, $"Provider '{providerKey}' not found.");
        }

        if (request.DollarCap is < 0 || request.TokenCap is < 0)
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.InvalidRequest, "Budget caps must be non-negative.");
        }

        BudgetWindow? window;
        try
        {
            window = ParseBudgetWindow(request.WindowKind, request.WindowHours);
        }
        catch (ArgumentException ex)
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.InvalidRequest, ex.Message);
        }

        try
        {
            _budgetStore.SetBudget(providerKey, request.DollarCap, request.TokenCap, window);
            return ManagementResult<ProvidersResponse>.Ok(BuildProvidersResponse());
        }
        catch (ArgumentException ex)
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.InvalidRequest, ex.Message);
        }
        catch (Exception)
        {
            // A persistence failure (e.g. the SQLite write) shouldn't leak storage detail to the caller.
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.Internal, "Failed to save the provider budget.");
        }
    }

    /// <summary>
    /// Parses a request's optional window fields into a <see cref="BudgetWindow"/>. Both null (the common
    /// case - an editor that hasn't opted into windows yet) yields <see langword="null"/>, which
    /// <see cref="ProviderBudgetStore.SetBudget"/> already treats as "keep Monthly".
    /// </summary>
    private static BudgetWindow? ParseBudgetWindow(string? windowKind, int? windowHours)
    {
        if (windowKind is null)
        {
            return null;
        }

        return windowKind switch
        {
            "Monthly" => new BudgetWindow.Monthly(),
            "Weekly" => new BudgetWindow.Weekly(),
            "RollingHours" when windowHours is > 0 => new BudgetWindow.RollingHours(windowHours.Value),
            "RollingHours" => throw new ArgumentException("windowHours must be a positive number of hours for a RollingHours window."),
            _ => throw new ArgumentException($"Unknown windowKind '{windowKind}'; expected 'Monthly', 'Weekly', or 'RollingHours'."),
        };
    }

    // Mirrors PriceCatalogModelPriceLookup.FreshnessFloor: the same "fresh enough to act on" definition
    // the request path and the startup health check already use. Duplicated rather than shared because
    // that constant is private to a request-path type this diagnosis view has no other reason to depend on.
    private static readonly TimeSpan PriceFreshnessFloor = TimeSpan.FromHours(24);

    /// <summary>
    /// For every configured <c>ModelRouting:ModelList</c> entry, reports whether the catalog currently
    /// resolves a fresh price for it and, if so, whether that price is an approximate match (§5.7's ladder,
    /// below <see cref="ResolutionRung.Exact"/>/<see cref="ResolutionRung.OperatorOverride"/>). The
    /// Governance price-overrides pane's read-only diagnosis view: this is what tells an operator *which*
    /// models actually need an override, before they add one.
    /// </summary>
    public ManagementResult<IReadOnlyList<PriceResolutionDiagnosisView>> GetPriceResolutionDiagnosis()
    {
        if (_priceCatalogRepository is null)
        {
            return ManagementResult<IReadOnlyList<PriceResolutionDiagnosisView>>.Fail(
                ManagementErrorType.Unavailable, "The price catalog is not available.");
        }

        var rows = _store.Snapshot.Options.ModelList
            .Select(entry =>
            {
                var price = _priceCatalogRepository.GetFreshPrice(new ModelKey(entry.ModelName, entry.Provider), PriceFreshnessFloor);
                return new PriceResolutionDiagnosisView(entry.ModelName, entry.Provider, price is not null, price?.IsApproximateMatch ?? false);
            })
            .OrderBy(r => r.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ManagementResult<IReadOnlyList<PriceResolutionDiagnosisView>>.Ok(rows);
    }

    /// <summary>
    /// Lists every configured price override (§5.7's <see cref="ResolutionRung.OperatorOverride"/> rung),
    /// backing the Governance price-overrides pane's read-only diagnosis view.
    /// </summary>
    public ManagementResult<IReadOnlyList<ModelAliasOverride>> ListPriceOverrides()
    {
        if (_overrideStore is null)
        {
            return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Fail(
                ManagementErrorType.Unavailable, "Price overrides are not available.");
        }

        return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Ok(_overrideStore.GetAll());
    }

    /// <summary>
    /// Adds or replaces an operator price override. <paramref name="request"/>'s <c>ModelName</c> must name
    /// a currently configured <c>ModelRouting:ModelList</c> entry - an override pointing at a model that
    /// doesn't exist could never resolve to a usable <c>ResolvedModelIdentity</c>, so it is rejected up
    /// front rather than silently stored and always missing at resolve time.
    /// </summary>
    public ManagementResult<IReadOnlyList<ModelAliasOverride>> SetPriceOverride(PriceOverrideWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_overrideStore is null)
        {
            return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Fail(
                ManagementErrorType.Unavailable, "Price overrides are not available.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceName) ||
            string.IsNullOrWhiteSpace(request.AggregatorModelKey) ||
            string.IsNullOrWhiteSpace(request.ModelName))
        {
            return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Fail(
                ManagementErrorType.InvalidRequest, "SourceName, AggregatorModelKey, and ModelName are all required.");
        }

        if (!_store.Snapshot.Options.ModelList.Any(m => string.Equals(m.ModelName, request.ModelName, StringComparison.OrdinalIgnoreCase)))
        {
            return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Fail(
                ManagementErrorType.InvalidRequest, $"Model '{request.ModelName}' is not configured.");
        }

        try
        {
            _overrideStore.Upsert(request.SourceName, request.AggregatorModelKey, request.ModelName);
            return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Ok(_overrideStore.GetAll());
        }
        catch (Exception)
        {
            return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Fail(
                ManagementErrorType.Internal, "Failed to save the price override.");
        }
    }

    /// <summary>Removes an operator price override. A no-op mapping (nothing removed) is rejected as 404-shaped.</summary>
    public ManagementResult<IReadOnlyList<ModelAliasOverride>> RemovePriceOverride(string sourceName, string aggregatorModelKey)
    {
        if (_overrideStore is null)
        {
            return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Fail(
                ManagementErrorType.Unavailable, "Price overrides are not available.");
        }

        if (!_overrideStore.Remove(sourceName, aggregatorModelKey))
        {
            return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Fail(
                ManagementErrorType.NotFound, $"No override found for source '{sourceName}' / key '{aggregatorModelKey}'.");
        }

        return ManagementResult<IReadOnlyList<ModelAliasOverride>>.Ok(_overrideStore.GetAll());
    }

    /// <summary>
    /// Totals over a preset window for the header ticker and summary tiles (Phase 4, §5.15). Backed by
    /// <see cref="IUsageRollupStore.Summary"/>.
    /// </summary>
    /// <param name="window">One of <c>"day"</c>, <c>"week"</c>, <c>"month"</c>, or <c>"all"</c>.</param>
    public ManagementResult<UsageSummary> GetUsageSummary(string window)
    {
        if (_rollupStore is null)
        {
            return ManagementResult<UsageSummary>.Fail(ManagementErrorType.Unavailable, "Usage rollups are not available.");
        }

        var now = DateTimeOffset.UtcNow;

        // Aligned to a UTC day boundary, not just "now minus N" - UsageRollupStore.Summary reads whole
        // P1D buckets keyed by bucket_start_utc, so an unaligned 'from' (e.g. now.AddDays(-1), which lands
        // mid-day) would fall after yesterday's bucket start and exclude that fully-elapsed bucket entirely.
        var todayStartUtc = new DateTimeOffset(now.Date, TimeSpan.Zero);
        DateTimeOffset from;
        switch (window)
        {
            case "day":
                from = todayStartUtc.AddDays(-1);
                break;
            case "week":
                from = todayStartUtc.AddDays(-7);
                break;
            case "month":
                from = todayStartUtc.AddMonths(-1);
                break;
            case "all":
                from = DateTimeOffset.UnixEpoch;
                break;
            default:
                return ManagementResult<UsageSummary>.Fail(ManagementErrorType.InvalidRequest, "window must be 'day', 'week', 'month', or 'all'.");
        }

        try
        {
            return ManagementResult<UsageSummary>.Ok(_rollupStore.Summary(from, now));
        }
        catch (Exception)
        {
            return ManagementResult<UsageSummary>.Fail(ManagementErrorType.Internal, "Failed to read the usage summary.");
        }
    }

    /// <summary>
    /// The Model Distribution / Cost Analytics chart feed (Phase 4, §5.15). Backed by
    /// <see cref="IUsageRollupStore.Query"/>.
    /// </summary>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Exclusive range end; must be after <paramref name="from"/>.</param>
    /// <param name="width">Bucket width: <c>"hour"</c> or <c>"day"</c>.</param>
    /// <param name="groupBy"><c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>.</param>
    public ManagementResult<IReadOnlyList<UsageRollupBucket>> GetUsageRollup(DateTimeOffset from, DateTimeOffset to, string width, string groupBy)
    {
        if (_rollupStore is null)
        {
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.Unavailable, "Usage rollups are not available.");
        }

        if (to <= from)
        {
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.InvalidRequest, "'to' must be after 'from'.");
        }

        string bucketWidth;
        switch (width)
        {
            case "hour":
                bucketWidth = "PT1H";
                break;
            case "day":
                bucketWidth = "P1D";
                break;
            default:
                return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.InvalidRequest, "width must be 'hour' or 'day'.");
        }

        if (groupBy is not ("model" or "provider" or "day"))
        {
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.InvalidRequest, "groupBy must be 'model', 'provider', or 'day'.");
        }

        try
        {
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Ok(_rollupStore.Query(from, to, bucketWidth, groupBy));
        }
        catch (Exception)
        {
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.Internal, "Failed to read usage rollups.");
        }
    }

    /// <summary>
    /// Switches a provider on or off. An unknown key is rejected (404-shaped) rather than silently doing
    /// nothing - a toggle that reports success while changing nothing is the failure this surface exists to
    /// avoid. Enforced immediately on the next request via <see cref="ProviderOptions.Enabled"/>'s own
    /// documented routing-path gate (<see cref="TotallyHot.ArcRouter.Proxy.IModelRouteResolver.IsProviderEnabled"/>),
    /// no restart needed.
    /// </summary>
    public async Task<ManagementResult<ProvidersResponse>> SetEnabledAsync(
        string key, ProviderEnabledWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.Providers.TryGetValue(key, out var existing))
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.NotFound, $"Provider '{key}' not found.");
        }

        var provider = WithEnabled(existing, request.Enabled);
        return await MutateAsync(() => _store.UpsertProviderAsync(key, provider, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Queries a provider's own OpenAI-shaped model list (best-effort; reports <c>Supported: false</c> when unavailable).</summary>
    public async Task<ManagementResult<DiscoverModelsResponse>> DiscoverModelsAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.Providers.TryGetValue(providerKey, out var provider))
        {
            return ManagementResult<DiscoverModelsResponse>.Fail(ManagementErrorType.NotFound, $"Provider '{providerKey}' not found.");
        }

        var result = await DiscoverModelsCoreAsync(provider, cancellationToken).ConfigureAwait(false);
        return ManagementResult<DiscoverModelsResponse>.Ok(result);
    }

    /// <summary>
    /// The consolidated "Refresh from endpoint" operation: discovers this provider's live model list,
    /// reconciles it into <see cref="ModelRoutingOptions.ModelList"/> (adding newly-seen ids as stopped,
    /// flagging previously-configured ones no longer reported - never deleting), then re-probes endpoint
    /// flavors and re-runs tier 1-3 dialect detection, so one click keeps both the model list and its
    /// capability data current. This is the router noticing added/removed models and their capabilities
    /// itself, rather than the GUI orchestrating separate discovery and capability-scan calls.
    /// </summary>
    /// <remarks>
    /// Reconciliation only runs when discovery reports <see cref="DiscoverModelsResponse.Supported"/> - a
    /// provider that doesn't answer OpenAI-shaped <c>/v1/models</c> (hosted Anthropic, Bedrock) must have
    /// its existing models left untouched, not mass-flagged absent just because the question couldn't be
    /// asked. The endpoint-flavor scan and dialect detection are skipped (not failed) when no capability
    /// store is wired up, mirroring <see cref="TryScanCapabilitiesAsync"/>'s degrade - model reconciliation
    /// is this method's primary duty and must not become unavailable just because that secondary step
    /// can't run.
    /// </remarks>
    /// <param name="key">The provider key to refresh.</param>
    /// <param name="cancellationToken">Cancels the discovery/scan/detection probes.</param>
    public async Task<ManagementResult<ProvidersResponse>> RefreshFromEndpointAsync(
        string key, CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.Providers.TryGetValue(key, out var provider))
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.NotFound, $"Provider '{key}' not found.");
        }

        var discovery = await DiscoverModelsCoreAsync(provider, cancellationToken).ConfigureAwait(false);
        if (discovery.Supported)
        {
            await ReconcileModelsAsync(key, discovery.Models, cancellationToken).ConfigureAwait(false);
        }

        if (_endpointScanner is not null && _capabilityStore is not null)
        {
            var capabilities = await ScanAndPersistCapabilitiesAsync(
                key, provider, ExplicitCapabilityScanBudget, cancellationToken).ConfigureAwait(false);
            await TryResolveDialectsAsync(key, provider, capabilities, ModelsFor(key), cancellationToken).ConfigureAwait(false);
        }

        return ManagementResult<ProvidersResponse>.Ok(BuildProvidersResponse());
    }

    /// <summary>
    /// Reconciles <paramref name="providerKey"/>'s configured models against what its endpoint just
    /// reported, updating only <see cref="ModelRouteEntry.PresentUpstream"/> - never
    /// <see cref="ModelRouteEntry.Enabled"/>, which is the operator's own intent and must survive a scan
    /// untouched in either direction. Part of <see cref="RefreshFromEndpointAsync"/>.
    /// </summary>
    /// <remarks>
    /// One <see cref="IProviderConfigStore.UpsertModelAsync"/> call per entry that actually changed - a
    /// no-op is skipped so a refresh that changes nothing doesn't bump the config version or touch disk.
    /// Model ids are compared ordinally (exact match): a provider's own id casing/spelling is
    /// authoritative, and guessing at case-insensitivity here would risk conflating two genuinely different
    /// upstream ids.
    /// </remarks>
    /// <param name="providerKey">The provider whose models are being reconciled.</param>
    /// <param name="discoveredIds">The model ids the endpoint just reported.</param>
    /// <param name="cancellationToken">Cancels the underlying persistence calls.</param>
    private async Task ReconcileModelsAsync(
        string providerKey, IReadOnlyList<string> discoveredIds, CancellationToken cancellationToken)
    {
        var discovered = new HashSet<string>(discoveredIds, StringComparer.Ordinal);
        var configured = ModelsFor(providerKey);

        foreach (var entry in configured)
        {
            var isPresent = discovered.Contains(entry.ProviderModelId);
            if (entry.PresentUpstream == isPresent)
            {
                continue;
            }

            await _store.UpsertModelAsync(
                new ModelRouteEntry
                {
                    ModelName = entry.ModelName,
                    Provider = entry.Provider,
                    ProviderModelId = entry.ProviderModelId,
                    Enabled = entry.Enabled,
                    PresentUpstream = isPresent
                },
                cancellationToken).ConfigureAwait(false);
        }

        var configuredIds = new HashSet<string>(configured.Select(e => e.ProviderModelId), StringComparer.Ordinal);
        foreach (var id in discoveredIds)
        {
            if (configuredIds.Contains(id))
            {
                continue;
            }

            // Auto-added, but never auto-started: the router notices the model exists, the operator decides
            // whether it should receive traffic. Same "id becomes both the client-facing name and provider
            // id" convention the old manual "click a discovered id to add it" affordance used.
            await _store.UpsertModelAsync(
                new ModelRouteEntry { ModelName = id, Provider = providerKey, ProviderModelId = id, Enabled = false, PresentUpstream = true },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Projects the store's current snapshot into the masked, client-facing <see cref="ProvidersResponse"/> shape.</summary>
    private ProvidersResponse BuildProvidersResponse()
    {
        var options = _store.Snapshot.Options;

        var providers = options.Providers
            .Select(kvp =>
            {
                var models = options.ModelList
                    .Where(m => string.Equals(m.Provider, kvp.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(m =>
                    {
                        // In-memory snapshot read, not a query - the same lookup Phase 4 makes on every
                        // request that carries tools. Null when the model has never been classified (no
                        // scan has run, and no live response has been observed yet).
                        var capability = _capabilityStore?.GetModelCapability(kvp.Key, m.ModelName);
                        return new ModelView(
                            m.ModelName,
                            m.ProviderModelId,
                            Dialect: capability?.Dialect,
                            Confidence: capability?.Confidence.ToString(),
                            Enabled: m.Enabled,
                            PresentUpstream: m.PresentUpstream);
                    })
                    .ToList();

                var headers = kvp.Value.Headers
                    .Select(h =>
                    {
                        var source = ClassifyHeaderSource(h);
                        // The one place a stored literal header value can leave the application. A locked
                        // header is a secret the operator chose to make unreadable, so its value is dropped
                        // here rather than at any caller - see docs/gui/secret-field.md.
                        var locked = source == HeaderValueSource.Literal && h.Locked;
                        // ValueEnvVar is only meaningful for an envVar-sourced header; a header with both
                        // fields somehow set (legacy/bad data) classifies as literal, and must not also
                        // surface the env-var name - that would violate HeaderView's documented contract.
                        return new HeaderView(
                            h.Name,
                            source,
                            source == HeaderValueSource.EnvVar ? h.ValueEnvVar : null,
                            Value: source == HeaderValueSource.Literal && !locked ? h.Value : null,
                            Locked: locked);
                    })
                    .ToList();

                // Current-month caps and spend for the budget bars. Absent a budget store (or a provider with
                // no budget/usage), this is an all-zero, no-cap state, so the caller renders "no cap set".
                var budget = _budgetStore?.GetStatus(kvp.Key) ?? default;

                return new ProviderView(
                    Key: kvp.Key,
                    Name: kvp.Value.Name,
                    BaseUrl: kvp.Value.BaseUrl,
                    AuthHeaderName: kvp.Value.AuthHeaderName,
                    Models: models,
                    Headers: headers,
                    IsFree: kvp.Value.IsFree,
                    DollarCap: budget.DollarCap,
                    TokenCap: budget.TokenCap,
                    DollarSpent: budget.DollarSpent,
                    TokensUsed: budget.TokensUsed,
                    Enabled: kvp.Value.Enabled,
                    EndpointCapabilities: _capabilityStore?.GetProviderCapabilities(kvp.Key),
                    ProviderType: kvp.Value.ProviderType,
                    UsageLastRecordedAtUtc: budget.LastUsageAtUtc,
                    RateLimit: BuildRateLimitView(kvp.Key),
                    WindowKind: budget.WindowKind is { Length: > 0 } ? budget.WindowKind : "Monthly",
                    NextResetUtc: budget.NextResetUtc);
            })
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProvidersResponse(providers);
    }

    /// <summary>
    /// Builds a provider's typed rate-limit snapshot view from its captured headers, or <see langword="null"/>
    /// when no repository is wired up or no header has ever been captured for this provider - both read as
    /// "no rate-limit data observed yet" to the caller.
    /// </summary>
    private ProviderRateLimitView? BuildRateLimitView(string providerKey)
    {
        if (_priceCatalogRepository is null)
        {
            return null;
        }

        var (headers, observedAtUtc) = _priceCatalogRepository.GetRateLimitSnapshot(providerKey);
        if (headers.Count == 0 || observedAtUtc is not { } observedAt)
        {
            return null;
        }

        var snapshot = RateLimitSnapshotParser.Parse(headers, observedAt);
        var isStale = DateTimeOffset.UtcNow - observedAt > _rateLimitStalenessThreshold;
        var projections = BuildExhaustionProjections(providerKey, snapshot, observedAt);
        return new ProviderRateLimitView(snapshot, observedAt, isStale, projections);
    }

    /// <summary>
    /// Projects each standard-family dimension's time-to-exhaustion (§5.9) by pairing the current snapshot
    /// with the earliest history point inside <see cref="ProjectionLookback"/> that still carries a
    /// <c>Remaining</c> value for that dimension. A dimension with no such history point (too new, or the
    /// header simply wasn't captured that recently) is omitted rather than projected from stale history.
    /// </summary>
    private Dictionary<string, RateLimitExhaustionProjection> BuildExhaustionProjections(
        string providerKey, RateLimitSnapshotView latest, DateTimeOffset observedAtUtc)
    {
        var projections = new Dictionary<string, RateLimitExhaustionProjection>(StringComparer.OrdinalIgnoreCase);
        if (latest.StandardDimensions.Count == 0)
        {
            return projections;
        }

        var history = _priceCatalogRepository!.GetRateLimitHistory(providerKey, observedAtUtc - ProjectionLookback);

        // Parse each history bucket once and reuse the parsed snapshot across all dimensions below,
        // rather than reparsing the same headers once per dimension.
        var parsedHistory = new List<(DateTimeOffset BucketUtc, RateLimitSnapshotView Snapshot)>(history.Count);
        foreach (var bucket in history)
        {
            parsedHistory.Add((bucket.BucketUtc, RateLimitSnapshotParser.Parse(bucket.Headers, bucket.BucketUtc)));
        }

        foreach (var (dimensionName, laterDimension) in latest.StandardDimensions)
        {
            RateLimitObservation? earliest = null;
            foreach (var (bucketUtc, bucketSnapshot) in parsedHistory)
            {
                // history is chronologically ascending, so the first bucket that captured this dimension's
                // remaining value is the earliest usable observation.
                if (bucketSnapshot.StandardDimensions.TryGetValue(dimensionName, out var bucketDimension)
                    && bucketDimension.Remaining is not null)
                {
                    earliest = new RateLimitObservation(bucketUtc, bucketDimension.Remaining, bucketDimension.ResetAt);
                    break;
                }
            }

            if (earliest is null)
            {
                continue;
            }

            var later = new RateLimitObservation(observedAtUtc, laterDimension.Remaining, laterDimension.ResetAt);
            var projection = RateLimitProjection.Project(earliest, later);
            if (projection is not null)
            {
                projections[dimensionName] = projection;
            }
        }

        return projections;
    }

    /// <summary>
    /// Returns a provider's rate-limit remaining-over-time series for the last <paramref name="hours"/>
    /// hours, per standard-family dimension - the Providers card's trend-chart data source (§5.9).
    /// </summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="hours">How far back to look, clamped to [0.25, 720] hours (15 minutes to 30 days).</param>
    public ManagementResult<RateLimitHistoryResponse> GetRateLimitHistory(string providerKey, double hours)
    {
        if (!_store.Snapshot.Options.Providers.ContainsKey(providerKey))
        {
            return ManagementResult<RateLimitHistoryResponse>.Fail(ManagementErrorType.NotFound, $"Provider '{providerKey}' not found.");
        }

        if (_priceCatalogRepository is null)
        {
            return ManagementResult<RateLimitHistoryResponse>.Fail(ManagementErrorType.Unavailable, "Rate-limit history is not available.");
        }

        if (!double.IsFinite(hours))
        {
            return ManagementResult<RateLimitHistoryResponse>.Fail(ManagementErrorType.InvalidRequest, "hours must be a finite number.");
        }

        var clampedHours = Math.Clamp(hours, 0.25, 24 * 30);
        var sinceUtc = DateTimeOffset.UtcNow.AddHours(-clampedHours);
        var buckets = _priceCatalogRepository.GetRateLimitHistory(providerKey, sinceUtc);

        var series = new Dictionary<string, List<RateLimitHistoryPointView>>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? previousBucketUtc = null;
        foreach (var bucket in buckets)
        {
            // A gap of more than a minute between consecutive stored buckets means nothing was captured
            // in between - insert an explicit null point at the first missing minute for every dimension
            // already in the series so the stepped chart (connectNulls: false) renders a break instead of
            // implying the value held steady across the gap.
            if (previousBucketUtc is { } prev && bucket.BucketUtc - prev > TimeSpan.FromMinutes(1))
            {
                var gapUtc = prev.AddMinutes(1);
                foreach (var points in series.Values)
                {
                    points.Add(new RateLimitHistoryPointView(gapUtc, null, null));
                }
            }

            var bucketSnapshot = RateLimitSnapshotParser.Parse(bucket.Headers, bucket.BucketUtc);
            foreach (var (dimensionName, dimension) in bucketSnapshot.StandardDimensions)
            {
                if (!series.TryGetValue(dimensionName, out var points))
                {
                    points = [];
                    series[dimensionName] = points;
                }

                points.Add(new RateLimitHistoryPointView(bucket.BucketUtc, dimension.Remaining, dimension.Limit));
            }

            // A dimension already tracked from an earlier bucket but absent from this one (header not
            // captured/unparsable that minute, while other dimensions still were) needs its own null point
            // at this bucket's timestamp too - otherwise its series simply skips the x-value, and the
            // stepped line visually holds the previous value through what should render as a gap.
            foreach (var (dimensionName, points) in series)
            {
                if (!bucketSnapshot.StandardDimensions.ContainsKey(dimensionName))
                {
                    points.Add(new RateLimitHistoryPointView(bucket.BucketUtc, null, null));
                }
            }

            previousBucketUtc = bucket.BucketUtc;
        }

        var dimensions = series.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<RateLimitHistoryPointView>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
        return ManagementResult<RateLimitHistoryResponse>.Ok(new RateLimitHistoryResponse(dimensions));
    }

    /// <summary>Classifies a stored header's <see cref="HeaderValueSource"/> from which of its fields is set.</summary>
    private static string ClassifyHeaderSource(ProviderHeader header) =>
        !string.IsNullOrWhiteSpace(header.Value) ? HeaderValueSource.Literal
            : !string.IsNullOrWhiteSpace(header.ValueEnvVar) ? HeaderValueSource.EnvVar
            : HeaderValueSource.None;

    /// <summary>
    /// Builds the <see cref="ProviderOptions"/> to store from an incoming write request, merged over any
    /// existing provider. Fields fall back to the existing value when omitted, then to sensible defaults;
    /// each custom header is resolved by <see cref="ResolveHeader"/>.
    /// </summary>
    private static ProviderOptions MergeProvider(ProviderWriteRequest request, ProviderOptions? existing)
    {
        // A `with` over the existing provider (or a default one when adding), rather than a hand-listed
        // rebuild. Only the fields this request can actually change are named below; everything else -
        // EnableToolCallGuard, the four Aws* fields, and anything added to ProviderOptions later - carries
        // across by construction. The previous hand-written list had silently fallen behind the type and
        // was resetting exactly those fields on every edit (docs/router/backlog.md item 1).
        //
        // `new ProviderOptions()`'s own defaults reproduce the old terminal fallbacks exactly: BaseUrl "",
        // AuthHeaderName "Authorization", IsFree false, Enabled true, Headers [].
        var baseline = existing ?? new ProviderOptions();

        return baseline with
        {
            // Name: null from the request preserves the existing value; any other value (including empty/whitespace)
            // is normalized - empty/whitespace becomes null (explicitly cleared).
            Name = request.ProviderName is null ? baseline.Name : NormalizeNameField(request.ProviderName),
            BaseUrl = request.BaseUrl ?? baseline.BaseUrl,
            // Normalized on the same rule as Name: null preserves, anything else is trimmed, and
            // empty/whitespace becomes null. Without this a caller sending "" or " " would persist it, and
            // since a value that doesn't parse as a ProviderType member reads as "Other" in the editor
            // anyway, the only thing storing it achieves is junk in model-routing.json.
            ProviderType = request.ProviderType is null ? baseline.ProviderType : NormalizeNameField(request.ProviderType),
            AuthHeaderName = request.AuthHeaderName ?? baseline.AuthHeaderName,
            // The caller always sends the full header set (a null list means "keep existing", e.g. a
            // legacy/partial caller); a provided list replaces it wholesale, one header at a time through
            // ResolveHeader so a blank value preserves what's already stored under that name.
            Headers = request.Headers is null
                ? baseline.Headers
                : request.Headers
                    .Where(h => !string.IsNullOrWhiteSpace(h.Name))
                    .Select(h => ResolveHeader(h, baseline.Headers))
                    .ToList(),
            IsFree = request.IsFree ?? baseline.IsFree,
            Enabled = request.Enabled ?? baseline.Enabled,
        };
    }

    /// <summary>
    /// Copies a provider with a different <see cref="ProviderOptions.Enabled"/>.
    /// </summary>
    /// <remarks>
    /// This used to copy every property by hand, to avoid <see cref="MergeProvider"/>'s field loss - and had
    /// itself fallen a field behind, silently clearing <see cref="ProviderOptions.EnableToolCallGuard"/> on
    /// every Stop/Play toggle. Now that <see cref="ProviderOptions"/> is a record, <c>with</c> carries
    /// everything across by construction and the hazard is gone from both methods at once.
    /// </remarks>
    private static ProviderOptions WithEnabled(ProviderOptions source, bool enabled) =>
        source with { Enabled = enabled };

    /// <summary>
    /// Normalizes a provider display name: empty or whitespace-only strings become null, so clearing the
    /// field in the UI results in a consistent null rather than an empty string; any other value is trimmed.
    /// </summary>
    private static string? NormalizeNameField(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Resolves one incoming header write to the <see cref="ProviderHeader"/> to store: a non-blank
    /// <see cref="HeaderWriteRequest.Value"/> is a literal, a non-blank
    /// <see cref="HeaderWriteRequest.ValueEnvVar"/> is an env-var reference, and - since a locked header's
    /// value is never returned for a caller to resend - both blank preserves whatever value is already
    /// stored under this header's name, and otherwise stores as <see cref="HeaderValueSource.None"/>.
    /// <para>
    /// <see cref="HeaderWriteRequest.Locked"/> travels with the header independently of its value, so an
    /// operator can lock an already-stored secret without retyping it - and it is also what makes the
    /// blank rule safe to relax: an <em>explicitly unlocked</em> blank write clears the stored value,
    /// because the caller was shown that value in full and chose to empty the field. That is how the
    /// editor's unlock destroys a secret. Null (the legacy shape) keeps the old preserve-on-blank
    /// behavior in every case.
    /// </para>
    /// </summary>
    private static ProviderHeader ResolveHeader(HeaderWriteRequest request, IReadOnlyList<ProviderHeader> existingHeaders)
    {
        var name = request.Name!.Trim();

        // A caller that predates the flag stored every literal write-only, so its headers keep meaning
        // "locked" rather than silently becoming readable.
        var locked = request.Locked ?? true;

        if (!string.IsNullOrWhiteSpace(request.Value))
        {
            return new ProviderHeader { Name = name, Value = request.Value, ValueEnvVar = null, Locked = locked };
        }

        // An env-var-backed header keeps its secret in the environment rather than in configuration, so
        // there is nothing to withhold and it always stores unlocked.
        if (!string.IsNullOrWhiteSpace(request.ValueEnvVar))
        {
            return new ProviderHeader { Name = name, Value = null, ValueEnvVar = request.ValueEnvVar.Trim(), Locked = false };
        }

        if (request.Locked is false)
        {
            return new ProviderHeader { Name = name, Value = null, ValueEnvVar = null, Locked = false };
        }

        // HTTP header names are case-insensitive, so "X-Foo" and "x-foo" must be treated as the same
        // header when looking up the value to preserve - otherwise a casing mismatch between what was
        // stored and what the caller resends silently drops the stored secret instead of keeping it.
        var existing = existingHeaders.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
        var preservedValue = existing?.Value;

        return new ProviderHeader
        {
            Name = name,
            Value = preservedValue,
            ValueEnvVar = existing?.ValueEnvVar,
            // Only a literal can be a secret, so a preserved env-var (or valueless) header stores unlocked
            // no matter what the caller asked for.
            Locked = !string.IsNullOrWhiteSpace(preservedValue) && locked
        };
    }

    /// <summary>Runs a store mutation and maps it to a <see cref="ManagementResult{T}"/>, translating validation/argument failures into <see cref="ManagementErrorType.InvalidRequest"/>.</summary>
    private async Task<ManagementResult<ProvidersResponse>> MutateAsync(Func<Task> mutation)
    {
        try
        {
            await mutation().ConfigureAwait(false);
            return ManagementResult<ProvidersResponse>.Ok(BuildProvidersResponse());
        }
        catch (OptionsValidationException ex)
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.InvalidRequest, string.Join("; ", ex.Failures));
        }
        catch (ArgumentException ex)
        {
            // Bad input reaching the store (e.g. a blank key/model name) is a client error, not a fault.
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.InvalidRequest, ex.Message);
        }
    }

    /// <summary>Queries a provider's live OpenAI-shaped model list, sending the same auth/extra headers the forwarding path uses.</summary>
    private async Task<DiscoverModelsResponse> DiscoverModelsCoreAsync(ProviderOptions provider, CancellationToken cancellationToken)
    {
        Uri target;
        try
        {
            target = new Uri(ProviderUrlBuilder.BuildModelsUrl(provider.BaseUrl), UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            return new DiscoverModelsResponse(Supported: false, Models: [], Error: $"Invalid BaseUrl: {ex.Message}");
        }

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, target);

        // The provider's credentials and configured custom headers, sent identically to the forwarding path.
        // This is how a provider that requires an extra header for discovery gets it (e.g. Anthropic's
        // anthropic-version) without any provider-specific code here.
        ProviderCredentialResolver.ApplyToRequest(requestMessage, provider, _environment);

        try
        {
            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new DiscoverModelsResponse(
                    Supported: false,
                    Models: [],
                    Error: $"Provider returned {(int)response.StatusCode} for {target}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var models = ParseModelIds(body);
            return new DiscoverModelsResponse(Supported: true, Models: models, Error: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new DiscoverModelsResponse(Supported: false, Models: [], Error: ex.Message);
        }
    }

    /// <summary>Parses an OpenAI-shaped model-list JSON body and returns the <c>id</c> of each entry in its <c>data</c> array.</summary>
    private static IReadOnlyList<string> ParseModelIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }
}

/// <summary>The kind of failure a <see cref="ManagementFacade"/> call reports, so each surface can map it to its own error shape.</summary>
public enum ManagementErrorType
{
    /// <summary>No error; <see cref="ManagementResult{T}.Success"/> is <see langword="true"/>.</summary>
    None,

    /// <summary>The referenced provider/model does not exist.</summary>
    NotFound,

    /// <summary>The request itself is invalid (validation failure, negative budget cap, etc.).</summary>
    InvalidRequest,

    /// <summary>The operation requires a dependency that isn't configured (e.g. no budget store).</summary>
    Unavailable,

    /// <summary>An unexpected failure occurred (e.g. a persistence error); the message is deliberately generic.</summary>
    Internal
}

/// <summary>
/// The outcome of a <see cref="ManagementFacade"/> write: either the refreshed <typeparamref name="T"/> on
/// success, or an <see cref="ManagementErrorType"/> and message on failure. Both the REST endpoints and the
/// MCP tools translate this into their own transport's error shape (HTTP status codes / MCP tool errors).
/// </summary>
public readonly record struct ManagementResult<T>(bool Success, T? Value, ManagementErrorType ErrorType, string? ErrorMessage)
{
    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    public static ManagementResult<T> Ok(T value) => new(true, value, ManagementErrorType.None, null);

    /// <summary>Creates a failed result with the given <paramref name="errorType"/> and <paramref name="message"/>.</summary>
    public static ManagementResult<T> Fail(ManagementErrorType errorType, string message) => new(false, default, errorType, message);
}

/// <summary>The credential source for a stored custom header, mirrored in <c>TotallyHot.ArcRouter.Gui.Admin.HeaderValueSource</c>'s naming.</summary>
public static class HeaderValueSource
{
    /// <summary>A literal value is stored (never returned by any surface; only this source marker is).</summary>
    public const string Literal = "literal";

    /// <summary>The value is read at request time from an environment variable (see <see cref="HeaderView.ValueEnvVar"/>).</summary>
    public const string EnvVar = "envVar";

    /// <summary>Neither a literal value nor an environment variable is configured.</summary>
    public const string None = "none";
}

/// <summary>A single provider as returned to a management caller, with credentials masked.</summary>
/// <param name="Key">The provider key.</param>
/// <param name="Name">The user-friendly display name for this provider; null when not set.</param>
/// <param name="BaseUrl">The provider's absolute base URL.</param>
/// <param name="AuthHeaderName">The header carrying the credential.</param>
/// <param name="Models">The models configured to route to this provider.</param>
/// <param name="Headers">The provider's configured custom headers, masked (see <see cref="HeaderView"/>).</param>
/// <param name="IsFree">Whether this provider costs nothing, making its models' cost a known zero rather than unknown.</param>
/// <param name="DollarCap">The provider's monthly USD budget cap, or null for no dollar budget.</param>
/// <param name="TokenCap">The provider's monthly total-token budget cap, or null for no token budget.</param>
/// <param name="DollarSpent">USD spent against this provider in the current month.</param>
/// <param name="TokensUsed">
/// Total tokens used against this provider in the current month - prompt, completion, and cache
/// creation/read tokens combined (see <see cref="ProviderBudgetState.TokensUsed"/>).
/// </param>
/// <param name="Enabled">
/// Whether the operator has this provider switched on. Enforced immediately on the next request via
/// <see cref="ProviderOptions.Enabled"/>'s own documented routing-path gate
/// (<see cref="TotallyHot.ArcRouter.Proxy.IModelRouteResolver.IsProviderEnabled"/>), no restart needed.
/// </param>
/// <param name="EndpointCapabilities">
/// Which API flavors this provider's endpoint answers, as last recorded by
/// <see cref="ManagementFacade.ScanCapabilitiesAsync"/> (<c>docs/router/tool-call-normalization.md</c>
/// §3.3), or <see langword="null"/> when no capability store is wired up or the endpoint has never been
/// scanned.
/// </param>
/// <param name="ProviderType">
/// The provider family the operator selected in the editor (see <see cref="ProviderOptions.ProviderType"/>),
/// or <see langword="null"/> for a provider configured before the field existed. Returned so that reopening
/// a provider restores the right type rather than defaulting to "Other".
/// </param>
/// <param name="UsageLastRecordedAtUtc">
/// The UTC instant the most recent request's usage was recorded against this provider (the same instant
/// backing <see cref="DollarSpent"/>/<see cref="TokensUsed"/>), or <see langword="null"/> if no usage has
/// been recorded this month yet. Backs the Governance card's "estimated from intercepted traffic" footer.
/// </param>
/// <param name="RateLimit">
/// This provider's most recently captured rate-limit response headers - <c>anthropic-ratelimit-*</c>
/// (<c>docs/router/anthropic-reported-usage-plan.md</c> §5) or OpenAI's <c>x-ratelimit-*</c>
/// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §6.2), whichever family this provider's
/// responses carry - or <see langword="null"/> when no repository is wired up or no such header has ever
/// been captured for this provider.
/// </param>
/// <param name="WindowKind">
/// The <see cref="BudgetWindow"/> kind the caps above reset on: <c>"Monthly"</c>, <c>"Weekly"</c>, or
/// <c>"RollingHours"</c> (Phase 4, §5.10). <c>"Monthly"</c> when no budget store is wired up.
/// </param>
/// <param name="NextResetUtc">
/// The UTC instant the current period ends and spend resets to zero, computed live from
/// <paramref name="WindowKind"/> - backs the budget bar's "resets in 2h 10m" text.
/// </param>
public sealed record ProviderView(
    string Key,
    string? Name,
    string BaseUrl,
    string AuthHeaderName,
    IReadOnlyList<ModelView> Models,
    IReadOnlyList<HeaderView> Headers,
    bool IsFree = false,
    decimal? DollarCap = null,
    long? TokenCap = null,
    decimal DollarSpent = 0m,
    long TokensUsed = 0L,
    bool Enabled = true,
    ProviderEndpointCapabilities? EndpointCapabilities = null,
    string? ProviderType = null,
    DateTimeOffset? UsageLastRecordedAtUtc = null,
    ProviderRateLimitView? RateLimit = null,
    string WindowKind = "Monthly",
    DateTimeOffset? NextResetUtc = null);

/// <summary>
/// A provider's most recently captured rate-limit header snapshot, as returned to a management caller.
/// </summary>
/// <param name="Snapshot">The typed projection of the captured headers.</param>
/// <param name="ObservedAtUtc">
/// The UTC instant the most recent header in <see cref="Snapshot"/> was captured. Server-reported numbers
/// are trustworthy only as of this instant - the GUI's "As of" footer.
/// </param>
/// <param name="IsStale">
/// Whether <see cref="ObservedAtUtc"/> is older than the configured staleness threshold (§5.9) - the GUI
/// dims the "As of" footer and labels it stale when set, rather than presenting an old snapshot as current.
/// The snapshot itself is unaffected: a stale/absent capture never clears the last-known values.
/// </param>
/// <param name="Projections">
/// Per-dimension projected time-to-exhaustion (§5.9), keyed by the same dimension names as
/// <see cref="RateLimitSnapshotView.StandardDimensions"/>. A dimension is absent here - not present with a
/// null value - when no projection could be made (flat, refilled, reset-before-empty, or too little
/// history); the GUI shows nothing for that dimension rather than fabricating urgency.
/// </param>
public sealed record ProviderRateLimitView(
    RateLimitSnapshotView Snapshot,
    DateTimeOffset ObservedAtUtc,
    bool IsStale,
    IReadOnlyDictionary<string, RateLimitExhaustionProjection> Projections);

/// <summary>One dimension's history points for the Providers card's rate-limit trend chart (§5.9).</summary>
/// <param name="BucketUtc">The minute bucket's start instant, UTC.</param>
/// <param name="Remaining">What was left in this bucket, or <see langword="null"/> if the header was absent/unparsable that minute.</param>
/// <param name="Limit">The dimension's configured cap in this bucket, or <see langword="null"/> if unknown.</param>
public sealed record RateLimitHistoryPointView(DateTimeOffset BucketUtc, long? Remaining, long? Limit);

/// <summary>The <c>GET /admin/providers/{key}/rate-limit-history</c> response: per-dimension history series.</summary>
/// <param name="Dimensions">History points per standard-family dimension name, chronologically ordered.</param>
public sealed record RateLimitHistoryResponse(IReadOnlyDictionary<string, IReadOnlyList<RateLimitHistoryPointView>> Dimensions);

/// <summary>A single configured model as returned to a management caller.</summary>
/// <param name="ModelName">The client-facing model name.</param>
/// <param name="ProviderModelId">The upstream model identifier.</param>
/// <param name="Dialect">
/// The tool-call dialect detected for this model (<c>docs/router/tool-call-normalization.md</c> §3.2), or
/// <see langword="null"/> when it has never been classified - no capability store is wired up, or no scan
/// or live observation has run yet for this (provider, model) pair.
/// </param>
/// <param name="Confidence">
/// How <see cref="Dialect"/> was learned (e.g. <c>"Heuristic"</c>, <c>"Template"</c>, <c>"Observed"</c>,
/// <c>"Operator"</c>), or <see langword="null"/> alongside a null <see cref="Dialect"/>.
/// </param>
/// <param name="Enabled">
/// The operator's own Start/Stop state for this model (<see cref="ModelRouteEntry.Enabled"/>) - never
/// changed by a scan, only by an explicit toggle.
/// </param>
/// <param name="PresentUpstream">
/// Whether the most recent "Refresh from endpoint" scan reported this model
/// (<see cref="ModelRouteEntry.PresentUpstream"/>). <see langword="false"/> means the provider's endpoint
/// didn't list it last time, not that it was removed from configuration.
/// </param>
public sealed record ModelView(
    string ModelName,
    string ProviderModelId,
    string? Dialect = null,
    string? Confidence = null,
    bool Enabled = true,
    bool PresentUpstream = true);

/// <summary>
/// A single custom header as returned to a management caller. A header carries a mix of public
/// configuration and secrets, so readability is per-header: an unlocked literal is returned in
/// <see cref="Value"/> so the operator can read and edit it, while a locked one is write-only and only
/// its <see cref="Source"/> is reported. <see cref="ManagementFacade"/>'s projection is the single place
/// that decides this - see <c>docs/gui/secret-field.md</c>.
/// </summary>
/// <param name="Name">The header name.</param>
/// <param name="Source">One of <see cref="HeaderValueSource"/>: where this header's value comes from.</param>
/// <param name="ValueEnvVar">The environment variable name holding the value, when <paramref name="Source"/> is <see cref="HeaderValueSource.EnvVar"/>.</param>
/// <param name="Value">The literal value, but only for an unlocked literal header; null whenever
/// <paramref name="Locked"/> is set or the value comes from elsewhere.</param>
/// <param name="Locked">Whether this header's value is a secret withheld from management callers.</param>
public sealed record HeaderView(string Name, string Source, string? ValueEnvVar, string? Value = null, bool Locked = false);

/// <summary>The <c>GET /admin/providers</c> (and MCP <c>list_providers</c>) response envelope.</summary>
/// <param name="Providers">All configured providers, ordered by key.</param>
public sealed record ProvidersResponse(IReadOnlyList<ProviderView> Providers);

/// <summary>The body for adding or editing a provider. Fields fall back to the existing value when
/// omitted. Authentication is expressed purely via <paramref name="Headers"/>.</summary>
/// <param name="BaseUrl">The provider's absolute base URL.</param>
/// <param name="AuthHeaderName">The header carrying the credential.</param>
/// <param name="Headers">The full set of custom headers to store (replaces the existing set, one header at
/// a time via the blank-preserves-existing rule); null keeps the existing headers (legacy callers).</param>
/// <param name="IsFree">Whether this provider costs nothing; null keeps the existing value, so a partial
/// write can't silently un-free a provider.</param>
/// <param name="Enabled">Whether the provider is switched on; null keeps the existing value, so a partial
/// write can't silently restart a stopped provider.</param>
/// <param name="ProviderName">The user-friendly display name for this provider. Null preserves the existing value.
/// Any other value (including empty/whitespace) is normalized: empty/whitespace becomes null (an explicit clear);
/// non-empty becomes the trimmed string.</param>
/// <param name="ProviderType">The provider family selected in the editor (see
/// <see cref="ProviderOptions.ProviderType"/>). Normalized exactly like <paramref name="ProviderName"/>:
/// null keeps the existing value, so a partial write can't silently reset a provider's type; any other
/// value is trimmed, and empty/whitespace becomes null (an explicit clear).</param>
public sealed record ProviderWriteRequest(
    string? BaseUrl,
    string? AuthHeaderName,
    IReadOnlyList<HeaderWriteRequest>? Headers = null,
    bool? IsFree = null,
    bool? Enabled = null,
    string? ProviderName = null,
    string? ProviderType = null);

/// <summary>
/// The body sent to switch a provider on or off (<c>PUT /admin/providers/{key}/enabled</c>). Unlike
/// <see cref="ProviderWriteRequest.Enabled"/> this is non-nullable: the dedicated route exists to state the
/// new value outright rather than leaving it optional among a full provider edit.
/// </summary>
/// <param name="Enabled">The provider's new on/off state.</param>
public sealed record ProviderEnabledWriteRequest(bool Enabled);

/// <summary>A single custom header to store for a provider.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">A literal value; takes precedence over <paramref name="ValueEnvVar"/> when non-empty.</param>
/// <param name="ValueEnvVar">The name of an environment variable holding the value, used when <paramref name="Value"/> is blank.</param>
/// <param name="Locked">Whether the literal value is a secret to withhold from future reads. Applies to the
/// header whether or not <paramref name="Value"/> is resent, so a value can be locked without retyping it,
/// and it also decides what a blank write means: <see langword="true"/> preserves the stored value (the
/// caller was never shown it), while an explicit <see langword="false"/> clears it (the caller could see
/// the field and left it empty - this is how the editor's unlock clears a secret). Null is the legacy
/// shape, kept for callers that predate the flag: blank preserves, and a literal stores locked. Ignored
/// for an env-var-backed header, which always stores unlocked.</param>
public sealed record HeaderWriteRequest(string? Name, string? Value, string? ValueEnvVar, bool? Locked = null);

/// <summary>The body for adding or editing a model under a provider.</summary>
/// <param name="ProviderModelId">The upstream model identifier; defaults to the model name when blank.</param>
public sealed record ModelWriteRequest(string? ProviderModelId);

/// <summary>The body sent to switch a model on or off (<c>PUT /admin/providers/{key}/models/{modelName}/enabled</c>).</summary>
/// <param name="Enabled">The model's new on/off state.</param>
public sealed record ModelEnabledWriteRequest(bool Enabled);

/// <summary>
/// The body sent to pin how a model expresses tool calls
/// (<c>PUT /admin/providers/{key}/models/{modelName}/tool-dialect</c>).
/// </summary>
/// <param name="Dialect">
/// A <see cref="TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.ToolCallDialect.Name"/> to pin at
/// <see cref="TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.DetectionConfidence.Operator"/>, or
/// <see langword="null"/>/empty to clear the pin and hand the model back to automatic detection.
/// </param>
public sealed record ModelToolDialectWriteRequest(string? Dialect);

/// <summary>
/// The body for setting a provider's budget caps and reset window. A null cap clears that dimension; both
/// null removes the budget. Caps must be non-negative.
/// </summary>
/// <param name="DollarCap">The cap for the window, or null for no dollar budget.</param>
/// <param name="TokenCap">The cap for the window, or null for no token budget.</param>
/// <param name="WindowKind">
/// The window the caps reset on: <c>"Monthly"</c>, <c>"Weekly"</c>, or <c>"RollingHours"</c>. Null (the
/// default) keeps today's behavior - <see cref="ProviderBudgetStore.SetBudget"/> defaults to Monthly.
/// </param>
/// <param name="WindowHours">Required and must be positive when <paramref name="WindowKind"/> is <c>"RollingHours"</c>; otherwise ignored.</param>
public sealed record ProviderBudgetWriteRequest(decimal? DollarCap, long? TokenCap, string? WindowKind = null, int? WindowHours = null);

/// <summary>
/// The body for adding or replacing an operator price override (§5.7's <see cref="ResolutionRung.OperatorOverride"/> rung).
/// </summary>
/// <param name="SourceName">The aggregator source this override applies to (e.g. <c>LiteLLM</c>).</param>
/// <param name="AggregatorModelKey">The source's own model key this override matches, verbatim.</param>
/// <param name="ModelName">The client-facing <c>ModelRouting:ModelList[].ModelName</c> to resolve to; must already be configured.</param>
public sealed record PriceOverrideWriteRequest(string SourceName, string AggregatorModelKey, string ModelName);

/// <summary>
/// One configured model's current price-resolution state, as returned by
/// <see cref="ManagementFacade.GetPriceResolutionDiagnosis"/>. Backs the Governance price-overrides pane's
/// read-only diagnosis view.
/// </summary>
/// <param name="ModelName">The client-facing <c>ModelRouting:ModelList[].ModelName</c>.</param>
/// <param name="Provider">The configured provider serving this model.</param>
/// <param name="Resolved">Whether the catalog currently holds a fresh price for this (model, provider) cell.</param>
/// <param name="IsApproximate">
/// Whether the resolved price (when <paramref name="Resolved"/>) was matched via a resolution-ladder rung
/// below <c>Exact</c>/<c>OperatorOverride</c> - a disclosed estimate rather than an exact match.
/// </param>
public sealed record PriceResolutionDiagnosisView(string ModelName, string Provider, bool Resolved, bool IsApproximate);

/// <summary>The result of a model-discovery call.</summary>
/// <param name="Supported">Whether the provider answered an OpenAI-shaped model list.</param>
/// <param name="Models">The discovered model ids (empty when unsupported).</param>
/// <param name="Error">A human-readable reason when <paramref name="Supported"/> is false.</param>
public sealed record DiscoverModelsResponse(bool Supported, IReadOnlyList<string> Models, string? Error);

