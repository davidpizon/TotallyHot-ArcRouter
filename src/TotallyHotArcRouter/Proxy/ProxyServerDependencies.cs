using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Everything <see cref="ProxyServer"/> must be handed across the boundary between the application's DI
/// container and the inner Kestrel host's own, private one. That boundary is deliberate (see
/// <see cref="ProxyServer"/>'s constructor remarks: sharing the containers previously caused unbounded
/// recursive construction of <see cref="Hosting.ProxyHostedService"/>), and its consequence is that nothing
/// here can be auto-resolved - each dependency has to arrive explicitly or the feature it backs simply does
/// not exist on the running server.
/// </summary>
/// <remarks>
/// Grouped by feature rather than flattened, because most of these dependencies are all-or-nothing: an admin
/// gRPC service needs every member of its group or it cannot be constructed at all. Each group record below
/// takes its required members as non-nullable positional parameters, so "supplied two of the three" is a
/// compile error rather than a service that maps successfully and then throws on its first RPC call - a
/// failure that would otherwise surface at runtime, in production, long after the mistake was made.
/// A <see langword="null"/> group means that feature is switched off and its endpoint is left unmapped.
/// </remarks>
public sealed record ProxyServerDependencies
{
    /// <summary>
    /// The outer application's shared broadcaster, so <see cref="TelemetryGrpcService"/> streams the same
    /// events the rest of the application publishes. When omitted, <see cref="ProxyServer"/> creates a
    /// private instance: the gRPC endpoint still exists, it just has nothing to broadcast.
    /// </summary>
    public TelemetryBroadcaster? Telemetry { get; init; }

    /// <summary>
    /// The shared secret required in the <c>X-Admin-Token</c> header on every <c>/admin/*</c> request and,
    /// via <see cref="TelemetryAuthInterceptor"/>, on every call to the TLS gRPC endpoint. Deliberately not
    /// a member of <see cref="ManagementApi"/>: it gates both surfaces independently, so burying it there
    /// would wrongly couple gRPC authentication to the REST API being enabled. <see langword="null"/> means
    /// no inbound auth, which only a test exercising plain forwarding should choose.
    /// </summary>
    public string? ManagementToken { get; init; }

    /// <summary>
    /// Routing configuration backing <see cref="RoutingModeAdminGrpcService"/>. Unlike every group below,
    /// that service is mapped <em>unconditionally</em> - routing configuration is core, not an add-on that
    /// can be absent - so this property changes only the values the Routing Mode panel reports, never
    /// whether its API exists. When omitted, <see cref="Models.RoutingOptions"/>'s own coded defaults are
    /// reported.
    /// </summary>
    public IOptions<Models.RoutingOptions>? RoutingOptions { get; init; }

    /// <summary>The <c>/admin/*</c> REST management API on the plain-HTTP port. <see langword="null"/> leaves it unmapped.</summary>
    public ManagementApiDependencies? ManagementApi { get; init; }

    /// <summary>The Governance UI's Price Sources panel API. <see langword="null"/> leaves it unmapped.</summary>
    public PriceSourceAdminDependencies? PriceSourceAdmin { get; init; }

    /// <summary>The Governance UI's Benchmark Data panel API. <see langword="null"/> leaves it unmapped.</summary>
    public BenchmarkDataAdminDependencies? BenchmarkDataAdmin { get; init; }

    /// <summary>The Governance UI's "Local Voter Model" section API. <see langword="null"/> leaves it unmapped.</summary>
    public LlmRouterModelAdminDependencies? LlmRouterModelAdmin { get; init; }

    /// <summary>The Governance UI's Cluster Model panel API (Phase T5). <see langword="null"/> leaves it unmapped.</summary>
    public ClusterModelAdminDependencies? ClusterModelAdmin { get; init; }

    /// <summary>The Governance UI's System Settings window API (Phase T6). <see langword="null"/> leaves it unmapped.</summary>
    public RouterSettingsAdminDependencies? RouterSettingsAdmin { get; init; }
}

/// <summary>
/// Backs the <c>/admin/*</c> REST management API (see <see cref="ProviderAdminEndpoints"/>), which shares
/// the plain-HTTP forwarding port - real LLM traffic never targets <c>/admin</c>, so it is never intercepted.
/// Everything optional here is forwarded to <see cref="ManagementFacade"/>, the shared security boundary the
/// MCP provider tools use too; an absent member makes its endpoints answer
/// <see cref="ManagementErrorType.Unavailable"/> rather than failing the whole API.
/// </summary>
/// <param name="ConfigStore">
/// The writable provider/model configuration store. Required: it is what makes the API meaningful at all,
/// and edits through it reload the running router live.
/// </param>
public sealed record ManagementApiDependencies(IProviderConfigStore ConfigStore)
{
    /// <summary>Resolves provider credentials for model discovery. Defaults to a real <see cref="EnvironmentVariableProvider"/>.</summary>
    public IEnvironmentVariableProvider? Environment { get; init; }

    /// <summary>
    /// Queries a provider's live model list. When omitted <see cref="ProxyServer"/> creates one and owns it,
    /// disposing exactly what it created and never a client the caller still uses elsewhere.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Per-provider budgets. When absent, providers report no caps or spend and budget edits are unavailable.</summary>
    public ProviderBudgetStore? BudgetStore { get; init; }

    /// <summary>
    /// Probes which API flavors a provider's endpoint answers (docs/router/tool-call-normalization.md §3.3).
    /// Only useful alongside <see cref="CapabilityStore"/>, which is where its results are persisted.
    /// </summary>
    public ProviderEndpointScanner? EndpointScanner { get; init; }

    /// <summary>Persists endpoint/model capability records. Useful on its own for reading them back, even with no <see cref="EndpointScanner"/>.</summary>
    public ToolCallCapabilityStore? CapabilityStore { get; init; }

    /// <summary>Supplies each provider's captured <c>anthropic-ratelimit-*</c> snapshot to <c>GET /admin/providers</c>.</summary>
    public PriceCatalogRepository? PriceCatalogRepository { get; init; }

    /// <summary>Operator price overrides, backing <c>PUT/DELETE /admin/price-overrides</c>.</summary>
    public ModelAliasOverrideStore? ModelAliasOverrideStore { get; init; }

    /// <summary>Usage rollups, backing <c>GET /admin/usage/summary</c> and <c>GET /admin/usage/rollup</c>.</summary>
    public IUsageRollupStore? UsageRollupStore { get; init; }

    /// <summary>Writes a locked literal header into the protected secret store instead of <c>model-routing.json</c>.</summary>
    public ISecretWriter? SecretWriter { get; init; }

    /// <summary>Authenticates a provider whose credential lives in the protected secret store during model discovery.</summary>
    public ISecretReader? SecretReader { get; init; }

    /// <summary>Backs Cost Analytics' "Routing ROI" feed, <c>GET /admin/usage/routing-roi</c> (Phase T4).</summary>
    public ITaxonomyComparisonStore? TaxonomyComparisonStore { get; init; }
}

/// <summary>
/// Backs <see cref="PriceSourceAdminGrpcService"/>, the Governance UI's Price Sources panel. Shares the TLS
/// gRPC port with the telemetry stream; price data itself never crosses it (D5), only feed metadata and the
/// toggle/refresh commands.
/// </summary>
/// <param name="ToggleStore">Enables and disables individual price feeds.</param>
/// <param name="IngestionService">Backs the panel's "Pull Now" action.</param>
public sealed record PriceSourceAdminDependencies(
    PriceSourceToggleStore ToggleStore,
    PriceCatalogIngestionService IngestionService)
{
    /// <summary>
    /// Supplies the poll cadence driving the panel's countdown. The inner host has no configuration bound
    /// into it, so this must arrive explicitly or the panel's <c>IOptions</c> dependency is unresolvable and
    /// fails on the first call rather than at startup. Defaults to <see cref="PriceCatalogOptions"/>'s own values.
    /// </summary>
    public PriceCatalogOptions? Options { get; init; }
}

/// <summary>
/// Backs <see cref="BenchmarkDataAdminGrpcService"/>, the Governance UI's Benchmark Data panel, which reads
/// the CodeRouterBench corpus's sync state, rechecks it, and runs a sync.
/// </summary>
/// <param name="StatusService">The corpus freshness cache.</param>
/// <param name="FileLedger">The per-file sync ledger.</param>
/// <param name="SyncService">Backs the panel's sync action.</param>
public sealed record BenchmarkDataAdminDependencies(
    BenchmarkDataStatusService StatusService,
    BenchmarkFileLedger FileLedger,
    BenchmarkSyncService SyncService)
{
    /// <summary>Supplies the dataset ref driving the sync action. Defaults to <see cref="BenchmarkSyncOptions"/>'s own values.</summary>
    public BenchmarkSyncOptions? Options { get; init; }
}

/// <summary>
/// Backs <see cref="LlmRouterModelAdminGrpcService"/>, the Benchmark Data panel's "Local Voter Model"
/// section, which reads the llm_router voter's file sync state, switches models, and runs a sync.
/// </summary>
/// <param name="OverrideStore">The voter's active-model store.</param>
/// <param name="SyncService">Backs the section's sync action.</param>
public sealed record LlmRouterModelAdminDependencies(
    ILlmRouterModelOverrideStore OverrideStore,
    LlmRouterModelSyncService SyncService);

/// <summary>
/// Backs <see cref="ClusterModelAdminGrpcService"/>, the Governance UI's Cluster Model panel
/// (docs/router/self-organizing-classification-plan.md Phase T5), which reads the trained artifact's status
/// and runs a retrain. All five members are required - a retrain reads live memory and transcripts, and
/// writes the artifact named by the storage configuration.
/// </summary>
/// <param name="TrainingService">Performs the retrain.</param>
/// <param name="MemoryEntryStore">The live memory entries a retrain draws on.</param>
/// <param name="TranscriptStore">The transcripts a retrain draws on.</param>
/// <param name="TranscriptOptions">Transcript retention configuration.</param>
/// <param name="StorageOptions">Names the path the trained cluster model artifact is written to.</param>
public sealed record ClusterModelAdminDependencies(
    IClusterTrainingService TrainingService,
    IMemoryEntryStore MemoryEntryStore,
    ITranscriptStore TranscriptStore,
    IOptions<TranscriptOptions> TranscriptOptions,
    IOptions<StorageOptions> StorageOptions);

/// <summary>
/// Backs <see cref="RouterSettingsAdminGrpcService"/>, the Governance UI's System Settings window
/// (docs/router/self-organizing-classification-plan.md Phase T6) - the one admin service on this endpoint
/// that mutates <see cref="Models.RoutingOptions"/>.
/// </summary>
/// <param name="Store">Persists the settings overrides layered on top of <see cref="Models.RoutingOptions"/>.</param>
/// <param name="ReloadToken">Triggered after a successful save so <paramref name="OptionsMonitor"/> recomputes immediately.</param>
/// <param name="OptionsMonitor">Reports the currently effective values once precedence has been applied.</param>
public sealed record RouterSettingsAdminDependencies(
    RouterSettingsStore Store,
    RouterSettingsReloadToken ReloadToken,
    IOptionsMonitor<Models.RoutingOptions> OptionsMonitor)
{
    /// <summary>
    /// The working set trimmed synchronously when a save lowers the capacity, so the response reflects the
    /// trim rather than racing it. Optional: when omitted, the reactive <c>IOptionsMonitor.OnChange</c> trim
    /// still runs, just on its own schedule. Living inside this group rather than beside it is deliberate -
    /// its only consumer is the service this group constructs, so "supplied on its own and silently ignored"
    /// is not a mistake that can be expressed.
    /// </summary>
    public EmbeddingMemory? EmbeddingMemory { get; init; }
}
