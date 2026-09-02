using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
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

    // Per-model metadata probing's own budget (dialect and context window, learned from the same two
    // responses), applied across every model on a provider rather than per model, so a provider serving
    // thirty models cannot turn one scan into thirty sequential timeouts. Deliberately under
    // ExplicitCapabilityScanBudget: the endpoint flavors are what the operator actually asked for, and this
    // detection is the free extra that rides along on the metadata those flavors expose.
    private static readonly TimeSpan ModelProbeBudget = TimeSpan.FromSeconds(10);

    private readonly IProviderConfigStore _store;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly HttpClient _httpClient;
    private readonly ProviderBudgetStore? _budgetStore;
    private readonly ProviderEndpointScanner? _endpointScanner;
    private readonly ToolCallCapabilityStore? _capabilityStore;
    private readonly PriceCatalogRepository? _priceCatalogRepository;
    private readonly ModelAliasOverrideStore? _overrideStore;
    private readonly ISecretWriter? _secretWriter;
    private readonly ISecretReader? _secretReader;
    private readonly IProviderInteractionStatusStore? _interactionStatus;

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
    /// <param name="dependencies">
    /// The optional collaborators, carried as one named object rather than a dozen positional nullable
    /// arguments - see <see cref="ManagementFacadeDependencies"/>, whose members document what each one
    /// enables and what its absence makes unavailable. Defaults to <see langword="null"/>, which behaves
    /// identically to supplying an instance with every member unset: the facade still manages providers and
    /// models, and every surface needing an absent collaborator answers
    /// <see cref="ManagementErrorType.Unavailable"/>.
    /// </param>
    public ManagementFacade(
        IProviderConfigStore store,
        IEnvironmentVariableProvider environment,
        HttpClient httpClient,
        ManagementFacadeDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(httpClient);

        _store = store;
        _environment = environment;
        _httpClient = httpClient;
        _budgetStore = dependencies?.BudgetStore;
        _endpointScanner = dependencies?.EndpointScanner;
        _capabilityStore = dependencies?.CapabilityStore;
        _priceCatalogRepository = dependencies?.PriceCatalogRepository;
        _overrideStore = dependencies?.OverrideStore;
        _rateLimitStalenessThreshold = dependencies?.RateLimitStalenessThreshold ?? DefaultRateLimitStalenessThreshold;
        _secretWriter = dependencies?.SecretWriter;
        _secretReader = dependencies?.SecretReader;
        _interactionStatus = dependencies?.InteractionStatusStore;
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
        var provider = MergeProvider(key, request, existing);
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

        RecordScanOutcome(key, "Scan capabilities", capabilities);

        // Tier 1-3 dialect detection for every model on this provider, now that the flags saying which
        // native metadata APIs are reachable have just been refreshed. Best-effort and non-blocking on the
        // result: the operator asked which flavors the endpoint answers, and that question has been
        // answered above regardless of what detection manages to learn.
        await TryResolveModelMetadataAsync(key, provider, capabilities, ModelsFor(key), cancellationToken).ConfigureAwait(false);

        return ManagementResult<ProviderEndpointCapabilities>.Ok(capabilities);
    }

    /// <summary>
    /// Records a capability scan's outcome against <see cref="_interactionStatus"/>: a <see cref="ProviderEndpointCapabilities.ScanError"/>
    /// is always a failure; otherwise the scan is a success even when no flavor was detected (the endpoint
    /// answered - it just isn't any of the flavors this router recognizes).
    /// </summary>
    /// <param name="key">The provider key that was scanned.</param>
    /// <param name="operation">A short label for the interaction, used verbatim in the recorded status.</param>
    /// <param name="capabilities">The scan's result.</param>
    private void RecordScanOutcome(string key, string operation, ProviderEndpointCapabilities capabilities)
    {
        if (capabilities.ScanError is { } scanError)
        {
            _interactionStatus?.RecordFailure(key, operation, scanError);
        }
        else
        {
            _interactionStatus?.RecordSuccess(key, operation);
        }
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
            await TryResolveModelMetadataAsync(key, provider, capabilities, ModelsFor(key), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately swallowing everything, including cancellation. The provider has already been
            // persisted; surfacing a scan problem here would turn a successful save into a failed request
            // and leave the caller unsure whether their configuration was stored.
        }
    }

    /// <summary>
    /// Removes a provider by key, cascading to every model that routes to it and to every secret this
    /// provider ever wrote to the protected store (<c>docs/router/secrets-at-rest-plan.md</c> §5). Rejected
    /// (404-shaped) if unknown. Historical metrics are retained.
    /// </summary>
    public async Task<ManagementResult<ProvidersResponse>> RemoveProviderAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.Providers.ContainsKey(key))
        {
            return ManagementResult<ProvidersResponse>.Fail(ManagementErrorType.NotFound, $"Provider '{key}' not found.");
        }

        var result = await MutateAsync(() => _store.RemoveProviderAsync(key, cancellationToken)).ConfigureAwait(false);

        // Best-effort: the provider's configuration is already gone by this point, so a store failure here
        // (e.g. non-Windows) would only leave orphaned ciphertext behind, never resurrect the provider.
        if (result.Success)
        {
            TryDeleteProviderSecrets(key);
            _interactionStatus?.Remove(key);
        }

        return result;
    }

    /// <summary>Removes every protected-store entry named under <paramref name="providerKey"/>'s prefix, swallowing a store that is unavailable on this platform.</summary>
    private void TryDeleteProviderSecrets(string providerKey)
    {
        if (_secretWriter is null)
        {
            return;
        }

        try
        {
            _secretWriter.DeleteByPrefix(SecretRefPrefix(providerKey));
        }
        catch (PlatformNotSupportedException)
        {
            // The store never wrote anything on this platform in the first place - see ResolveHeader.
        }
    }

    /// <summary>The protected-store name prefix for every secret belonging to <paramref name="providerKey"/> (<c>docs/router/secrets-at-rest-plan.md</c> §3's naming convention).</summary>
    private static string SecretRefPrefix(string providerKey) => $"provider:{providerKey}:header:";

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
            await TryResolveModelMetadataAsync(
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
    /// Runs tier 1-3 metadata detection over <paramref name="models"/> and records whatever it learns -
    /// each model's tool-call dialect and its context window. Never throws and never affects the caller's
    /// outcome.
    /// </summary>
    /// <remarks>
    /// A model the resolver cannot classify is skipped rather than recorded as anything - the deliberate
    /// design from <c>tool-call-normalization.md</c> §3.2, since a missing row means "forward natively and
    /// classify from the first real response" while a wrong row arms the wrong scanner against every
    /// response the model produces. Dialect writes go through
    /// <see cref="ToolCallCapabilityStore.TryRecordModelCapability"/>, whose confidence gate is what keeps
    /// a re-scan from overwriting something a live observation or an operator already established.
    /// <para>
    /// Context windows are recorded through <see cref="ToolCallCapabilityStore.SetModelContextWindow"/>,
    /// which has no such gate - see that method for why a context length has no confidence ladder to rank.
    /// The two are recorded independently: a probe routinely learns one without the other.
    /// </para>
    /// </remarks>
    private async Task TryResolveModelMetadataAsync(
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
            budget.CancelAfter(ModelProbeBudget);

            foreach (var model in models)
            {
                // Checked per model rather than relying on the probes to fail fast, so a budget that has
                // already elapsed stops the loop instead of firing a doomed request for every remaining
                // model.
                if (budget.IsCancellationRequested)
                {
                    break;
                }

                var probe = await _dialectResolver.ResolveAsync(
                    providerKey,
                    provider,
                    endpointCapabilities,
                    model.ModelName,
                    model.ProviderModelId,
                    budget.Token).ConfigureAwait(false);

                // The caller aborting is not an observation about this model, and unlike the endpoint scan
                // there is no record here worth degrading - so stop rather than persist a partial pass.
                cancellationToken.ThrowIfCancellationRequested();

                if (probe.Capability is not null)
                {
                    _capabilityStore.TryRecordModelCapability(probe.Capability);
                }

                // Recorded independently of the dialect: the probe deliberately reports a window even on the
                // paths that classify nothing, so gating this on Capability would discard exactly the
                // readings tier 1 is best at producing.
                if (probe.ContextWindow is not null)
                {
                    _capabilityStore.SetModelContextWindow(probe.ContextWindow);
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

        return ManagementResultExecutor.TryExecute(() =>
        {
            _overrideStore.Upsert(request.SourceName, request.AggregatorModelKey, request.ModelName);
            return _overrideStore.GetAll();
        }, "Failed to save the price override.");
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

        // Supported: false with no Error is simply "this provider has no OpenAI-shaped /v1/models" (hosted
        // Anthropic, Bedrock) - expected and not a failure. Only a populated Error - a real transport/auth
        // problem - is worth flagging on the card.
        if (result.Error is { } error)
        {
            _interactionStatus?.RecordFailure(providerKey, "Discover models", error);
        }
        else
        {
            _interactionStatus?.RecordSuccess(providerKey, "Discover models");
        }

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

        ProviderEndpointCapabilities? capabilities = null;
        if (_endpointScanner is not null && _capabilityStore is not null)
        {
            capabilities = await ScanAndPersistCapabilitiesAsync(
                key, provider, ExplicitCapabilityScanBudget, cancellationToken).ConfigureAwait(false);
            await TryResolveModelMetadataAsync(key, provider, capabilities, ModelsFor(key), cancellationToken).ConfigureAwait(false);
        }

        RecordRefreshOutcome(key, discovery, capabilities);

        return ManagementResult<ProvidersResponse>.Ok(BuildProvidersResponse());
    }

    /// <summary>
    /// Records "Refresh from endpoint"'s combined outcome against <see cref="_interactionStatus"/>. Discovery
    /// reporting <see cref="DiscoverModelsResponse.Error"/> is not automatically a failure - a provider with
    /// no OpenAI-shaped <c>/v1/models</c> (hosted Anthropic, Bedrock) always reports one, and that is
    /// expected. It is only treated as a failure when the capability scan does not corroborate that the
    /// endpoint is reachable and authenticating: either the scan itself errored, or it completed but
    /// recognized none of the API flavors it probes for, or no scan ran at all to corroborate the discovery
    /// failure one way or the other. This is what turns an invalid/expired API key - the motivating case -
    /// into a visible warning, while a healthy Anthropic-only provider stays silent.
    /// </summary>
    /// <param name="key">The provider key that was refreshed.</param>
    /// <param name="discovery">The model-discovery result.</param>
    /// <param name="capabilities">The capability scan's result, or <see langword="null"/> when no scan ran (no scanner/capability store wired up).</param>
    private void RecordRefreshOutcome(string key, DiscoverModelsResponse discovery, ProviderEndpointCapabilities? capabilities)
    {
        if (discovery.Error is null)
        {
            _interactionStatus?.RecordSuccess(key, "Refresh from endpoint");
            return;
        }

        var flavorDetected = capabilities is { ScanError: null } detected &&
            (detected.OpenAiCompatible || detected.AnthropicCompatible || detected.LmStudioNative || detected.OllamaNative);
        if (flavorDetected)
        {
            _interactionStatus?.RecordSuccess(key, "Refresh from endpoint");
            return;
        }

        _interactionStatus?.RecordFailure(key, "Refresh from endpoint", discovery.Error);
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
                        // here rather than at any caller - see docs/gui/secret-field.md. A protected-store
                        // header never carries a value to drop (h.Value is always null once migrated/written
                        // there), but still reports Locked so the GUI's "saved, blank keeps it" placeholder
                        // keeps working identically to a locked literal.
                        var locked = (source == HeaderValueSource.Literal || source == HeaderValueSource.Protected) && h.Locked;
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
                    NextResetUtc: budget.NextResetUtc,
                    HasStoredAdminKey: _secretReader?.TryRead(AdminKeySecretName(kvp.Key), out _) ?? false,
                    ReportedUsage: BuildReportedUsageView(kvp.Key),
                    AdminAction: _interactionStatus?.Get(kvp.Key),
                    LiveTraffic: _interactionStatus?.GetLiveTraffic(kvp.Key));
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
    /// Builds a provider's reported-usage view from the price catalog repository
    /// (docs/router/secrets-at-rest-plan.md §8.1), or <see langword="null"/> when no repository is wired up
    /// or nothing has been fetched for this provider yet (the common case - only <c>anthropic</c> with a
    /// stored/configured Admin API key ever has rows).
    /// </summary>
    private ProviderReportedUsageView? BuildReportedUsageView(string providerKey)
    {
        if (_priceCatalogRepository is null)
        {
            return null;
        }

        var (rows, fetchedAtUtc) = _priceCatalogRepository.GetReportedUsage(providerKey);
        if (rows.Count == 0 || fetchedAtUtc is not { } fetchedAt)
        {
            return null;
        }

        var rowViews = rows
            .Select(r => new ReportedUsageRowView(r.UsageDay, r.Model, r.InputTokens, r.OutputTokens, r.CacheCreationTokens, r.CacheReadTokens))
            .ToList();
        return new ProviderReportedUsageView(rowViews, fetchedAt);
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
            : !string.IsNullOrWhiteSpace(header.ValueSecretRef) ? HeaderValueSource.Protected
            : !string.IsNullOrWhiteSpace(header.ValueEnvVar) ? HeaderValueSource.EnvVar
            : HeaderValueSource.None;

    /// <summary>
    /// Builds the <see cref="ProviderOptions"/> to store from an incoming write request, merged over any
    /// existing provider. Fields fall back to the existing value when omitted, then to sensible defaults;
    /// each custom header is resolved by <see cref="ResolveHeader"/>.
    /// </summary>
    /// <param name="providerKey">The provider key being upserted, used to name any secret <see cref="ResolveHeader"/> writes.</param>
    /// <param name="request">The incoming write request.</param>
    /// <param name="existing">The provider's current configuration, or <see langword="null"/> when adding a new one.</param>
    private ProviderOptions MergeProvider(string providerKey, ProviderWriteRequest request, ProviderOptions? existing)
    {
        // A `with` over the existing provider (or a default one when adding), rather than a hand-listed
        // rebuild. Only the fields this request can actually change are named below; everything else -
        // the four Aws* fields, and anything added to ProviderOptions later - carries across by
        // construction. The previous hand-written list had silently fallen behind the type and was
        // resetting exactly those fields on every edit (docs/router/backlog.md item 1).
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
                : ResolveHeaders(providerKey, request.Headers, baseline.Headers),
            IsFree = request.IsFree ?? baseline.IsFree,
            Enabled = request.Enabled ?? baseline.Enabled,
        };
    }

    /// <summary>
    /// Resolves every incoming header write via <see cref="ResolveHeader"/>, then cleans up the protected
    /// store for any header that existed under <paramref name="existingHeaders"/> with a
    /// <see cref="ProviderHeader.ValueSecretRef"/> but is entirely absent from the resolved result - the
    /// case <see cref="ResolveHeader"/> itself cannot see, since it only runs once per header still present
    /// in the write request.
    /// </summary>
    private List<ProviderHeader> ResolveHeaders(
        string providerKey, IReadOnlyList<HeaderWriteRequest> requests, IReadOnlyList<ProviderHeader> existingHeaders)
    {
        var resolved = requests
            .Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .Select(h => ResolveHeader(providerKey, h, existingHeaders))
            .ToList();

        var resolvedNames = new HashSet<string>(resolved.Select(h => h.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var existing in existingHeaders)
        {
            if (!string.IsNullOrWhiteSpace(existing.ValueSecretRef) && !resolvedNames.Contains(existing.Name))
            {
                DeleteExistingSecret(providerKey, existing, existing.Name);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Copies a provider with a different <see cref="ProviderOptions.Enabled"/>.
    /// </summary>
    /// <remarks>
    /// This used to copy every property by hand, to avoid <see cref="MergeProvider"/>'s field loss - and had
    /// itself fallen a field behind, silently clearing a since-removed provider flag on every Stop/Play
    /// toggle. Now that <see cref="ProviderOptions"/> is a record, <c>with</c> carries everything across by
    /// construction and the hazard is gone from both methods at once.
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
    /// <see cref="HeaderWriteRequest.Value"/> is a literal - written straight to the protected secret store
    /// and referenced via <see cref="ProviderHeader.ValueSecretRef"/> when it is locked and the store is
    /// available (<c>docs/router/secrets-at-rest-plan.md</c> §5), or kept as a plain literal otherwise - a
    /// non-blank <see cref="HeaderWriteRequest.ValueEnvVar"/> is an env-var reference, and - since a locked
    /// header's value is never returned for a caller to resend - both blank preserves whatever is already
    /// stored under this header's name (literal, protected-store reference, or env var alike), and otherwise
    /// stores as <see cref="HeaderValueSource.None"/>.
    /// <para>
    /// <see cref="HeaderWriteRequest.Locked"/> travels with the header independently of its value, so an
    /// operator can lock an already-stored secret without retyping it - and it is also what makes the
    /// blank rule safe to relax: an <em>explicitly unlocked</em> blank write clears the stored value
    /// (including deleting any protected-store entry), because the caller was shown that value in full and
    /// chose to empty the field. That is how the editor's unlock destroys a secret. Null (the legacy shape)
    /// keeps the old preserve-on-blank behavior in every case.
    /// </para>
    /// </summary>
    /// <param name="providerKey">The provider key being upserted, used to name this header's protected-store entry.</param>
    /// <param name="request">The incoming header write.</param>
    /// <param name="existingHeaders">The provider's current headers, consulted for the preserve-on-blank and secret-cleanup rules.</param>
    private ProviderHeader ResolveHeader(string providerKey, HeaderWriteRequest request, IReadOnlyList<ProviderHeader> existingHeaders)
    {
        var name = request.Name!.Trim();

        // A caller that predates the flag stored every literal write-only, so its headers keep meaning
        // "locked" rather than silently becoming readable.
        var locked = request.Locked ?? true;

        // HTTP header names are case-insensitive, so "X-Foo" and "x-foo" must be treated as the same
        // header when looking up the value to preserve or clean up - otherwise a casing mismatch between
        // what was stored and what the caller resends silently drops the stored secret instead of keeping
        // it, or leaves its protected-store entry orphaned.
        var existing = existingHeaders.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(request.Value))
        {
            if (locked && TryWriteSecret(providerKey, name, request.Value))
            {
                return new ProviderHeader { Name = name, Value = null, ValueEnvVar = null, ValueSecretRef = SecretRefName(providerKey, name), Locked = true };
            }

            // Either unlocked (public configuration, stored verbatim) or the store is unavailable on this
            // platform - either way this header no longer references the store, so drop any stale entry.
            DeleteExistingSecret(providerKey, existing, name);
            return new ProviderHeader { Name = name, Value = request.Value, ValueEnvVar = null, Locked = locked };
        }

        // An env-var-backed header keeps its secret in the environment rather than in configuration, so
        // there is nothing to withhold and it always stores unlocked.
        if (!string.IsNullOrWhiteSpace(request.ValueEnvVar))
        {
            DeleteExistingSecret(providerKey, existing, name);
            return new ProviderHeader { Name = name, Value = null, ValueEnvVar = request.ValueEnvVar.Trim(), Locked = false };
        }

        if (request.Locked is false)
        {
            DeleteExistingSecret(providerKey, existing, name);
            return new ProviderHeader { Name = name, Value = null, ValueEnvVar = null, Locked = false };
        }

        var preservedValue = existing?.Value;
        var preservedSecretRef = existing?.ValueSecretRef;

        return new ProviderHeader
        {
            Name = name,
            Value = preservedValue,
            ValueEnvVar = existing?.ValueEnvVar,
            ValueSecretRef = preservedSecretRef,
            // Only a literal or a protected-store reference can be a secret, so a preserved env-var (or
            // valueless) header stores unlocked no matter what the caller asked for.
            Locked = (!string.IsNullOrWhiteSpace(preservedValue) || !string.IsNullOrWhiteSpace(preservedSecretRef)) && locked
        };
    }

    /// <summary>The protected-store name for one provider header (<c>docs/router/secrets-at-rest-plan.md</c> §3's naming convention).</summary>
    private static string SecretRefName(string providerKey, string headerName) => $"provider:{providerKey}:header:{headerName}";

    /// <summary>The protected-store name for a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md §3's naming convention), matching <c>Hosting.ServiceCollectionExtensions.AdminApiKeySecretName</c>.</summary>
    private static string AdminKeySecretName(string provider) => $"reconciliation:{provider}:admin-key";

    // Only these two are recognized by BuildCostReconcilers (docs/router/agent-cost-tracking.md §3.5), so
    // this is the complete set of names SetSecret/DeleteSecret may ever touch.
    private static readonly string[] RecognizedReconciliationProviders = ["openai", "anthropic"];

    /// <summary>
    /// Stores <paramref name="value"/> as a provider's reconciliation Admin API key
    /// (docs/router/secrets-at-rest-plan.md §7), taking effect on the next reconciliation cycle with no
    /// restart required. The public route is named by secret (<c>PUT /admin/secrets/{name}</c>) rather than
    /// by provider so it matches the plan's write-only-secrets shape, but only the fixed
    /// <c>reconciliation:{openai|anthropic}:admin-key</c> names are accepted - this is not a generic secret
    /// store write endpoint.
    /// </summary>
    public ManagementResult<object?> SetSecret(string name, string value)
    {
        if (_secretWriter is null)
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.Unavailable, "The protected secret store is unavailable on this platform.");
        }

        if (!TryParseAdminKeySecretName(name, out var provider))
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.InvalidRequest, $"Unsupported secret name '{name}'.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.InvalidRequest, "value must not be blank.");
        }

        try
        {
            _secretWriter.Write(AdminKeySecretName(provider), value);
            return ManagementResult<object?>.Ok(null);
        }
        catch (PlatformNotSupportedException)
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.Unavailable, "The protected secret store is unavailable on this platform.");
        }
    }

    /// <summary>Clears a stored secret by name - the only counterpart to <see cref="SetSecret"/>, same name restriction. There is deliberately no read counterpart (docs/router/secrets-at-rest-plan.md §4).</summary>
    public ManagementResult<object?> DeleteSecret(string name)
    {
        if (_secretWriter is null)
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.Unavailable, "The protected secret store is unavailable on this platform.");
        }

        if (!TryParseAdminKeySecretName(name, out var provider))
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.InvalidRequest, $"Unsupported secret name '{name}'.");
        }

        _secretWriter.Delete(AdminKeySecretName(provider));
        return ManagementResult<object?>.Ok(null);
    }

    /// <summary>Parses a secret name as <c>reconciliation:{provider}:admin-key</c> for a recognized provider, the only shape <see cref="SetSecret"/>/<see cref="DeleteSecret"/> accept.</summary>
    private static bool TryParseAdminKeySecretName(string name, out string provider)
    {
        provider = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var parts = name.Split(':');
        if (parts.Length != 3 || parts[0] != "reconciliation" || parts[2] != "admin-key")
        {
            return false;
        }

        foreach (var candidate in RecognizedReconciliationProviders)
        {
            if (string.Equals(candidate, parts[1], StringComparison.OrdinalIgnoreCase))
            {
                provider = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes <paramref name="value"/> to the protected store under this header's name. Returns <see langword="false"/> when no store is configured or it is unavailable on this platform.</summary>
    private bool TryWriteSecret(string providerKey, string headerName, string value)
    {
        if (_secretWriter is null)
        {
            return false;
        }

        try
        {
            _secretWriter.Write(SecretRefName(providerKey, headerName), value);
            return true;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Deletes <paramref name="existing"/>'s protected-store entry, if it has one, swallowing a store that is unavailable on this platform.</summary>
    private void DeleteExistingSecret(string providerKey, ProviderHeader? existing, string headerName)
    {
        if (_secretWriter is null || string.IsNullOrWhiteSpace(existing?.ValueSecretRef))
        {
            return;
        }

        try
        {
            _secretWriter.Delete(SecretRefName(providerKey, headerName));
        }
        catch (PlatformNotSupportedException)
        {
            // Nothing was ever written to the store on this platform in the first place.
        }
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
        ProviderCredentialResolver.ApplyToRequest(requestMessage, provider, _environment, _secretReader);

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
