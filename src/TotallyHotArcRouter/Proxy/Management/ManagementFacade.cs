using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

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
/// <para>
/// Per <see href="../../../../docs/adr/0006-split-managementfacade-along-crud-aggregate-boundaries.md"/>,
/// this class now delegates almost all of its implementation to three internal collaborators -
/// <see cref="ProviderManagementService"/> (provider/model CRUD and capability scanning),
/// <see cref="BudgetAndPriceOverrideService"/> (budget and price-override CRUD), and
/// <see cref="SecretManagementService"/> (secret read/write) - while remaining the single class every
/// caller depends on and the single place "is this a security-sensitive operation" is answered. The
/// security boundary is this class's public method set, not its file: any code path that mutates
/// provider/model/budget/price-override/secret state must still go through a public method here, even
/// though the mutation itself now runs inside one of the three collaborators above.
/// </para>
/// </summary>
public sealed class ManagementFacade
{
    // Config default (§5.9); overridable via the constructor's rateLimitStalenessThreshold parameter.
    private static readonly TimeSpan DefaultRateLimitStalenessThreshold = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _rateLimitStalenessThreshold;

    // How far back BuildExhaustionProjections looks for an "earlier" observation to pair with the current
    // snapshot. Short deliberately: a burn rate measured over the last half hour reflects current traffic,
    // not a stale average diluted by a quiet period earlier in the retention window.
    private static readonly TimeSpan ProjectionLookback = TimeSpan.FromMinutes(30);

    private readonly IProviderConfigStore _store;
    private readonly ProviderBudgetStore? _budgetStore;
    private readonly ToolCallCapabilityStore? _capabilityStore;
    private readonly RateLimitRepository? _rateLimitRepository;
    private readonly ReportedUsageRepository? _reportedUsageRepository;
    private readonly ISecretReader? _secretReader;
    private readonly IProviderInteractionStatusStore? _interactionStatus;

    private readonly ProviderManagementService _providerManagementService;
    private readonly BudgetAndPriceOverrideService _budgetAndPriceOverrideService;
    private readonly SecretManagementService _secretManagementService;

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
        _budgetStore = dependencies?.BudgetStore;
        _capabilityStore = dependencies?.CapabilityStore;
        _rateLimitRepository = dependencies?.RateLimitRepository;
        _reportedUsageRepository = dependencies?.ReportedUsageRepository;
        _rateLimitStalenessThreshold = dependencies?.RateLimitStalenessThreshold ?? DefaultRateLimitStalenessThreshold;
        _secretReader = dependencies?.SecretReader;
        _interactionStatus = dependencies?.InteractionStatusStore;

        // Constructed last, once every field BuildProvidersResponse reads is assigned - each collaborator
        // captures the method group as its buildProvidersResponse callback, but none of them invoke it
        // until after this constructor has returned.
        _providerManagementService = new ProviderManagementService(store, environment, httpClient, dependencies, BuildProvidersResponse);
        _budgetAndPriceOverrideService = new BudgetAndPriceOverrideService(store, dependencies, BuildProvidersResponse);
        _secretManagementService = new SecretManagementService(dependencies);
    }

    /// <summary>Lists every configured provider, masked, with its models and (if a budget store is present) its budget.</summary>
    public ProvidersResponse ListProviders() => BuildProvidersResponse();

    /// <summary>
    /// Adds or replaces a provider by key, merging over any existing provider. Delegates to
    /// <see cref="ProviderManagementService.UpsertProviderAsync"/>.
    /// </summary>
    /// <param name="key">The provider key.</param>
    /// <param name="request">The incoming write request.</param>
    /// <param name="cancellationToken">Cancels the underlying store mutation and best-effort capability scan.</param>
    public Task<ManagementResult<ProvidersResponse>> UpsertProviderAsync(
        string key, ProviderWriteRequest request, CancellationToken cancellationToken = default) =>
        _providerManagementService.UpsertProviderAsync(key, request, cancellationToken);

    /// <summary>
    /// Re-probes a provider's endpoint flavors on demand and persists the result
    /// (<c>POST /admin/providers/{key}/scan-capabilities</c>). Delegates to
    /// <see cref="ProviderManagementService.ScanCapabilitiesAsync"/>.
    /// </summary>
    /// <param name="key">The provider key to scan.</param>
    /// <param name="cancellationToken">Cancels the probes.</param>
    public Task<ManagementResult<ProviderEndpointCapabilities>> ScanCapabilitiesAsync(
        string key, CancellationToken cancellationToken = default) =>
        _providerManagementService.ScanCapabilitiesAsync(key, cancellationToken);

    /// <summary>
    /// Pins how one model expresses tool calls, so no automatic scan or live observation can overwrite it
    /// (<c>PUT /admin/providers/{key}/models/{modelName}/tool-dialect</c>). Delegates to
    /// <see cref="ProviderManagementService.SetModelToolDialect"/>.
    /// </summary>
    /// <param name="key">The provider key serving the model.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="request">The dialect to pin, or a null/empty dialect to clear the pin.</param>
    public ManagementResult<ProvidersResponse> SetModelToolDialect(
        string key, string modelName, ModelToolDialectWriteRequest request) =>
        _providerManagementService.SetModelToolDialect(key, modelName, request);

    /// <summary>
    /// Removes a provider by key, cascading to every model that routes to it and to every secret this
    /// provider ever wrote to the protected store. Delegates to
    /// <see cref="ProviderManagementService.RemoveProviderAsync"/>.
    /// </summary>
    /// <param name="key">The provider key to remove.</param>
    /// <param name="cancellationToken">Cancels the underlying store mutation.</param>
    public Task<ManagementResult<ProvidersResponse>> RemoveProviderAsync(string key, CancellationToken cancellationToken = default) =>
        _providerManagementService.RemoveProviderAsync(key, cancellationToken);

    /// <summary>Adds or replaces a model route under a provider. Delegates to <see cref="ProviderManagementService.UpsertModelAsync"/>.</summary>
    /// <param name="providerKey">The provider the model routes to.</param>
    /// <param name="modelName">The client-facing model name.</param>
    /// <param name="request">The incoming write request.</param>
    /// <param name="cancellationToken">Cancels the underlying store mutation and best-effort metadata probe.</param>
    public Task<ManagementResult<ProvidersResponse>> UpsertModelAsync(
        string providerKey, string modelName, ModelWriteRequest request, CancellationToken cancellationToken = default) =>
        _providerManagementService.UpsertModelAsync(providerKey, modelName, request, cancellationToken);

    /// <summary>Removes a model route by name. Delegates to <see cref="ProviderManagementService.RemoveModelAsync"/>.</summary>
    /// <param name="modelName">The model to remove.</param>
    /// <param name="cancellationToken">Cancels the underlying store mutation.</param>
    public Task<ManagementResult<ProvidersResponse>> RemoveModelAsync(string modelName, CancellationToken cancellationToken = default) =>
        _providerManagementService.RemoveModelAsync(modelName, cancellationToken);

    /// <summary>Switches a model on or off. Delegates to <see cref="ProviderManagementService.SetModelEnabledAsync"/>.</summary>
    /// <param name="modelName">The model to toggle.</param>
    /// <param name="request">The desired enabled state.</param>
    /// <param name="cancellationToken">Cancels the underlying store mutation.</param>
    public Task<ManagementResult<ProvidersResponse>> SetModelEnabledAsync(
        string modelName, ModelEnabledWriteRequest request, CancellationToken cancellationToken = default) =>
        _providerManagementService.SetModelEnabledAsync(modelName, request, cancellationToken);

    /// <summary>
    /// Sets or clears a provider's monthly budget caps. A null cap clears that dimension; both null removes
    /// the budget. Delegates to <see cref="BudgetAndPriceOverrideService.SetBudget"/>.
    /// </summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="request">The budget caps/window to set.</param>
    public ManagementResult<ProvidersResponse> SetBudget(string providerKey, ProviderBudgetWriteRequest request) =>
        _budgetAndPriceOverrideService.SetBudget(providerKey, request);

    /// <summary>
    /// For every configured model, reports whether the catalog currently resolves a fresh price for it.
    /// Delegates to <see cref="BudgetAndPriceOverrideService.GetPriceResolutionDiagnosis"/>.
    /// </summary>
    public ManagementResult<IReadOnlyList<PriceResolutionDiagnosisView>> GetPriceResolutionDiagnosis() =>
        _budgetAndPriceOverrideService.GetPriceResolutionDiagnosis();

    /// <summary>Lists every configured price override. Delegates to <see cref="BudgetAndPriceOverrideService.ListPriceOverrides"/>.</summary>
    public ManagementResult<IReadOnlyList<ModelAliasOverride>> ListPriceOverrides() =>
        _budgetAndPriceOverrideService.ListPriceOverrides();

    /// <summary>Adds or replaces an operator price override. Delegates to <see cref="BudgetAndPriceOverrideService.SetPriceOverride"/>.</summary>
    /// <param name="request">The override to add or replace.</param>
    public ManagementResult<IReadOnlyList<ModelAliasOverride>> SetPriceOverride(PriceOverrideWriteRequest request) =>
        _budgetAndPriceOverrideService.SetPriceOverride(request);

    /// <summary>Removes an operator price override. Delegates to <see cref="BudgetAndPriceOverrideService.RemovePriceOverride"/>.</summary>
    /// <param name="sourceName">The override's source name.</param>
    /// <param name="aggregatorModelKey">The override's aggregator model key.</param>
    public ManagementResult<IReadOnlyList<ModelAliasOverride>> RemovePriceOverride(string sourceName, string aggregatorModelKey) =>
        _budgetAndPriceOverrideService.RemovePriceOverride(sourceName, aggregatorModelKey);

    /// <summary>Switches a provider on or off. Delegates to <see cref="ProviderManagementService.SetEnabledAsync"/>.</summary>
    /// <param name="key">The provider key.</param>
    /// <param name="request">The desired enabled state.</param>
    /// <param name="cancellationToken">Cancels the underlying store mutation.</param>
    public Task<ManagementResult<ProvidersResponse>> SetEnabledAsync(
        string key, ProviderEnabledWriteRequest request, CancellationToken cancellationToken = default) =>
        _providerManagementService.SetEnabledAsync(key, request, cancellationToken);

    /// <summary>Queries a provider's own OpenAI-shaped model list. Delegates to <see cref="ProviderManagementService.DiscoverModelsAsync"/>.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="cancellationToken">Cancels the discovery request.</param>
    public Task<ManagementResult<DiscoverModelsResponse>> DiscoverModelsAsync(string providerKey, CancellationToken cancellationToken = default) =>
        _providerManagementService.DiscoverModelsAsync(providerKey, cancellationToken);

    /// <summary>
    /// The consolidated "Refresh from endpoint" operation: discovers a provider's live model list,
    /// reconciles it, then re-probes endpoint flavors and re-runs dialect detection. Delegates to
    /// <see cref="ProviderManagementService.RefreshFromEndpointAsync"/>.
    /// </summary>
    /// <param name="key">The provider key to refresh.</param>
    /// <param name="cancellationToken">Cancels the discovery/scan/detection probes.</param>
    public Task<ManagementResult<ProvidersResponse>> RefreshFromEndpointAsync(string key, CancellationToken cancellationToken = default) =>
        _providerManagementService.RefreshFromEndpointAsync(key, cancellationToken);

    /// <summary>
    /// Returns a provider's rate-limit remaining-over-time series for the last <paramref name="hours"/>
    /// hours, per standard-family dimension - the Providers card's trend-chart data source (§5.9). Kept
    /// directly on the facade rather than moved to a collaborator: it shares
    /// <see cref="RateLimitSnapshotParser"/> parsing logic with <see cref="BuildRateLimitView"/> and
    /// <see cref="BuildExhaustionProjections"/>, which in turn are tightly coupled to
    /// <see cref="BuildProvidersResponse"/>'s own field-projection logic and stay owned by this class per
    /// the ADR - splitting this one query away from that shared parsing logic would cost more cohesion than
    /// it would gain, since it does not belong to the provider-CRUD, budget/price-override, or secret
    /// clusters either.
    /// </summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="hours">How far back to look, clamped to [0.25, 720] hours (15 minutes to 30 days).</param>
    public ManagementResult<RateLimitHistoryResponse> GetRateLimitHistory(string providerKey, double hours)
    {
        if (!_store.Snapshot.Options.Providers.ContainsKey(providerKey))
        {
            return ManagementResult<RateLimitHistoryResponse>.Fail(ManagementErrorType.NotFound, $"Provider '{providerKey}' not found.");
        }

        if (_rateLimitRepository is null)
        {
            return ManagementResult<RateLimitHistoryResponse>.Fail(ManagementErrorType.Unavailable, "Rate-limit history is not available.");
        }

        if (!double.IsFinite(hours))
        {
            return ManagementResult<RateLimitHistoryResponse>.Fail(ManagementErrorType.InvalidRequest, "hours must be a finite number.");
        }

        var clampedHours = Math.Clamp(hours, 0.25, 24 * 30);
        var sinceUtc = DateTimeOffset.UtcNow.AddHours(-clampedHours);
        var buckets = _rateLimitRepository.GetRateLimitHistory(providerKey, sinceUtc);

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

    /// <summary>
    /// Stores a provider's reconciliation Admin API key. Delegates to <see cref="SecretManagementService.SetSecret"/>.
    /// </summary>
    /// <param name="name">The secret name, e.g. <c>reconciliation:openai:admin-key</c>.</param>
    /// <param name="value">The secret value to store.</param>
    public ManagementResult<object?> SetSecret(string name, string value) =>
        _secretManagementService.SetSecret(name, value);

    /// <summary>Clears a stored secret by name. Delegates to <see cref="SecretManagementService.DeleteSecret"/>.</summary>
    /// <param name="name">The secret name to clear.</param>
    public ManagementResult<object?> DeleteSecret(string name) =>
        _secretManagementService.DeleteSecret(name);

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
        if (_rateLimitRepository is null)
        {
            return null;
        }

        var (headers, observedAtUtc) = _rateLimitRepository.GetRateLimitSnapshot(providerKey);
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
        if (_reportedUsageRepository is null)
        {
            return null;
        }

        var (rows, fetchedAtUtc) = _reportedUsageRepository.GetReportedUsage(providerKey);
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

        var history = _rateLimitRepository!.GetRateLimitHistory(providerKey, observedAtUtc - ProjectionLookback);

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

    /// <summary>Classifies a stored header's <see cref="HeaderValueSource"/> from which of its fields is set.</summary>
    private static string ClassifyHeaderSource(ProviderHeader header) =>
        !string.IsNullOrWhiteSpace(header.Value) ? HeaderValueSource.Literal
            : !string.IsNullOrWhiteSpace(header.ValueSecretRef) ? HeaderValueSource.Protected
            : !string.IsNullOrWhiteSpace(header.ValueEnvVar) ? HeaderValueSource.EnvVar
            : HeaderValueSource.None;

    /// <summary>The protected-store name for a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md §3's naming convention), matching <c>Hosting.ServiceCollectionExtensions.AdminApiKeySecretName</c>.</summary>
    private static string AdminKeySecretName(string provider) => $"reconciliation:{provider}:admin-key";
}
