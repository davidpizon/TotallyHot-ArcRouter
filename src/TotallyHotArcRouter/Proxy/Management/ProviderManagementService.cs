using Microsoft.Extensions.Options;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The provider CRUD and capability-scanning collaborator split out of <see cref="ManagementFacade"/> per
/// <see href="../../../../docs/adr/0006-split-managementfacade-along-crud-aggregate-boundaries.md"/>: adding,
/// editing, enabling, and removing providers and models; discovering a provider's live model list and
/// reconciling it; and probing/persisting which API flavors an endpoint answers together with tier 1-3
/// tool-call-dialect detection. Reachable only through <see cref="ManagementFacade"/>'s public methods - it
/// is constructed directly by the facade and is not registered in DI as an independently reachable service,
/// so <see cref="ManagementFacade"/>'s public method set remains the single security boundary the ADR
/// describes.
/// </summary>
internal sealed class ProviderManagementService
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
    private readonly Func<ProvidersResponse> _buildProvidersResponse;
    private readonly ToolCallCapabilityStore? _capabilityStore;
    private readonly ModelDialectResolver _dialectResolver;
    private readonly ProviderEndpointScanner? _endpointScanner;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly HttpClient _httpClient;
    private readonly IProviderInteractionStatusStore? _interactionStatus;
    private readonly ISecretReader? _secretReader;
    private readonly ISecretWriter? _secretWriter;

    private readonly IProviderConfigStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderManagementService"/> class.
    /// </summary>
    /// <param name="store">The writable provider/model configuration store.</param>
    /// <param name="environment">Accessor used to resolve provider credentials for model discovery.</param>
    /// <param name="httpClient">HTTP client used to query a provider's live model list.</param>
    /// <param name="dependencies">The same optional collaborators bag <see cref="ManagementFacade"/> was constructed with.</param>
    /// <param name="buildProvidersResponse">
    /// Builds the masked, client-facing <see cref="ProvidersResponse"/> from the current store snapshot.
    /// Owned by <see cref="ManagementFacade"/> rather than this service, since it projects fields spanning
    /// every CRUD cluster (budget status, price overrides, secrets, rate limits) - not just provider/model
    /// state - so this service calls back into it after every mutation instead of duplicating it.
    /// </param>
    public ProviderManagementService(
        IProviderConfigStore store,
        IEnvironmentVariableProvider environment,
        HttpClient httpClient,
        ManagementFacadeDependencies? dependencies,
        Func<ProvidersResponse> buildProvidersResponse)
    {
        _store = store;
        _environment = environment;
        _httpClient = httpClient;
        _endpointScanner = dependencies?.EndpointScanner;
        _capabilityStore = dependencies?.CapabilityStore;
        _secretWriter = dependencies?.SecretWriter;
        _secretReader = dependencies?.SecretReader;
        _interactionStatus = dependencies?.InteractionStatusStore;
        _dialectResolver = new ModelDialectResolver(httpClient: httpClient, environment: environment);
        _buildProvidersResponse = buildProvidersResponse;
    }

    /// <summary>Adds or replaces a provider by key, merging over any existing provider (see <see cref="MergeProvider"/>).</summary>
    public async Task<ManagementResult<ProvidersResponse>> UpsertProviderAsync(
        string key, ProviderWriteRequest request, CancellationToken cancellationToken = default)
    {
        var current = _store.Snapshot.Options;
        current.Providers.TryGetValue(key: key, value: out var existing);
        var provider = MergeProvider(providerKey: key, request: request, existing: existing);
        var result = await MutateAsync(() =>
                _store.UpsertProviderAsync(key: key, provider: provider, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        // Refresh the endpoint-capability record for the provider that was just saved, so tier-1 dialect
        // detection has something to read without the operator having to trigger a scan by hand. Strictly
        // best-effort: it runs only after the save has already succeeded, is bounded by
        // CapabilityScanBudget, and swallows everything - a provider that is merely unreachable right now
        // must still be configurable.
        if (result.Success)
            await TryScanCapabilitiesAsync(key: key, provider: provider, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

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
            return ManagementResult<ProviderEndpointCapabilities>.Fail(
                errorType: ManagementErrorType.Unavailable, message: "Endpoint capability scanning is not available.");

        if (!_store.Snapshot.Options.Providers.TryGetValue(key: key, value: out var provider))
            return ManagementResult<ProviderEndpointCapabilities>.Fail(
                errorType: ManagementErrorType.NotFound, message: $"Provider '{key}' not found.");

        var capabilities = await ScanAndPersistCapabilitiesAsync(
            key: key, provider: provider, budgetDuration: ExplicitCapabilityScanBudget,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        RecordScanOutcome(key: key, operation: "Scan capabilities", capabilities: capabilities);

        // Tier 1-3 dialect detection for every model on this provider, now that the flags saying which
        // native metadata APIs are reachable have just been refreshed. Best-effort and non-blocking on the
        // result: the operator asked which flavors the endpoint answers, and that question has been
        // answered above regardless of what detection manages to learn.
        await TryResolveModelMetadataAsync(providerKey: key, provider: provider, endpointCapabilities: capabilities,
            models: ModelsFor(key), cancellationToken: cancellationToken).ConfigureAwait(false);

        return ManagementResult<ProviderEndpointCapabilities>.Ok(capabilities);
    }

    /// <summary>
    /// Records a capability scan's outcome against <see cref="_interactionStatus"/>: a
    /// <see cref="ProviderEndpointCapabilities.ScanError"/>
    /// is always a failure; otherwise the scan is a success even when no flavor was detected (the endpoint
    /// answered - it just isn't any of the flavors this router recognizes).
    /// </summary>
    /// <param name="key">The provider key that was scanned.</param>
    /// <param name="operation">A short label for the interaction, used verbatim in the recorded status.</param>
    /// <param name="capabilities">The scan's result.</param>
    private void RecordScanOutcome(string key, string operation, ProviderEndpointCapabilities capabilities)
    {
        if (capabilities.ScanError is { } scanError)
            _interactionStatus?.RecordFailure(providerKey: key, operation: operation, message: scanError);
        else
            _interactionStatus?.RecordSuccess(providerKey: key, operation: operation);
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
            return ManagementResult<ProvidersResponse>.Fail(
                errorType: ManagementErrorType.Unavailable,
                message: "Tool-call capability overrides are not available.");

        if (!_store.Snapshot.Options.Providers.ContainsKey(key))
            return ManagementResult<ProvidersResponse>.Fail(
                errorType: ManagementErrorType.NotFound, message: $"Provider '{key}' not found.");

        if (!ModelsFor(key).Any(m =>
                string.Equals(a: m.ModelName, b: modelName, comparisonType: StringComparison.OrdinalIgnoreCase)))
            return ManagementResult<ProvidersResponse>.Fail(
                errorType: ManagementErrorType.NotFound,
                message: $"Model '{modelName}' not found on provider '{key}'.");

        if (string.IsNullOrWhiteSpace(request.Dialect))
        {
            _capabilityStore.ClearModelCapability(providerKey: key, modelName: modelName);
            return ManagementResult<ProvidersResponse>.Ok(_buildProvidersResponse());
        }

        // Rejected rather than stored, unlike a row read back from the database. A name this build does not
        // know degrades to "not scanned" when found on disk, which is the right call for a row a *newer*
        // build wrote - but accepting one here would let a typo silently disable tool calling for the model
        // while the UI showed the pin as applied.
        if (!ToolCallDialectRegistry.TryGet(name: request.Dialect, dialect: out _))
            return ManagementResult<ProvidersResponse>.Fail(
                errorType: ManagementErrorType.InvalidRequest,
                message: $"Unknown tool-call dialect '{request.Dialect}'.");

        _capabilityStore.TryRecordModelCapability(new ModelToolCapability(
            ProviderKey: key,
            ModelName: modelName,
            Dialect: request.Dialect,
            Confidence: DetectionConfidence.Operator,
            Evidence: "Set by an operator."));

        return ManagementResult<ProvidersResponse>.Ok(_buildProvidersResponse());
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

        var capabilities = await _endpointScanner!
            .ScanAsync(providerKey: key, provider: provider, cancellationToken: budget.Token).ConfigureAwait(false);

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
        if (_endpointScanner is null || _capabilityStore is null) return;

        try
        {
            var capabilities = await ScanAndPersistCapabilitiesAsync(
                key: key, provider: provider, budgetDuration: CapabilityScanBudget,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Same best-effort spirit, and inside the same try: a provider save must not start depending on
            // a metadata probe succeeding.
            await TryResolveModelMetadataAsync(providerKey: key, provider: provider, endpointCapabilities: capabilities,
                    models: ModelsFor(key), cancellationToken: cancellationToken)
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
    public async Task<ManagementResult<ProvidersResponse>> RemoveProviderAsync(string key,
        CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.Providers.ContainsKey(key))
            return ManagementResult<ProvidersResponse>.Fail(errorType: ManagementErrorType.NotFound,
                message: $"Provider '{key}' not found.");

        var result = await MutateAsync(() => _store.RemoveProviderAsync(key: key, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        // Best-effort: the provider's configuration is already gone by this point, so a store failure here
        // (e.g. non-Windows) would only leave orphaned ciphertext behind, never resurrect the provider.
        if (result.Success)
        {
            TryDeleteProviderSecrets(key);
            _interactionStatus?.Remove(key);
        }

        return result;
    }

    /// <summary>
    /// Removes every protected-store entry named under <paramref name="providerKey"/>'s prefix, swallowing a store
    /// that is unavailable on this platform.
    /// </summary>
    private void TryDeleteProviderSecrets(string providerKey)
    {
        if (_secretWriter is null) return;

        try
        {
            _secretWriter.DeleteByPrefix(SecretRefPrefix(providerKey));
        }
        catch (PlatformNotSupportedException)
        {
            // The store never wrote anything on this platform in the first place - see ResolveHeader.
        }
    }

    /// <summary>
    /// The protected-store name prefix for every secret belonging to <paramref name="providerKey"/> (
    /// <c>docs/router/secrets-at-rest-plan.md</c> §3's naming convention).
    /// </summary>
    private static string SecretRefPrefix(string providerKey)
    {
        return $"provider:{providerKey}:header:";
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
            .FirstOrDefault(m =>
                string.Equals(a: m.ModelName, b: modelName, comparisonType: StringComparison.OrdinalIgnoreCase));

        var entry = new ModelRouteEntry
        {
            ModelName = modelName,
            Provider = providerKey,
            ProviderModelId = string.IsNullOrWhiteSpace(request.ProviderModelId) ? modelName : request.ProviderModelId,
            Enabled = existing?.Enabled ?? true,
            PresentUpstream = existing?.PresentUpstream ?? true
        };

        var result =
            await MutateAsync(() => _store.UpsertModelAsync(entry: entry, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

        // Classify the model that was just added, so the request path has a dialect before the model's first
        // real request rather than having to learn one from it. Uses the provider's already-scanned endpoint
        // flags - no scan is triggered here, since adding a model says nothing new about the provider.
        if (result.Success && _store.Snapshot.Options.Providers.TryGetValue(key: providerKey, value: out var provider))
            await TryResolveModelMetadataAsync(
                providerKey: providerKey,
                provider: provider,
                endpointCapabilities: _capabilityStore?.GetProviderCapabilities(providerKey),
                models: [entry],
                cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>Every model route that forwards to <paramref name="providerKey"/>.</summary>
    private IReadOnlyList<ModelRouteEntry> ModelsFor(string providerKey)
    {
        return [.. _store.Snapshot.Options.ModelList
            .Where(model => string.Equals(a: model.Provider, b: providerKey,
                comparisonType: StringComparison.OrdinalIgnoreCase))];
    }

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
        if (_capabilityStore is null || models.Count == 0) return;

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ModelProbeBudget);

            foreach (var model in models)
            {
                // Checked per model rather than relying on the probes to fail fast, so a budget that has
                // already elapsed stops the loop instead of firing a doomed request for every remaining
                // model.
                if (budget.IsCancellationRequested) break;

                var probe = await _dialectResolver.ResolveAsync(
                    providerKey: providerKey,
                    provider: provider,
                    endpointCapabilities: endpointCapabilities,
                    modelName: model.ModelName,
                    providerModelId: model.ProviderModelId,
                    cancellationToken: budget.Token).ConfigureAwait(false);

                // The caller aborting is not an observation about this model, and unlike the endpoint scan
                // there is no record here worth degrading - so stop rather than persist a partial pass.
                cancellationToken.ThrowIfCancellationRequested();

                if (probe.Capability is not null) _capabilityStore.TryRecordModelCapability(probe.Capability);

                // Recorded independently of the dialect: the probe deliberately reports a window even on the
                // paths that classify nothing, so gating this on Capability would discard exactly the
                // readings tier 1 is best at producing.
                if (probe.ContextWindow is not null) _capabilityStore.SetModelContextWindow(probe.ContextWindow);
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
    public async Task<ManagementResult<ProvidersResponse>> RemoveModelAsync(string modelName,
        CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.ModelList.Any(m =>
                string.Equals(a: m.ModelName, b: modelName, comparisonType: StringComparison.OrdinalIgnoreCase)))
            return ManagementResult<ProvidersResponse>.Fail(errorType: ManagementErrorType.NotFound,
                message: $"Model '{modelName}' not found.");

        return await MutateAsync(() =>
            _store.RemoveModelAsync(modelName: modelName, cancellationToken: cancellationToken)).ConfigureAwait(false);
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
            .FirstOrDefault(m =>
                string.Equals(a: m.ModelName, b: modelName, comparisonType: StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            return ManagementResult<ProvidersResponse>.Fail(errorType: ManagementErrorType.NotFound,
                message: $"Model '{modelName}' not found.");

        var entry = new ModelRouteEntry
        {
            ModelName = existing.ModelName,
            Provider = existing.Provider,
            ProviderModelId = existing.ProviderModelId,
            Enabled = request.Enabled,
            PresentUpstream = existing.PresentUpstream
        };

        return await MutateAsync(() => _store.UpsertModelAsync(entry: entry, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
        if (!_store.Snapshot.Options.Providers.TryGetValue(key: key, value: out var existing))
            return ManagementResult<ProvidersResponse>.Fail(errorType: ManagementErrorType.NotFound,
                message: $"Provider '{key}' not found.");

        var provider = WithEnabled(source: existing, enabled: request.Enabled);
        return await MutateAsync(() =>
                _store.UpsertProviderAsync(key: key, provider: provider, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Queries a provider's own OpenAI-shaped model list (best-effort; reports <c>Supported: false</c> when
    /// unavailable).
    /// </summary>
    public async Task<ManagementResult<DiscoverModelsResponse>> DiscoverModelsAsync(string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (!_store.Snapshot.Options.Providers.TryGetValue(key: providerKey, value: out var provider))
            return ManagementResult<DiscoverModelsResponse>.Fail(errorType: ManagementErrorType.NotFound,
                message: $"Provider '{providerKey}' not found.");

        var result = await DiscoverModelsCoreAsync(provider: provider, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Supported: false with no Error is simply "this provider has no OpenAI-shaped /v1/models" (hosted
        // Anthropic, Bedrock) - expected and not a failure. Only a populated Error - a real transport/auth
        // problem - is worth flagging on the card.
        if (result.Error is { } error)
            _interactionStatus?.RecordFailure(providerKey: providerKey, operation: "Discover models", message: error);
        else
            _interactionStatus?.RecordSuccess(providerKey: providerKey, operation: "Discover models");

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
        if (!_store.Snapshot.Options.Providers.TryGetValue(key: key, value: out var provider))
            return ManagementResult<ProvidersResponse>.Fail(errorType: ManagementErrorType.NotFound,
                message: $"Provider '{key}' not found.");

        var discovery = await DiscoverModelsCoreAsync(provider: provider, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (discovery.Supported)
            await ReconcileModelsAsync(providerKey: key, discoveredIds: discovery.Models,
                cancellationToken: cancellationToken).ConfigureAwait(false);

        ProviderEndpointCapabilities? capabilities = null;
        if (_endpointScanner is not null && _capabilityStore is not null)
        {
            capabilities = await ScanAndPersistCapabilitiesAsync(
                key: key, provider: provider, budgetDuration: ExplicitCapabilityScanBudget,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await TryResolveModelMetadataAsync(providerKey: key, provider: provider, endpointCapabilities: capabilities,
                models: ModelsFor(key), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        RecordRefreshOutcome(key: key, discovery: discovery, capabilities: capabilities);

        return ManagementResult<ProvidersResponse>.Ok(_buildProvidersResponse());
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
    /// <param name="capabilities">
    /// The capability scan's result, or <see langword="null"/> when no scan ran (no
    /// scanner/capability store wired up).
    /// </param>
    private void RecordRefreshOutcome(string key, DiscoverModelsResponse discovery,
        ProviderEndpointCapabilities? capabilities)
    {
        if (discovery.Error is null)
        {
            _interactionStatus?.RecordSuccess(providerKey: key, operation: "Refresh from endpoint");
            return;
        }

        var flavorDetected = capabilities is { ScanError: null } detected &&
                             (detected.OpenAiCompatible || detected.AnthropicCompatible || detected.LmStudioNative ||
                              detected.OllamaNative);
        if (flavorDetected)
        {
            _interactionStatus?.RecordSuccess(providerKey: key, operation: "Refresh from endpoint");
            return;
        }

        _interactionStatus?.RecordFailure(providerKey: key, operation: "Refresh from endpoint",
            message: discovery.Error);
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
        var discovered = new HashSet<string>(collection: discoveredIds, comparer: StringComparer.Ordinal);
        var configured = ModelsFor(providerKey);

        foreach (var entry in configured)
        {
            var isPresent = discovered.Contains(entry.ProviderModelId);
            if (entry.PresentUpstream == isPresent) continue;

            await _store.UpsertModelAsync(
                entry: new ModelRouteEntry
                {
                    ModelName = entry.ModelName,
                    Provider = entry.Provider,
                    ProviderModelId = entry.ProviderModelId,
                    Enabled = entry.Enabled,
                    PresentUpstream = isPresent
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var configuredIds = new HashSet<string>(collection: configured.Select(e => e.ProviderModelId),
            comparer: StringComparer.Ordinal);
        foreach (var id in discoveredIds)
        {
            if (configuredIds.Contains(id)) continue;

            // Auto-added, but never auto-started: the router notices the model exists, the operator decides
            // whether it should receive traffic. Same "id becomes both the client-facing name and provider
            // id" convention the old manual "click a discovered id to add it" affordance used.
            await _store.UpsertModelAsync(
                entry: new ModelRouteEntry
                {
                    ModelName = id,
                    Provider = providerKey,
                    ProviderModelId = id,
                    Enabled = false,
                    PresentUpstream = true
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

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
            ProviderType = request.ProviderType is null
                ? baseline.ProviderType
                : NormalizeNameField(request.ProviderType),
            AuthHeaderName = request.AuthHeaderName ?? baseline.AuthHeaderName,
            // The caller always sends the full header set (a null list means "keep existing", e.g. a
            // legacy/partial caller); a provided list replaces it wholesale, one header at a time through
            // ResolveHeader so a blank value preserves what's already stored under that name.
            Headers = request.Headers is null
                ? baseline.Headers
                : ResolveHeaders(providerKey: providerKey, requests: request.Headers,
                    existingHeaders: baseline.Headers),
            IsFree = request.IsFree ?? baseline.IsFree,
            Enabled = request.Enabled ?? baseline.Enabled
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
            .Select(h => ResolveHeader(providerKey: providerKey, request: h, existingHeaders: existingHeaders))
            .ToList();

        var resolvedNames = new HashSet<string>(collection: resolved.Select(h => h.Name),
            comparer: StringComparer.OrdinalIgnoreCase);
        foreach (var existing in existingHeaders)
            if (!string.IsNullOrWhiteSpace(existing.ValueSecretRef) && !resolvedNames.Contains(existing.Name))
                DeleteExistingSecret(providerKey: providerKey, existing: existing, headerName: existing.Name);

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
    private static ProviderOptions WithEnabled(ProviderOptions source, bool enabled)
    {
        return source with { Enabled = enabled };
    }

    /// <summary>
    /// Normalizes a provider display name: empty or whitespace-only strings become null, so clearing the
    /// field in the UI results in a consistent null rather than an empty string; any other value is trimmed.
    /// </summary>
    private static string? NormalizeNameField(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

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
    /// <param name="existingHeaders">
    /// The provider's current headers, consulted for the preserve-on-blank and secret-cleanup
    /// rules.
    /// </param>
    private ProviderHeader ResolveHeader(string providerKey, HeaderWriteRequest request,
        IReadOnlyList<ProviderHeader> existingHeaders)
    {
        var name = request.Name!.Trim();

        // A caller that predates the flag stored every literal write-only, so its headers keep meaning
        // "locked" rather than silently becoming readable.
        var locked = request.Locked ?? true;

        // HTTP header names are case-insensitive, so "X-Foo" and "x-foo" must be treated as the same
        // header when looking up the value to preserve or clean up - otherwise a casing mismatch between
        // what was stored and what the caller resends silently drops the stored secret instead of keeping
        // it, or leaves its protected-store entry orphaned.
        var existing = existingHeaders.FirstOrDefault(h =>
            string.Equals(a: h.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(request.Value))
        {
            if (locked && TryWriteSecret(providerKey: providerKey, headerName: name, value: request.Value))
                return new ProviderHeader
                {
                    Name = name,
                    Value = null,
                    ValueEnvVar = null,
                    ValueSecretRef = SecretRefName(providerKey: providerKey, headerName: name),
                    Locked = true
                };

            // Either unlocked (public configuration, stored verbatim) or the store is unavailable on this
            // platform - either way this header no longer references the store, so drop any stale entry.
            DeleteExistingSecret(providerKey: providerKey, existing: existing, headerName: name);
            return new ProviderHeader { Name = name, Value = request.Value, ValueEnvVar = null, Locked = locked };
        }

        // An env-var-backed header keeps its secret in the environment rather than in configuration, so
        // there is nothing to withhold and it always stores unlocked.
        if (!string.IsNullOrWhiteSpace(request.ValueEnvVar))
        {
            DeleteExistingSecret(providerKey: providerKey, existing: existing, headerName: name);
            return new ProviderHeader
            { Name = name, Value = null, ValueEnvVar = request.ValueEnvVar.Trim(), Locked = false };
        }

        if (request.Locked is false)
        {
            DeleteExistingSecret(providerKey: providerKey, existing: existing, headerName: name);
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
            Locked = (!string.IsNullOrWhiteSpace(preservedValue) || !string.IsNullOrWhiteSpace(preservedSecretRef)) &&
                     locked
        };
    }

    /// <summary>
    /// The protected-store name for one provider header (<c>docs/router/secrets-at-rest-plan.md</c> §3's naming
    /// convention).
    /// </summary>
    private static string SecretRefName(string providerKey, string headerName)
    {
        return $"provider:{providerKey}:header:{headerName}";
    }

    /// <summary>
    /// Writes <paramref name="value"/> to the protected store under this header's name. Returns
    /// <see langword="false"/> when no store is configured or it is unavailable on this platform.
    /// </summary>
    private bool TryWriteSecret(string providerKey, string headerName, string value)
    {
        if (_secretWriter is null) return false;

        try
        {
            _secretWriter.Write(name: SecretRefName(providerKey: providerKey, headerName: headerName), value: value);
            return true;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes <paramref name="existing"/>'s protected-store entry, if it has one, swallowing a store that is
    /// unavailable on this platform.
    /// </summary>
    private void DeleteExistingSecret(string providerKey, ProviderHeader? existing, string headerName)
    {
        if (_secretWriter is null || string.IsNullOrWhiteSpace(existing?.ValueSecretRef)) return;

        try
        {
            _secretWriter.Delete(SecretRefName(providerKey: providerKey, headerName: headerName));
        }
        catch (PlatformNotSupportedException)
        {
            // Nothing was ever written to the store on this platform in the first place.
        }
    }

    /// <summary>
    /// Runs a store mutation and maps it to a <see cref="ManagementResult{T}"/>, translating validation/argument
    /// failures into <see cref="ManagementErrorType.InvalidRequest"/>.
    /// </summary>
    private async Task<ManagementResult<ProvidersResponse>> MutateAsync(Func<Task> mutation)
    {
        try
        {
            await mutation().ConfigureAwait(false);
            return ManagementResult<ProvidersResponse>.Ok(_buildProvidersResponse());
        }
        catch (OptionsValidationException ex)
        {
            return ManagementResult<ProvidersResponse>.Fail(errorType: ManagementErrorType.InvalidRequest,
                message: string.Join(separator: "; ", values: ex.Failures));
        }
        catch (ArgumentException ex)
        {
            // Bad input reaching the store (e.g. a blank key/model name) is a client error, not a fault.
            return ManagementResult<ProvidersResponse>.Fail(errorType: ManagementErrorType.InvalidRequest,
                message: ex.Message);
        }
    }

    /// <summary>
    /// Queries a provider's live OpenAI-shaped model list, sending the same auth/extra headers the forwarding path
    /// uses.
    /// </summary>
    private async Task<DiscoverModelsResponse> DiscoverModelsCoreAsync(ProviderOptions provider,
        CancellationToken cancellationToken)
    {
        Uri target;
        try
        {
            target = new Uri(uriString: ProviderUrlBuilder.BuildModelsUrl(provider.BaseUrl), uriKind: UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            return new DiscoverModelsResponse(false, Models: [], Error: $"Invalid BaseUrl: {ex.Message}");
        }

        using var requestMessage = new HttpRequestMessage(method: HttpMethod.Get, requestUri: target);

        // The provider's credentials and configured custom headers, sent identically to the forwarding path.
        // This is how a provider that requires an extra header for discovery gets it (e.g. Anthropic's
        // anthropic-version) without any provider-specific code here.
        ProviderCredentialResolver.ApplyToRequest(request: requestMessage, provider: provider,
            environment: _environment, secretReader: _secretReader);

        try
        {
            using var response = await _httpClient
                .SendAsync(request: requestMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new DiscoverModelsResponse(
                    false,
                    Models: [],
                    Error: $"Provider returned {(int)response.StatusCode} for {target}.");

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var models = ParseModelIds(body);
            return new DiscoverModelsResponse(true, Models: models, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new DiscoverModelsResponse(false, Models: [], Error: ex.Message);
        }
    }

    /// <summary>Parses an OpenAI-shaped model-list JSON body and returns the <c>id</c> of each entry in its <c>data</c> array.</summary>
    private static IReadOnlyList<string> ParseModelIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty(propertyName: "data", value: out var data) ||
            data.ValueKind != JsonValueKind.Array) return [];

        var ids = new List<string>();
        foreach (var item in data.EnumerateArray())
            if (item.TryGetProperty(propertyName: "id", value: out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value)) ids.Add(value);
            }

        return ids;
    }
}