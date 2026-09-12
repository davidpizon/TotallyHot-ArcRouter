using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;
using TotallyHot.ArcRouter.Update;

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
    public IOptions<RoutingOptions>? RoutingOptions { get; init; }

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

    /// <summary>
    /// The Governance UI's Router Model panel API (live-feedback-learning-plan.md Phase 5). <see langword="null"/>
    /// leaves it unmapped.
    /// </summary>
    public LogRegModelAdminDependencies? LogRegModelAdmin { get; init; }

    /// <summary>The Governance UI's System Settings window API (Phase T6). <see langword="null"/> leaves it unmapped.</summary>
    public RouterSettingsAdminDependencies? RouterSettingsAdmin { get; init; }

    /// <summary>
    /// The Governance UI's System Settings window's "Software Update" section API
    /// (docs/router/auto-update-plan.md Phase 2, packaging superseded by
    /// docs/router/packaging-and-distribution.md). Unlike every group above, <c>UpdateAdminGrpcService</c>
    /// is mapped <em>unconditionally</em>, the same way <see cref="RoutingOptions"/> backs the
    /// always-mapped <c>RoutingModeAdminGrpcService</c> - update status is core operational state, not an
    /// optional add-on. When omitted, a private, harmless no-op state store/release-check client back the
    /// service instead of the real ones.
    /// </summary>
    public UpdateAdminDependencies? UpdateAdmin { get; init; }

    /// <summary>
    /// The GUI system tray's "Enable Routing"/"Disable Routing" toggle API. Unlike every optional group
    /// above, <c>RoutingGateAdminGrpcService</c> is mapped <em>unconditionally</em>, the same way
    /// <see cref="UpdateAdmin"/> backs the always-mapped <c>UpdateAdminGrpcService</c> - whether the proxy
    /// accepts routing requests is core operational state, not an optional add-on. When omitted, a private,
    /// unshared <see cref="Router.RoutingGateStore"/> (defaulting to enabled, and not the instance
    /// <see cref="ProxyMiddleware"/> checks) backs the service instead of the real one.
    /// </summary>
    public RoutingGateAdminDependencies? RoutingGateAdmin { get; init; }

    /// <summary>
    /// The Governance UI's Regret Harness panel API (docs/router/regret-evaluation-harness-plan.md N6).
    /// Unlike the optional groups above but like <see cref="UpdateAdmin"/>/<see cref="RoutingGateAdmin"/>,
    /// <c>RegretHarnessAdminGrpcService</c> is mapped <em>unconditionally</em> - its one dependency
    /// (<c>IRegretHarnessRunner</c>) is never an optional feature the way the training services above are.
    /// When omitted, a private <see cref="CodeRouterBench.Evaluation.NullRegretHarnessRunner"/> backs the
    /// service instead of the real one, always declining a run.
    /// </summary>
    public RegretHarnessAdminDependencies? RegretHarnessAdmin { get; init; }

    /// <summary>
    /// The Governance UI's Judge Calibration panel API (docs/router/geval-shadow-scoring-plan.md Phase G2).
    /// Mapped <em>unconditionally</em> for the same reason <see cref="RegretHarnessAdmin"/> is - its one
    /// dependency (<c>IJudgeCalibrationAnalyzer</c>) is registered by the host whether or not the judge is
    /// currently switched on, since the report reads an accumulated table rather than a live grader. When
    /// omitted, a private <see cref="Judge.NullJudgeCalibrationAnalyzer"/> backs the service, returning a
    /// report that says so rather than a silently empty one.
    /// </summary>
    public JudgeCalibrationAdminDependencies? JudgeCalibrationAdmin { get; init; }

    /// <summary>
    /// System Settings' Cost Reconciliation section API (docs/router/agent-cost-tracking.md §5.8).
    /// <see langword="null"/> leaves it unmapped - this is the first admin service added after
    /// <see href="../../../docs/adr/0010-collapse-the-per-feature-admin-slice-onto-shared-seams.md">ADR-0010</see>
    /// shipped, so its addition here (with no corresponding edit to <see cref="ProxyServer"/>) is the
    /// concrete evidence the new seams actually lower the marginal cost of the next knob.
    /// </summary>
    public CostReconciliationAdminDependencies? CostReconciliationAdmin { get; init; }

    /// <summary>
    /// Every optional admin feature group that was actually supplied, as the registration seam
    /// <see cref="ProxyServer"/> drives. Adding an optional admin service means implementing
    /// <see cref="IAdminServiceModule"/> on its group record and adding a name here - never editing
    /// <see cref="ProxyServer"/>, which used to carry one hand-written registration block and one
    /// hand-written endpoint mapping per feature, ~120 lines apart, with nothing but a comment tying the
    /// two halves together.
    /// </summary>
    /// <remarks>
    /// The unconditional services (<c>TelemetryGrpcService</c>, <c>RoutingModeAdminGrpcService</c>,
    /// <c>UpdateAdminGrpcService</c>, <c>RoutingGateAdminGrpcService</c>,
    /// <c>RegretHarnessAdminGrpcService</c>, <c>JudgeCalibrationAdminGrpcService</c>) are deliberately
    /// <em>not</em> here. They map whether or not their group was supplied, falling back to a null-object
    /// collaborator - a module cannot register itself when it does not exist, and folding them in would
    /// change when they map.
    /// </remarks>
    internal IReadOnlyList<IAdminServiceModule> AdminModules =>
    [
        .. new IAdminServiceModule?[]
        {
            PriceSourceAdmin, BenchmarkDataAdmin, LlmRouterModelAdmin, ClusterModelAdmin, LogRegModelAdmin,
            RouterSettingsAdmin, CostReconciliationAdmin
        }.OfType<IAdminServiceModule>()
    ];
}

/// <summary>
/// One optional admin feature's own inner-container registration and endpoint mapping, implemented by the
/// <c>*AdminDependencies</c> record that already carries exactly the collaborators its gRPC service needs.
/// </summary>
/// <remarks>
/// <para>
/// This exists so registration knowledge lives next to the group it describes. Before it,
/// <see cref="ProxyServer"/>'s constructor held one <c>if (xxxAdmin is not null) { ... AddSingleton ... }</c>
/// block per feature and its endpoint block held a matching <c>if</c> - the file's own comments said
/// "Same reasoning again" four times over. The two halves drifting apart produces a service that maps
/// successfully and then throws on its first RPC call, because <c>MapGrpcService</c> only reflects over the
/// service type and never constructs it.
/// </para>
/// <para>
/// Public because C# forbids a public record from listing a less-accessible interface in its base list even
/// when every member is implemented explicitly (CS0061) - the interface itself must be as accessible as the
/// records that implement it. It stays effectively invisible to callers anyway: each record implements both
/// methods explicitly, so a group's public surface is unchanged and callers building one see no new members.
/// See
/// <see href="../../../docs/adr/0010-collapse-the-per-feature-admin-slice-onto-shared-seams.md">ADR-0010</see>.
/// </para>
/// </remarks>
public interface IAdminServiceModule
{
    /// <summary>Registers this feature's collaborators into the inner Kestrel host's private container.</summary>
    /// <param name="services">The inner host's service collection.</param>
    void Register(IServiceCollection services);

    /// <summary>Maps this feature's gRPC service onto the shared TLS admin endpoint.</summary>
    /// <param name="endpoints">The inner host's endpoint builder.</param>
    void Map(IEndpointRouteBuilder endpoints);
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
    /// <summary>
    /// Resolves provider credentials for model discovery. Defaults to a real
    /// <see cref="EnvironmentVariableProvider"/>.
    /// </summary>
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

    /// <summary>
    /// Persists endpoint/model capability records. Useful on its own for reading them back, even with no
    /// <see cref="EndpointScanner"/>.
    /// </summary>
    public ToolCallCapabilityStore? CapabilityStore { get; init; }

    /// <summary>Reads a fresh catalog price, backing the price-resolution diagnosis view.</summary>
    public PriceRepository? PriceRepository { get; init; }

    /// <summary>
    /// Supplies each provider's captured <c>anthropic-ratelimit-*</c> snapshot/history to <c>GET /admin/providers</c>
    /// .
    /// </summary>
    public RateLimitRepository? RateLimitRepository { get; init; }

    /// <summary>
    /// Supplies each provider's own reported usage (docs/router/secrets-at-rest-plan.md §8.1) to
    /// <c>GET /admin/providers</c>.
    /// </summary>
    public ReportedUsageRepository? ReportedUsageRepository { get; init; }

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

    /// <summary>
    /// Tracks the outcome of the most recent admin-initiated interaction with each provider AND (per
    /// docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md) live-traffic
    /// health from the hot request path, surfaced via <see cref="ProviderView.AdminAction"/>/
    /// <see cref="ProviderView.LiveTraffic"/>. Must be the <em>same</em> instance given to
    /// <see cref="RequestInterceptor"/> and <see cref="ProxyMiddleware"/> - otherwise the Providers
    /// tab and the hot path silently observe two disconnected stores. When omitted, <see cref="ProxyServer"/>
    /// falls back to a fresh, unshared instance - <see cref="ProviderView.AdminAction"/> still works
    /// normally (this facade is still the only writer/reader of the admin-triggered refresh/scan/discovery
    /// outcomes it records), but <see cref="ProviderView.LiveTraffic"/> is the part that goes dark: with
    /// no shared instance, the hot request path's out-of-credits classification writes to a store nothing
    /// here ever reads, so the Providers tab never sees any live-traffic state.
    /// </summary>
    public IProviderInteractionStatusStore? InteractionStatusStore { get; init; }
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
    PriceCatalogIngestionService IngestionService) : IAdminServiceModule
{
    /// <summary>
    /// Supplies the poll cadence driving the panel's countdown. The inner host has no configuration bound
    /// into it, so this must arrive explicitly or the panel's <c>IOptions</c> dependency is unresolvable and
    /// fails on the first call rather than at startup. Defaults to <see cref="PriceCatalogOptions"/>'s own values.
    /// </summary>
    public PriceCatalogOptions? Options { get; init; }

    /// <inheritdoc/>
    void IAdminServiceModule.Register(IServiceCollection services)
    {
        services.AddSingleton(ToggleStore);
        services.AddSingleton(IngestionService);

        // The panel's countdown needs the poll cadence, and this inner container has no configuration
        // bound into it - AddGrpc alone would leave the IOptions dependency unresolvable and fail on the
        // first call, not at startup.
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options ?? new PriceCatalogOptions()));
    }

    /// <inheritdoc/>
    void IAdminServiceModule.Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<PriceSourceAdminGrpcService>();
    }
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
    BenchmarkSyncService SyncService) : IAdminServiceModule
{
    /// <summary>Supplies the dataset ref driving the sync action. Defaults to <see cref="BenchmarkSyncOptions"/>'s own values.</summary>
    public BenchmarkSyncOptions? Options { get; init; }

    /// <inheritdoc/>
    void IAdminServiceModule.Register(IServiceCollection services)
    {
        services.AddSingleton(StatusService);
        services.AddSingleton(FileLedger);
        services.AddSingleton(SyncService);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options ?? new BenchmarkSyncOptions()));
    }

    /// <inheritdoc/>
    void IAdminServiceModule.Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<BenchmarkDataAdminGrpcService>();
    }
}

/// <summary>
/// Backs <see cref="LlmRouterModelAdminGrpcService"/>, the Benchmark Data panel's "Local Voter Model"
/// section, which reads the llm_router voter's file sync state, switches models, and runs a sync.
/// </summary>
/// <param name="OverrideStore">The voter's active-model store.</param>
/// <param name="SyncService">Backs the section's sync action.</param>
public sealed record LlmRouterModelAdminDependencies(
    ILlmRouterModelOverrideStore OverrideStore,
    LlmRouterModelSyncService SyncService) : IAdminServiceModule
{
    /// <inheritdoc/>
    void IAdminServiceModule.Register(IServiceCollection services)
    {
        services.AddSingleton(OverrideStore);
        services.AddSingleton(SyncService);
    }

    /// <inheritdoc/>
    void IAdminServiceModule.Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<LlmRouterModelAdminGrpcService>();
    }
}

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
    IOptions<StorageOptions> StorageOptions) : IAdminServiceModule
{
    /// <inheritdoc/>
    void IAdminServiceModule.Register(IServiceCollection services)
    {
        services.AddSingleton(TrainingService);
        services.AddSingleton(MemoryEntryStore);
        services.AddSingleton(TranscriptStore);
        services.AddSingleton(TranscriptOptions);
        services.AddSingleton(StorageOptions);
    }

    /// <inheritdoc/>
    void IAdminServiceModule.Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<ClusterModelAdminGrpcService>();
    }
}

/// <summary>
/// Backs <see cref="LogRegModelAdminGrpcService"/>, the Governance UI's Router Model panel
/// (docs/router/live-feedback-learning-plan.md Phase 5), which reads the trained <c>logreg</c> voter
/// artifact's status and runs a retrain. The service's third dependency, <c>IOptions&lt;RoutingOptions&gt;</c>,
/// is not a member here because <see cref="ProxyServerDependencies.RoutingOptions"/> above already arrives
/// unconditionally - re-declaring it in this group would only duplicate that registration.
/// </summary>
/// <param name="TrainingService">Performs the retrain.</param>
/// <param name="MemoryEntryStore">The live memory entries a retrain draws on.</param>
/// <param name="StorageOptions">Names the path the trained logreg model artifact is written to.</param>
public sealed record LogRegModelAdminDependencies(
    IEmbeddingLogRegTrainingService TrainingService,
    IMemoryEntryStore MemoryEntryStore,
    IOptions<StorageOptions> StorageOptions) : IAdminServiceModule
{
    /// <inheritdoc/>
    void IAdminServiceModule.Register(IServiceCollection services)
    {
        // IOptions<RoutingOptions>, this service's third dependency, is registered unconditionally by
        // ProxyServer - see this record's own remarks.
        services.AddSingleton(TrainingService);
        services.AddSingleton(MemoryEntryStore);
        services.AddSingleton(StorageOptions);
    }

    /// <inheritdoc/>
    void IAdminServiceModule.Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<LogRegModelAdminGrpcService>();
    }
}

/// <summary>
/// Backs <see cref="RouterSettingsAdminGrpcService"/>, the Governance UI's System Settings window
/// (docs/router/self-organizing-classification-plan.md Phase T6) - the one admin service on this endpoint
/// that mutates <see cref="Models.RoutingOptions"/>.
/// </summary>
/// <param name="Store">Persists the settings overrides layered on top of <see cref="Models.RoutingOptions"/>.</param>
/// <param name="ReloadToken">
/// Triggered after a successful save so <paramref name="OptionsMonitor"/> recomputes
/// immediately.
/// </param>
/// <param name="OptionsMonitor">Reports the currently effective values once precedence has been applied.</param>
/// <param name="JudgeOptionsMonitor">
/// Reports the shadow judge's currently effective settings, the same way
/// <paramref name="OptionsMonitor"/> does for routing.
/// </param>
/// <param name="JudgeModelSelector">
/// Supplies the eligible judge-backbone list, both to populate the dropdown and to
/// validate a save against it.
/// </param>
/// <param name="TranscriptOptionsMonitor">
/// Reports the Transcription Capture toggle's currently effective value, the same
/// way <paramref name="OptionsMonitor"/> does for routing.
/// </param>
/// <param name="TranscriptStore">Backs the Transcription Capture row's "Clear" action.</param>
public sealed record RouterSettingsAdminDependencies(
    RouterSettingsStore Store,
    RouterSettingsReloadToken ReloadToken,
    IOptionsMonitor<RoutingOptions> OptionsMonitor,
    IOptionsMonitor<JudgeOptions> JudgeOptionsMonitor,
    JudgeModelSelector JudgeModelSelector,
    IOptionsMonitor<TranscriptOptions> TranscriptOptionsMonitor,
    ITranscriptStore TranscriptStore) : IAdminServiceModule
{
    /// <summary>
    /// The working set trimmed synchronously when a save lowers the capacity, so the response reflects the
    /// trim rather than racing it. Optional: when omitted, the reactive <c>IOptionsMonitor.OnChange</c> trim
    /// still runs, just on its own schedule. Living inside this group rather than beside it is deliberate -
    /// its only consumer is the service this group constructs, so "supplied on its own and silently ignored"
    /// is not a mistake that can be expressed.
    /// </summary>
    public EmbeddingMemory? EmbeddingMemory { get; init; }

    /// <summary>
    /// Reports Phase Q3's CodeJudge/ICE-Score/RACE portfolio's currently effective values, the same way
    /// <see cref="JudgeOptionsMonitor"/> does for the G-Eval judge. An init property rather than a positional
    /// parameter so this group's existing constructor call sites need no change - but <see langword="required"/>,
    /// since the System Settings window's Save action needs it to persist the three toggles.
    /// </summary>
    public required IOptionsMonitor<PortfolioGraderOptions> PortfolioGraderOptionsMonitor { get; init; }

    /// <inheritdoc/>
    void IAdminServiceModule.Register(IServiceCollection services)
    {
        services.AddSingleton(Store);
        services.AddSingleton(ReloadToken);
        services.AddSingleton(OptionsMonitor);
        services.AddSingleton(JudgeOptionsMonitor);
        services.AddSingleton(PortfolioGraderOptionsMonitor);
        services.AddSingleton(JudgeModelSelector);
        services.AddSingleton(TranscriptOptionsMonitor);
        services.AddSingleton(TranscriptStore);

        // Optional within the group - the service takes it as an optional constructor parameter and falls
        // back to the reactive OnChange trim when it is absent.
        if (EmbeddingMemory is not null) services.AddSingleton(EmbeddingMemory);
    }

    /// <inheritdoc/>
    void IAdminServiceModule.Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<RouterSettingsAdminGrpcService>();
    }
}

/// <summary>
/// Backs <see cref="CostReconciliationAdminGrpcService"/>, System Settings' Cost Reconciliation section
/// (docs/router/agent-cost-tracking.md §5.8): reads each configured provider's checkpoint and most recent
/// snapshot, and runs a cycle on demand.
/// </summary>
/// <param name="Store">The reconciliation checkpoint and snapshot history.</param>
/// <param name="ReconciliationService">Backs the section's "Run Now" action.</param>
/// <param name="Reconcilers">
/// Every provider with a resolvable Admin API key at the time this group was built - the set of providers
/// the section reports on. Resolved once from the outer container, like the other collaborators here; an
/// operator who saves a new key from the GUI needs a restart to pick it up on this surface, unlike the
/// background poll loop's own always-fresh <c>Func</c> factory (docs/router/secrets-at-rest-plan.md §7).
/// </param>
public sealed record CostReconciliationAdminDependencies(
    IProviderCostReconciliationStore Store,
    CostReconciliationService ReconciliationService,
    IReadOnlyList<IProviderCostReconciler> Reconcilers) : IAdminServiceModule
{
    /// <inheritdoc/>
    void IAdminServiceModule.Register(IServiceCollection services)
    {
        services.AddSingleton(Store);
        services.AddSingleton(ReconciliationService);
        services.AddSingleton(Reconcilers);
    }

    /// <inheritdoc/>
    void IAdminServiceModule.Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<CostReconciliationAdminGrpcService>();
    }
}

/// <summary>
/// Backs <see cref="Update.UpdateAdminGrpcService"/>, the Governance UI's System Settings window's
/// "Software Update" section (docs/router/auto-update-plan.md Phase 2). Both are required - the service
/// needs its own state store and release-check client - and (unlike every other group in this file) is
/// mapped even when this whole group is <see langword="null"/>: see
/// <see cref="ProxyServerDependencies.UpdateAdmin"/>'s remarks for why. There is no applier here anymore:
/// the Router only detects updates, it never downloads or applies one - that moved to the GUI (see
/// docs/router/packaging-and-distribution.md).
/// </summary>
/// <param name="StateStore">The last-known check outcome the panel reads.</param>
/// <param name="ReleaseCheckClient">Runs the immediate re-check the panel's "Check Now" button triggers.</param>
public sealed record UpdateAdminDependencies(
    IUpdateStateStore StateStore,
    IReleaseCheckClient ReleaseCheckClient);

/// <summary>
/// Backs <see cref="Router.RoutingGateAdminGrpcService"/>, the GUI system tray's routing kill switch.
/// Required - the service needs the same <see cref="Router.IRoutingGate"/> instance
/// <see cref="ProxyMiddleware"/> checks, so toggling it from the tray takes effect on the very next
/// request - and (unlike every other group in this file except <see cref="ProxyServerDependencies.UpdateAdmin"/>)
/// is mapped even when this whole group is <see langword="null"/>: see
/// <see cref="ProxyServerDependencies.RoutingGateAdmin"/>'s remarks.
/// </summary>
/// <param name="Gate">The routing kill switch this service reads and mutates.</param>
public sealed record RoutingGateAdminDependencies(IRoutingGate Gate);

/// <summary>
/// Backs <see cref="CodeRouterBench.Evaluation.RegretHarnessAdminGrpcService"/>, the Governance UI's
/// Regret Harness panel (docs/router/regret-evaluation-harness-plan.md N6). Required - the service needs
/// the outer container's real <see cref="CodeRouterBench.Evaluation.IRegretHarnessRunner"/>, since it
/// depends on the corpus database and embedding client that live there - and (like
/// <see cref="ProxyServerDependencies.UpdateAdmin"/>/<see cref="ProxyServerDependencies.RoutingGateAdmin"/>)
/// is mapped even when this whole group is <see langword="null"/>: see
/// <see cref="ProxyServerDependencies.RegretHarnessAdmin"/>'s remarks.
/// </summary>
/// <param name="Runner">Reports the last run's result and performs a new run for the panel's Run button.</param>
public sealed record RegretHarnessAdminDependencies(CodeRouterBench.Evaluation.IRegretHarnessRunner Runner);

/// <summary>
/// Backs <see cref="Judge.JudgeCalibrationAdminGrpcService"/>, the Governance UI's Judge Calibration panel
/// (docs/router/geval-shadow-scoring-plan.md Phase G2). Required - the service needs the outer container's
/// real <see cref="Judge.IJudgeCalibrationAnalyzer"/>, since it depends on the router-memory database that
/// lives there - and (like <see cref="ProxyServerDependencies.RegretHarnessAdmin"/>) is mapped even when
/// this whole group is <see langword="null"/>: see
/// <see cref="ProxyServerDependencies.JudgeCalibrationAdmin"/>'s remarks.
/// </summary>
/// <param name="Analyzer">Computes the judge-vs-static calibration report the panel renders.</param>
public sealed record JudgeCalibrationAdminDependencies(IJudgeCalibrationAnalyzer Analyzer);