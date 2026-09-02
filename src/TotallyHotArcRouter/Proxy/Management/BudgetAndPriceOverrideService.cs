using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The budget and price-override CRUD collaborator split out of <see cref="ManagementFacade"/> per
/// <see href="../../../../docs/adr/0006-split-managementfacade-along-crud-aggregate-boundaries.md"/>:
/// setting or clearing a provider's monthly/weekly/rolling-window budget caps, reporting which
/// configured models the price catalog currently resolves a fresh price for, and CRUD over operator
/// price overrides (§5.7's <c>OperatorOverride</c> resolution rung). Reachable only through
/// <see cref="ManagementFacade"/>'s public methods - it is constructed directly by the facade and is not
/// registered in DI as an independently reachable service, so <see cref="ManagementFacade"/>'s public
/// method set remains the single security boundary the ADR describes.
/// </summary>
internal sealed class BudgetAndPriceOverrideService
{
    // Mirrors PriceCatalogModelPriceLookup.FreshnessFloor: the same "fresh enough to act on" definition
    // the request path and the startup health check already use. Duplicated rather than shared because
    // that constant is private to a request-path type this diagnosis view has no other reason to depend on.
    private static readonly TimeSpan PriceFreshnessFloor = TimeSpan.FromHours(24);

    private readonly IProviderConfigStore _store;
    private readonly ProviderBudgetStore? _budgetStore;
    private readonly PriceRepository? _priceRepository;
    private readonly ModelAliasOverrideStore? _overrideStore;
    private readonly Func<ProvidersResponse> _buildProvidersResponse;

    /// <summary>
    /// Initializes a new instance of the <see cref="BudgetAndPriceOverrideService"/> class.
    /// </summary>
    /// <param name="store">The writable provider/model configuration store, consulted to validate that a budget/override target provider or model actually exists.</param>
    /// <param name="dependencies">The same optional collaborators bag <see cref="ManagementFacade"/> was constructed with; only <see cref="ManagementFacadeDependencies.BudgetStore"/>, <see cref="ManagementFacadeDependencies.PriceRepository"/>, and <see cref="ManagementFacadeDependencies.OverrideStore"/> are used here.</param>
    /// <param name="buildProvidersResponse">
    /// Builds the masked, client-facing <see cref="ProvidersResponse"/> from the current store snapshot.
    /// Owned by <see cref="ManagementFacade"/> rather than this service, since it projects fields spanning
    /// every CRUD cluster - not just budget/price-override state - so this service calls back into it
    /// after a successful budget write instead of duplicating it.
    /// </param>
    public BudgetAndPriceOverrideService(
        IProviderConfigStore store,
        ManagementFacadeDependencies? dependencies,
        Func<ProvidersResponse> buildProvidersResponse)
    {
        _store = store;
        _budgetStore = dependencies?.BudgetStore;
        _priceRepository = dependencies?.PriceRepository;
        _overrideStore = dependencies?.OverrideStore;
        _buildProvidersResponse = buildProvidersResponse;
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
            return ManagementResult<ProvidersResponse>.Ok(_buildProvidersResponse());
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

    /// <summary>
    /// For every configured <c>ModelRouting:ModelList</c> entry, reports whether the catalog currently
    /// resolves a fresh price for it and, if so, whether that price is an approximate match (§5.7's ladder,
    /// below <see cref="ResolutionRung.Exact"/>/<see cref="ResolutionRung.OperatorOverride"/>). The
    /// Governance price-overrides pane's read-only diagnosis view: this is what tells an operator *which*
    /// models actually need an override, before they add one.
    /// </summary>
    public ManagementResult<IReadOnlyList<PriceResolutionDiagnosisView>> GetPriceResolutionDiagnosis()
    {
        if (_priceRepository is null)
        {
            return ManagementResult<IReadOnlyList<PriceResolutionDiagnosisView>>.Fail(
                ManagementErrorType.Unavailable, "The price catalog is not available.");
        }

        var rows = _store.Snapshot.Options.ModelList
            .Select(entry =>
            {
                var price = _priceRepository.GetFreshPrice(new ModelKey(entry.ModelName, entry.Provider), PriceFreshnessFloor);
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
}
