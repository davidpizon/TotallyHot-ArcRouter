using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Mcp;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Update;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Registers the proxy pipeline with the DI container. Split out of
/// <see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions"/> so that adding a
/// dependency here is a change to this feature's own folder rather than an edit to a single
/// 1000-line file every feature shares.
/// </summary>
internal static class ProxyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the request-path proxy plumbing that <see cref="RequestInterceptor"/> and
    /// <see cref="ProxyMiddleware"/> share: live provider/model configuration, the protected secret
    /// store, the circuit breaker, per-provider interaction status, request classification, and the
    /// composite routing policy.
    /// </summary>
    internal static IServiceCollection AddProxyRequestPipeline(this IServiceCollection services)
    {
        // Proxy
        services.AddOptions<ModelRoutingOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(ModelRoutingOptions.SectionName).Bind(options));
        // Writable, live-reloadable provider/model configuration (see ProviderConfigStore). Seeded
        // from the appsettings-bound ModelRoutingOptions above on first run; becomes the source of
        // truth once edited via the management API. ModelRouteResolver reads its snapshots.
        services.AddOptions<ProviderConfigStoreOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(ProviderConfigStoreOptions.SectionName).Bind(options));
        // The generic protected secret store (docs/router/secrets-at-rest-plan.md §3): one DPAPI-
        // encrypted file backing both the resolution surface (ISecretReader, injected into
        // ProviderConfigStore's migration, ModelRouteResolver's request path, and ManagementFacade's
        // model-discovery probe) and the management surface (ISecretWriter, injected into
        // ManagementFacade's write path only - it never receives a reader). Registered before
        // IProviderConfigStore so the container can inject it into ProviderConfigStore's optional
        // migration parameter.
        services.AddSingleton<ProtectedSecretStore>();
        services.AddSingleton<ISecretReader>(sp => sp.GetRequiredService<ProtectedSecretStore>());
        services.AddSingleton<ISecretWriter>(sp => sp.GetRequiredService<ProtectedSecretStore>());
        services.AddSingleton<IProviderConfigStore, ProviderConfigStore>();
        services.AddSingleton<IEnvironmentVariableProvider, EnvironmentVariableProvider>();
        services.AddSingleton<IModelRouteResolver, ModelRouteResolver>();

        // Circuit breaker (docs/router/agent-resilience-strategies.md): registered as a singleton so
        // RequestInterceptor (candidate ranking/substitution) and ProxyMiddleware (recording
        // successes/failures after real attempts) share the exact same per-target state.
        services.AddOptions<CircuitBreakerOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(CircuitBreakerOptions.SectionName).Bind(options));
        services.AddSingleton<ICircuitBreaker, CircuitBreaker>();

        // Shared so RequestInterceptor, ProxyMiddleware, and ManagementFacade (via ManagementApiDependencies
        // below) all see the same per-provider admin-action/live-traffic state
        // (docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md) - the same
        // sharing requirement ICircuitBreaker has just above.
        services.AddSingleton<IProviderInteractionStatusStore, ProviderInteractionStatusStore>();

        // PLAN.md Phase H (the Context leg): classifies a request ahead of routing. Registered so
        // later consumers (Phase I's IRoutingPolicy) can take it as a normal DI dependency; not
        // registering IDimensionInferrer itself is deliberate - RequestInterceptor's own default
        // (a fresh KeywordDimensionInferrer) is what HeuristicRequestClassifier's default also
        // resolves to, keeping the two in lockstep without a shared registration to keep in sync.
        services.AddSingleton<IRequestClassifier, HeuristicRequestClassifier>();

        // PLAN.md Phase I (the Action leg, docs/router/utility-model-routing.md §B3-B4): selection-only
        // routing for the auto/unresolved-model paths. UtilityRoutingPolicy takes IModelPriceCatalog
        // (registered later in this method - DI resolution order is independent of registration order,
        // so this is safe). The three leaf policies (UtilityRoutingPolicy, AgentRouterPolicy, and Phase
        // L's OrchestratorRoutingPolicy above) are registered by concrete type so CompositeRoutingPolicy's
        // constructor can resolve all three directly, with only the composite exposed as IRoutingPolicy.
        services.AddSingleton<UtilityRoutingPolicy>();
        services.AddSingleton<AgentRouterPolicy>();
        services.AddSingleton<IRoutingPolicy, CompositeRoutingPolicy>();
        services.AddSingleton<RequestInterceptor>();

        return services;
    }

    /// <summary>
    /// Registers session/turn/usage telemetry primitives, the per-provider payload translators and
    /// tool-call normalizer, the Bedrock runtime client factory, and the spend tracker/broadcaster.
    /// </summary>
    internal static IServiceCollection AddTelemetryAndTranslation(this IServiceCollection services)
    {
        // Telemetry (live routing events broadcast to GUI dashboards over gRPC - see
        // docs/router/telemetry.md, docs/router/grpc-migration.md, and
        // Telemetry/TelemetryBroadcaster.cs for the outer/inner DI-container bridging this
        // singleton is responsible for).
        services.AddSingleton<ISessionIdResolver, SessionIdResolver>();
        services.AddSingleton<IConversationContinuityMatcher, MessageHistoryContinuityMatcher>();
        // Persistent (ledger-seeded) turn tracker (docs/router/token-tracking-implementation-plan.md
        // Phase 2, §5.5) replaces the process-lifetime-only ConversationTurnTracker as the app's default:
        // a session's turn number now survives a proxy restart. ConversationTurnTracker itself remains in
        // the codebase for tests and any no-ledger direct construction of ProxyMiddleware.
        services.AddSingleton<IConversationTurnTracker, PersistentConversationTurnTracker>();
        // The provider dispatch table (docs/router/code-smell-refactoring-plan.md's dispatch-table
        // task): which response-body shape each provider's captured telemetry bytes are parsed as, and
        // (for a future task) which IProviderCostReconciler reconciles its billed spend. Registered
        // before IUsageExtractor/IResponseTextExtractor so the container injects it into both
        // constructors' optional providerRegistrations parameter - mirroring the IPayloadTranslator
        // dictionary pattern just below, but keyed on parser shape rather than translation logic since
        // that's the only thing that varies between providers for these two extractors.
        services.AddSingleton<IReadOnlyDictionary<string, Proxy.ProviderRegistration>>(_ => Proxy.ProviderRegistrations.BuildDefault());
        services.AddSingleton<IUsageExtractor, UsageExtractor>();
        services.AddSingleton<IResponseTextExtractor, ResponseTextExtractor>();

        // Per-provider payload translators (see docs/router/unified-api-translation.md). Each
        // provider whose native API shape differs from OpenAI's registers one IPayloadTranslator;
        // the provider-keyed map below is what ProxyMiddleware consults. A provider with no
        // translator here is forwarded byte-for-byte, unchanged. Gemini and the three Bedrock
        // providers always translate; Anthropic (§4.4) is dual-mode - its translator is registered
        // here too, but ShouldTranslate lets real Claude Code traffic on /v1/messages keep passing
        // through untouched. The three Bedrock translators (§4.2) additionally implement
        // IBedrockPayloadTranslator, which ProxyMiddleware uses to fork into its AWS-SDK invocation
        // path instead of raw-HTTP forwarding. Registering the map from GetServices means a future
        // provider just adds its own AddSingleton<IPayloadTranslator, ...> to join it, no wiring
        // change here.
        services.AddSingleton<IPayloadTranslator, GeminiPayloadTranslator>();
        services.AddSingleton<IPayloadTranslator, AnthropicPayloadTranslator>();
        services.AddSingleton<IPayloadTranslator, AnthropicOnBedrockPayloadTranslator>();
        services.AddSingleton<IPayloadTranslator, TitanPayloadTranslator>();
        services.AddSingleton<IPayloadTranslator, LlamaPayloadTranslator>();
        services.AddSingleton<IReadOnlyDictionary<string, IPayloadTranslator>>(sp =>
            sp.GetServices<IPayloadTranslator>().ToDictionary(t => t.Provider, StringComparer.OrdinalIgnoreCase));

        // Tool-call normalization (docs/router/tool-call-normalization.md Phase 4) is registered as
        // itself, deliberately NOT also as IPayloadTranslator - it must never join the provider-keyed
        // dictionary above, since what it scans for depends on the (provider, model) pair and on
        // whether the request offered any tools, none of which a provider-keyed lookup can express.
        // ProxyMiddleware picks it up via its own optional constructor parameter and asks it, per
        // request, for a translator.
        services.AddSingleton<ToolCallNormalizerFactory>();
        services.AddSingleton<IBedrockRuntimeClientFactory, BedrockRuntimeClientFactory>();

        // Personal-scale running spend total - terminal output via the injected ILogger, plus a
        // local JSON Lines file. See SpendTracker's remarks.
        services.AddOptions<SpendTrackingOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(SpendTrackingOptions.SectionName).Bind(options));
        services.AddSingleton<ISpendTracker, SpendTracker>();

        services.AddSingleton<TelemetryBroadcaster>();
        services.AddSingleton<TelemetryPublisher>();
        services.AddSingleton<ITelemetryPublisher>(sp => sp.GetRequiredService<TelemetryPublisher>());

        return services;
    }

    /// <summary>
    /// Registers the in-flight request gauge, the routing on/off kill switch, and the
    /// <see cref="ProxyMiddleware"/> singleton itself.
    /// </summary>
    internal static IServiceCollection AddProxyMiddlewareCore(this IServiceCollection services)
    {
        // In-flight request gauge (docs/router/routing-roi-regret-plan.md): a singleton so
        // ProxyMiddleware (incrementing per served request) and TaxonomyComparisonService
        // (hard-pausing its comparison drain while the count is non-zero) observe one number.
        services.AddSingleton<InFlightRequestGauge>();

        // The GUI system tray's "Enable Routing"/"Disable Routing" kill switch: one singleton shared by
        // ProxyMiddleware (which checks it on every request) and RoutingGateAdminGrpcService (which the
        // tray calls to read/toggle it), so a toggle takes effect on the very next request.
        services.AddSingleton<IRoutingGate, RoutingGateStore>();

        // ProxyMiddleware takes its ~25 optional collaborators as one ProxyMiddlewareDependencies
        // bag rather than individual constructor parameters, so the container can no longer
        // auto-assemble it via plain constructor injection - this factory does that assembly
        // explicitly. GetService (not GetRequiredService) for every member preserves the
        // "unregistered = null = feature off" fallback the old parameter-default-value behavior gave
        // for free; this runs once, when ProxyMiddleware's singleton is first resolved, by which point
        // every Add* method in this class has already registered its services regardless of the order
        // those methods were called in.
        services.AddSingleton(sp => new ProxyMiddlewareDependencies
        {
            SessionIdResolver = sp.GetService<ISessionIdResolver>(),
            ContinuityMatcher = sp.GetService<IConversationContinuityMatcher>(),
            TurnTracker = sp.GetService<IConversationTurnTracker>(),
            UsageExtractor = sp.GetService<IUsageExtractor>(),
            ResponseTextExtractor = sp.GetService<IResponseTextExtractor>(),
            TelemetryPublisher = sp.GetService<ITelemetryPublisher>(),
            QualityIngress = sp.GetService<TotallyHot.ArcRouter.Quality.Ingress.IQualityIngress>(),
            SpendTracker = sp.GetService<ISpendTracker>(),
            PriceLookup = sp.GetService<IModelPriceLookup>(),
            Translators = sp.GetService<IReadOnlyDictionary<string, IPayloadTranslator>>(),
            BedrockClientFactory = sp.GetService<IBedrockRuntimeClientFactory>(),
            BudgetStore = sp.GetService<IBudgetEnforcer>(),
            CircuitBreaker = sp.GetService<ICircuitBreaker>(),
            ToolCallNormalizerFactory = sp.GetService<ToolCallNormalizerFactory>(),
            RateLimitCapture = sp.GetService<IRateLimitHeaderCapture>(),
            UsageLedger = sp.GetService<IUsageLedger>(),
            PendingTaskEmbeddingCache = sp.GetService<Router.Embeddings.PendingTaskEmbeddingCache>(),
            RoutingOptions = sp.GetService<IOptions<Models.RoutingOptions>>(),
            PendingRequestCostCache = sp.GetService<Router.Embeddings.PendingRequestCostCache>(),
            PendingRequestProvenanceCache = sp.GetService<Router.Embeddings.PendingRequestProvenanceCache>(),
            PendingResponseTextCache = sp.GetService<TotallyHot.ArcRouter.Judge.PendingResponseTextCache>(),
            TranscriptStore = sp.GetService<TotallyHot.ArcRouter.Transcripts.ITranscriptStore>(),
            InFlightGauge = sp.GetService<InFlightRequestGauge>(),
            RoutingOptionsMonitor = sp.GetService<IOptionsMonitor<Models.RoutingOptions>>(),
            JudgeOptionsMonitor = sp.GetService<IOptionsMonitor<TotallyHot.ArcRouter.Judge.JudgeOptions>>(),
            RoutingGate = sp.GetService<IRoutingGate>(),
            CapabilityStore = sp.GetService<IToolCallCapabilityStore>(),
            ContextWindowStore = sp.GetService<IModelContextWindowStore>(),
            InteractionStatusStore = sp.GetService<IProviderInteractionStatusStore>()
        });

        services.AddSingleton<ProxyMiddleware>();

        return services;
    }

    /// <summary>
    /// Registers the shared management core (docs/router/mcp-endpoint-plan.md): the provider-endpoint
    /// scanner, <see cref="ManagementFacade"/>, and the MCP management endpoint hosted service that
    /// projects the facade over a dedicated loopback TLS port.
    /// </summary>
    internal static IServiceCollection AddManagement(this IServiceCollection services)
    {
        // Shared management core (docs/router/mcp-endpoint-plan.md): both the REST /admin/* API and the
        // MCP endpoint's provider tools project through this one facade, so credential masking, header
        // resolution, and validation happen in exactly one place. Registered here so MCP (which lives in
        // this outer container) can resolve it; ProxyServer builds its own instance from the same
        // underlying stores for REST - the facade is stateless, so the two instances behave identically.
        services.AddSingleton<HttpClient>();
        // Probes a provider's well-known paths for which API flavors it answers
        // (docs/router/tool-call-normalization.md §3.3). Registered before the facade so the container
        // injects it into the facade's optional constructor parameters.
        services.AddSingleton<ProviderEndpointScanner>();
        // ManagementFacade's constructor resolves the price-catalog repositories registered above
        // (PriceRepository, RateLimitRepository, ReportedUsageRepository) automatically, so the
        // Anthropic Usage card's rate-limit snapshot is available on this MCP-facing facade the same way
        // it is on the REST one ProxyHostedService builds below.
        services.AddSingleton<ManagementFacade>();

        // MCP (Model Context Protocol) management endpoint - agent-facing access to the same
        // provider/model/budget/price-source management as REST /admin/*, over a dedicated loopback TLS
        // port. See docs/router/mcp-endpoint-plan.md.
        services.AddOptions<McpOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(McpOptions.SectionName).Bind(options));
        services.AddHostedService<McpHostedService>();

        return services;
    }

    /// <summary>
    /// Registers the inner Kestrel host: <c>ProxyHostedService</c>, handed an already-constructed
    /// <see cref="ProxyMiddleware"/> plus every dependency bag its gRPC admin surfaces need, since the
    /// inner host has its own container and cannot resolve any of it from this one.
    /// </summary>
    internal static IServiceCollection AddProxyHost(this IServiceCollection services)
    {
        // ProxyServer's inner Kestrel host is handed an already-constructed ProxyMiddleware instance rather
        // than a copy of this IServiceCollection. It never gets its own IHostedService registrations, so it
        // can never end up recursively constructing another ProxyHostedService.
        services.AddHostedService(sp =>
        {
            // The /admin/* management API is served from the same host: pass the writable config store
            // (edits reload the router live), the credential accessor for model discovery, and the
            // always-present per-user management token (see ManagementAccessToken) that gates every
            // /admin request by default - the same token the MCP endpoint requires, so both management
            // surfaces are gated identically out of the box. The price catalog singletons are passed
            // across for the same reason as the broadcaster: the inner host has its own container and
            // cannot resolve them from this one. They back the Governance > Price Sources panel's gRPC API.
            return new ProxyHostedService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProxyHostedService>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProxyServer>>(),
                sp.GetRequiredService<ProxyMiddleware>(),
                // Lets a port clash stop the host in an orderly way instead of throwing out of
                // StartAsync - see ProxyHostedService.StartAsync.
                sp.GetRequiredService<IHostApplicationLifetime>(),
                dependencies: new ProxyServerDependencies
                {
                    Telemetry = sp.GetRequiredService<TelemetryBroadcaster>(),
                    // The always-present per-user token that gates every /admin request and every gRPC
                    // call by default - the same token the MCP endpoint requires, so both management
                    // surfaces are gated identically out of the box.
                    ManagementToken = ManagementAccessToken.GetOrCreate(),
                    // Backs the Governance > Routing Mode panel's gRPC API (docs/router/orchestrator-live-path-plan.md §M3.2).
                    RoutingOptions = sp.GetRequiredService<IOptions<RoutingOptions>>(),

                    // The /admin/* management REST API. The writable config store makes edits reload the
                    // router live; the rest is what the REST facade needs, passed across for the same
                    // reason as everything else here - the inner host has its own container and cannot
                    // resolve any of it from this one.
                    ManagementApi = new ManagementApiDependencies(sp.GetRequiredService<IProviderConfigStore>())
                    {
                        Environment = sp.GetRequiredService<IEnvironmentVariableProvider>(),
                        BudgetStore = sp.GetRequiredService<ProviderBudgetStore>(),
                        EndpointScanner = sp.GetRequiredService<ProviderEndpointScanner>(),
                        CapabilityStore = sp.GetRequiredService<ToolCallCapabilityStore>(),
                        PriceRepository = sp.GetRequiredService<PriceRepository>(),
                        RateLimitRepository = sp.GetRequiredService<RateLimitRepository>(),
                        ReportedUsageRepository = sp.GetRequiredService<ReportedUsageRepository>(),
                        ModelAliasOverrideStore = sp.GetRequiredService<ModelAliasOverrideStore>(),
                        UsageRollupStore = sp.GetRequiredService<IUsageRollupStore>(),
                        SecretWriter = sp.GetRequiredService<ISecretWriter>(),
                        SecretReader = sp.GetRequiredService<ISecretReader>(),
                        // Backs Cost Analytics' "Routing ROI" feed (docs/router/self-organizing-classification-plan.md Phase T4).
                        TaxonomyComparisonStore = sp.GetRequiredService<TotallyHot.ArcRouter.Transcripts.ITaxonomyComparisonStore>(),
                        // The same singleton registered above and given to RequestInterceptor/ProxyMiddleware,
                        // so the Providers tab's AdminAction/LiveTraffic warnings reflect real hot-path state.
                        InteractionStatusStore = sp.GetRequiredService<IProviderInteractionStatusStore>(),
                    },

                    // Backs the Governance > Price Sources panel's gRPC API.
                    PriceSourceAdmin = new PriceSourceAdminDependencies(
                        sp.GetRequiredService<PriceSourceToggleStore>(),
                        sp.GetRequiredService<PriceCatalogIngestionService>())
                    {
                        Options = sp.GetRequiredService<IOptions<PriceCatalogOptions>>().Value,
                    },

                    // Backs the Governance > Benchmark Data panel's gRPC API (Phase 4).
                    BenchmarkDataAdmin = new BenchmarkDataAdminDependencies(
                        sp.GetRequiredService<BenchmarkDataStatusService>(),
                        sp.GetRequiredService<BenchmarkFileLedger>(),
                        sp.GetRequiredService<BenchmarkSyncService>())
                    {
                        Options = sp.GetRequiredService<IOptions<BenchmarkSyncOptions>>().Value,
                    },

                    // Backs the Governance > Benchmark Data panel's "Local Voter Model" gRPC API.
                    LlmRouterModelAdmin = new LlmRouterModelAdminDependencies(
                        sp.GetRequiredService<Router.TextGeneration.ILlmRouterModelOverrideStore>(),
                        sp.GetRequiredService<Router.TextGeneration.LlmRouterModelSyncService>()),

                    // Backs the Governance > Cluster Model panel's gRPC API (Phase T5).
                    ClusterModelAdmin = new ClusterModelAdminDependencies(
                        sp.GetRequiredService<Router.Orchestrator.IClusterTrainingService>(),
                        sp.GetRequiredService<Router.IMemoryEntryStore>(),
                        sp.GetRequiredService<TotallyHot.ArcRouter.Transcripts.ITranscriptStore>(),
                        sp.GetRequiredService<IOptions<TotallyHot.ArcRouter.Transcripts.TranscriptOptions>>(),
                        sp.GetRequiredService<IOptions<StorageOptions>>()),

                    // Backs the Governance > Router Model panel's gRPC API (live-feedback-learning-plan.md Phase 5).
                    LogRegModelAdmin = new LogRegModelAdminDependencies(
                        sp.GetRequiredService<Router.Orchestrator.IEmbeddingLogRegTrainingService>(),
                        sp.GetRequiredService<Router.IMemoryEntryStore>(),
                        sp.GetRequiredService<IOptions<StorageOptions>>()),

                    // Backs the Governance UI's System Settings window gRPC API (Phase T6).
                    RouterSettingsAdmin = new RouterSettingsAdminDependencies(
                        sp.GetRequiredService<Router.RouterSettingsStore>(),
                        sp.GetRequiredService<Router.RouterSettingsReloadToken>(),
                        sp.GetRequiredService<IOptionsMonitor<RoutingOptions>>(),
                        sp.GetRequiredService<IOptionsMonitor<TotallyHot.ArcRouter.Judge.JudgeOptions>>(),
                        sp.GetRequiredService<TotallyHot.ArcRouter.Judge.JudgeModelSelector>(),
                        sp.GetRequiredService<IOptionsMonitor<TotallyHot.ArcRouter.Transcripts.TranscriptOptions>>(),
                        sp.GetRequiredService<TotallyHot.ArcRouter.Transcripts.ITranscriptStore>())
                    {
                        EmbeddingMemory = sp.GetRequiredService<EmbeddingMemory>(),
                    },

                    // Backs the Governance UI's System Settings window's "Software Update" section gRPC
                    // API (docs/router/auto-update-plan.md Phase 2).
                    UpdateAdmin = new UpdateAdminDependencies(
                        sp.GetRequiredService<IUpdateStateStore>(),
                        sp.GetRequiredService<IReleaseCheckClient>()),

                    // Backs the GUI system tray's "Enable Routing"/"Disable Routing" gRPC API. The same
                    // singleton ProxyMiddleware checks, so a toggle from the tray takes effect immediately.
                    RoutingGateAdmin = new RoutingGateAdminDependencies(sp.GetRequiredService<IRoutingGate>()),
                });
        });

        return services;
    }
}
