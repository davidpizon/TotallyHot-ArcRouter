using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The optional collaborators <see cref="ManagementFacade"/> can be given, carried as one named object so
/// its constructor does not take a dozen positional nullable arguments a caller can silently transpose.
/// </summary>
/// <remarks>
/// Deliberately flat, unlike <see cref="ProxyServerDependencies"/>'s per-feature groups. Grouping is only
/// worth its cost where members are genuinely all-or-nothing, and none of these are: every field is checked
/// independently at its use sites, and each is individually useful. <see cref="EndpointScanner"/> is the one
/// near-miss - the facade does check it together with <see cref="CapabilityStore"/> - but that relationship
/// is asymmetric rather than mutual (the scanner needs the store; the store is read on its own in eight
/// other places), so pairing them in a record would make "capability store, no scanner" unexpressible for
/// no safety gain.
/// </remarks>
public sealed record ManagementFacadeDependencies
{
    /// <summary>Per-provider budgets. When absent, providers report no caps or spend and budget edits are unavailable.</summary>
    public ProviderBudgetStore? BudgetStore { get; init; }

    /// <summary>
    /// Probes which API flavors a provider's endpoint answers (docs/router/tool-call-normalization.md §3.3).
    /// Scanning also requires <see cref="CapabilityStore"/>, which is where results are persisted; with
    /// either absent, <c>POST /admin/providers/{key}/scan-capabilities</c> is unavailable.
    /// </summary>
    public ProviderEndpointScanner? EndpointScanner { get; init; }

    /// <summary>
    /// Persists and reads back endpoint/model capability records. Useful without an
    /// <see cref="EndpointScanner"/>, which is why the two are separate properties rather than one group.
    /// </summary>
    public ToolCallCapabilityStore? CapabilityStore { get; init; }

    /// <summary>Supplies each provider's captured <c>anthropic-ratelimit-*</c> snapshot to <c>GET /admin/providers</c>.</summary>
    public PriceCatalogRepository? PriceCatalogRepository { get; init; }

    /// <summary>Operator price overrides, backing <c>PUT/DELETE /admin/price-overrides</c>.</summary>
    public ModelAliasOverrideStore? OverrideStore { get; init; }

    /// <summary>Usage rollups, backing <c>GET /admin/usage/summary</c> and <c>GET /admin/usage/rollup</c>.</summary>
    public IUsageRollupStore? RollupStore { get; init; }

    /// <summary>
    /// How old a captured rate-limit snapshot may be before <see cref="ProviderRateLimitView.IsStale"/> is
    /// set (§5.9). Defaults to 15 minutes.
    /// </summary>
    public TimeSpan? RateLimitStalenessThreshold { get; init; }

    /// <summary>Writes a locked literal header into the protected secret store instead of <c>model-routing.json</c>.</summary>
    public ISecretWriter? SecretWriter { get; init; }

    /// <summary>
    /// Reads a stored credential back when authenticating a provider for model discovery. Kept separate from
    /// <see cref="SecretWriter"/> - the two are never used together, and the split interfaces are what make
    /// the write-only invariant of docs/router/secrets-at-rest-plan.md §4 a compile-time boundary.
    /// </summary>
    public ISecretReader? SecretReader { get; init; }

    /// <summary>Backs Cost Analytics' "Routing ROI" feed, <c>GET /admin/usage/routing-roi</c> (Phase T4).</summary>
    public ITaxonomyComparisonStore? ComparisonStore { get; init; }

    /// <summary>
    /// Tracks the outcome of the most recent admin-initiated interaction with each provider (refresh from
    /// endpoint, capability scan, discovery), surfaced via <see cref="ProviderView.LastInteraction"/>. When
    /// absent, every provider simply reports no interaction history.
    /// </summary>
    public IProviderInteractionStatusStore? InteractionStatusStore { get; init; }
}
