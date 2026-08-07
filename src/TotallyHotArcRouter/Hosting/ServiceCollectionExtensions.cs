using TotallyHot.ArcRouter.Mcp;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Sandbox.DependencyInjection;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Hosting
{
    /// <summary>
    /// Extension methods for setting up agentic router services in an <see cref="IServiceCollection" />.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the core services for the agentic router to the specified <see cref="IServiceCollection" />.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection" /> to add the services to.</param>
        /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
        public static IServiceCollection AddTotallyHotArcRouter(this IServiceCollection services)
        {
            // Core Router
            services.AddSingleton<IRouterMemoryStore, JsonRouterMemoryStore>();
            services.AddSingleton<RouterMemory>();
            // Note: IRouterModelClient is not registered here as it will be context-specific.
            // It should be provided by a factory or a more specific DI scope.
            services.AddTransient<AgentAsARouter>();

            // Tools
            services.AddTransient<CheckSyntax>();
            services.AddTransient<RunVisibleTests>();
            services.AddTransient<EstimateQuality>();

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
            services.AddSingleton<RequestInterceptor>();

            // Telemetry (live routing events broadcast to GUI dashboards over gRPC - see
            // docs/router/telemetry.md, docs/router/grpc-migration.md, and
            // Telemetry/TelemetryBroadcaster.cs for the outer/inner DI-container bridging this
            // singleton is responsible for).
            services.AddSingleton<ISessionIdResolver, SessionIdResolver>();
            services.AddSingleton<IConversationContinuityMatcher, MessageHistoryContinuityMatcher>();
            services.AddSingleton<IConversationTurnTracker, ConversationTurnTracker>();
            services.AddSingleton<IUsageExtractor, UsageExtractor>();
            services.AddSingleton<IResponseTextExtractor, ResponseTextExtractor>();

            // Per-provider payload translators (PLAN.md's "Unified API Translation" pillar). Each
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

            // Personal-scale running spend total (PLAN.md's "Basic Token/Cost Tracking" parity
            // pillar) - terminal output via the injected ILogger, plus a local JSON Lines file. See
            // SpendTracker's remarks.
            services.AddOptions<SpendTrackingOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(SpendTrackingOptions.SectionName).Bind(options));
            services.AddSingleton<ISpendTracker, SpendTracker>();

            services.AddSingleton<TelemetryBroadcaster>();
            services.AddSingleton<TelemetryPublisher>();
            services.AddSingleton<ITelemetryPublisher>(sp => sp.GetRequiredService<TelemetryPublisher>());

            // Sandboxed executor (off-path, best-effort). The router-memory observer adapter is registered
            // before AddSandbox so it wins over the library's Null default (which uses TryAdd). Live scores
            // are written under a separate dimension namespace - see RouterMemoryScoreObserver.
            services.AddSingleton<IRouterScoreObserver, RouterMemoryScoreObserver>();
            services.AddSandbox();

            services.AddSingleton<ProxyMiddleware>();

            // Model price catalog (docs/router/model-price-catalog.md). Shares agent_telemetry.db with the
            // future usage ledger via the top-level Storage section. PriceCatalogOptions is validated by
            // PriceSourceRegistry's constructor (eager, at startup - mirroring ProviderConfigStore), so a
            // bad source name fails before Kestrel binds.
            services.AddOptions<StorageOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(StorageOptions.SectionName).Bind(options));
            services.AddOptions<PriceCatalogOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(PriceCatalogOptions.SectionName).Bind(options));
            services.AddSingleton<PriceCatalogDatabase>();
            // D3 alias resolver (docs/router/d3-alias-resolution.md): maps each source's own model/provider
            // naming onto the configured router identity at ingest, so cost resolves on the client-facing
            // ModelName. Registered so the container injects it into PriceCatalogRepository's optional param.
            services.AddSingleton<IModelIdentityResolver, ConfigModelIdentityResolver>();
            services.AddSingleton<PriceCatalogRepository>();
            // Request-path price lookup (PLAN.md "Basic Token/Cost Tracking", cost half): ProxyMiddleware
            // estimates each paid request's cost from the catalog through this seam. Registered so the
            // container injects it into ProxyMiddleware's optional priceLookup constructor parameter.
            services.AddSingleton<IModelPriceLookup, PriceCatalogModelPriceLookup>();
            // Owns aggregator_sources.enabled (D6). Starts empty and is populated by
            // StartupHealthCheckHostedService once the schema exists - see PriceSourceToggleStore's remarks.
            services.AddSingleton<PriceSourceToggleStore>();
            // Owns per-provider monthly budgets + spend (Governance > Providers). Same empty-until-schema-ready
            // lifecycle as the toggle store: StartupHealthCheckHostedService calls Reload after EnsureCreated.
            // Injected into ProxyMiddleware's optional budgetStore param (enforcement + spend recording) and
            // passed across to the inner host for the /admin budget endpoints.
            services.AddSingleton<ProviderBudgetStore>();
            // ProxyMiddleware depends only on the enforce+record slice (IBudgetEnforcer); map it to the same
            // singleton so the request path and the admin/cap surface share one store and one snapshot.
            services.AddSingleton<IBudgetEnforcer>(sp => sp.GetRequiredService<ProviderBudgetStore>());
            // Captures upstream anthropic-ratelimit-* response headers (docs/router/anthropic-reported-usage-plan.md
            // §5) into the same price-catalog database. Injected into ProxyMiddleware's optional
            // rateLimitCapture constructor parameter.
            services.AddSingleton<IRateLimitHeaderCapture, RateLimitHeaderCapture>();
            // Per-(provider, model) tool-call dialect capabilities (docs/router/tool-call-normalization.md
            // Phase 1). Shares agent_telemetry.db with the price catalog, so it has the same
            // empty-until-schema-ready lifecycle as the two stores above: StartupHealthCheckHostedService
            // calls Reload after EnsureCreated. The request path takes the narrow read/record slice
            // (IToolCallCapabilityStore) mapped to the same singleton, so both see one snapshot.
            services.AddSingleton<ToolCallCapabilityRepository>();
            services.AddSingleton<ToolCallCapabilityStore>();
            services.AddSingleton<IToolCallCapabilityStore>(sp => sp.GetRequiredService<ToolCallCapabilityStore>());
            services.AddSingleton<PriceSourceRegistry>();
            services.AddSingleton<IPriceSourceRegistry>(sp => sp.GetRequiredService<PriceSourceRegistry>());
            services.AddSingleton<PriceCatalogIngestionService>();

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
            // ManagementFacade's constructor resolves PriceCatalogRepository automatically (registered
            // above) into its optional priceCatalogRepository parameter, so the Anthropic Usage card's
            // rate-limit snapshot is available on this MCP-facing facade the same way it is on the REST one
            // ProxyHostedService builds below.
            services.AddSingleton<ManagementFacade>();

            // MCP (Model Context Protocol) management endpoint - agent-facing access to the same
            // provider/model/budget/price-source management as REST /admin/*, over a dedicated loopback TLS
            // port. See docs/router/mcp-endpoint-plan.md.
            services.AddOptions<McpOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(McpOptions.SectionName).Bind(options));
            services.AddHostedService<McpHostedService>();

            // Hosted-service order matters: the generic host awaits each StartAsync in registration order,
            // so the startup checks (which pull the first pricing cycle) run to completion before
            // ProxyHostedService binds Kestrel below. The background poll loop is registered between them;
            // it does not run its own initial cycle.
            services.AddHostedService<StartupHealthCheckHostedService>();
            services.AddHostedService<PriceCatalogIngestionHostedService>();

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
                    telemetryBroadcaster: sp.GetRequiredService<TelemetryBroadcaster>(),
                    providerConfigStore: sp.GetRequiredService<IProviderConfigStore>(),
                    environment: sp.GetRequiredService<IEnvironmentVariableProvider>(),
                    managementToken: ManagementAccessToken.GetOrCreate(),
                    priceSourceToggleStore: sp.GetRequiredService<PriceSourceToggleStore>(),
                    priceCatalogIngestionService: sp.GetRequiredService<PriceCatalogIngestionService>(),
                    priceCatalogOptions: sp.GetRequiredService<IOptions<PriceCatalogOptions>>().Value,
                    providerBudgetStore: sp.GetRequiredService<ProviderBudgetStore>(),
                    // Passed across for the same reason as the price-catalog singletons: the inner host has
                    // its own container, so the REST facade it builds cannot resolve these from this one.
                    endpointScanner: sp.GetRequiredService<ProviderEndpointScanner>(),
                    toolCallCapabilityStore: sp.GetRequiredService<ToolCallCapabilityStore>(),
                    priceCatalogRepository: sp.GetRequiredService<PriceCatalogRepository>());
            });

            return services;
        }
    }
}

