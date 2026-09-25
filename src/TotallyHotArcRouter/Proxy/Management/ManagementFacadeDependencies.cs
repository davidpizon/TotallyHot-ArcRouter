using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

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
    /// either absent, scanning via <see cref="ManagementFacade.ScanCapabilitiesAsync"/> is unavailable.
    /// </summary>
    public ProviderEndpointScanner? EndpointScanner { get; init; }

    /// <summary>
    /// Persists and reads back endpoint/model capability records. Useful without an
    /// <see cref="EndpointScanner"/>, which is why the two are separate properties rather than one group.
    /// </summary>
    public ToolCallCapabilityStore? CapabilityStore { get; init; }

    /// <summary>Reads a fresh catalog price, backing the price-resolution diagnosis view.</summary>
    public PriceRepository? PriceRepository { get; init; }

    /// <summary>
    /// Supplies each provider's captured <c>anthropic-ratelimit-*</c> snapshot/history to
    /// <see cref="ManagementFacade.ListProviders"/>.
    /// </summary>
    public RateLimitRepository? RateLimitRepository { get; init; }

    /// <summary>
    /// Supplies each provider's own reported usage (docs/router/secrets-at-rest-plan.md §8.1) to
    /// <see cref="ManagementFacade.ListProviders"/>.
    /// </summary>
    public ReportedUsageRepository? ReportedUsageRepository { get; init; }

    /// <summary>Operator price overrides, backing <see cref="ManagementFacade"/>'s price-override methods.</summary>
    public ModelAliasOverrideStore? OverrideStore { get; init; }

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

    /// <summary>
    /// Tracks the outcome of the most recent admin-initiated interaction with each provider (refresh from
    /// endpoint, capability scan, discovery), surfaced via <see cref="ProviderView.AdminAction"/>, and
    /// (docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md) the most recent
    /// classified live-traffic outcome, surfaced via <see cref="ProviderView.LiveTraffic"/>. When absent,
    /// every provider simply reports no interaction history on either track.
    /// </summary>
    public IProviderInteractionStatusStore? InteractionStatusStore { get; init; }

    /// <summary>
    /// Writes model-discovery failures (a non-success status from <c>GET /v1/models</c>, a missing
    /// credential, a header name HTTP refused). The provider card already shows that failure; this is what
    /// puts the same fact in the log file. Optional because tests construct the facade without a logger,
    /// and because the facade is built inside the inner host — pass the outer host's logger so the line
    /// reaches the operator-facing file sink rather than an unconfigured inner logger.
    /// </summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// The appsettings <c>ModelRouting</c> section, used as the add-provider template catalog. When a
    /// provider write names a <see cref="ProviderOptions.ProviderType"/> that matches a key here, that
    /// entry's <c>Aws*</c> fields are copied onto the saved provider. When absent, those fields are left
    /// untouched (the previous preserve-on-edit behavior).
    /// </summary>
    public ModelRoutingOptions? ModelRoutingTemplates { get; init; }
}