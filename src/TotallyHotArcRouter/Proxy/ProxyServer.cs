using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TotallyHot.ArcRouter.Proxy
{
    /// <summary>
    /// Represents the proxy server, responsible for building and managing the Kestrel web host.
    /// </summary>
    public class ProxyServer : IAsyncDisposable, IDisposable
    {
        private readonly IHost _host;

        // Non-null only when this server created its own management HttpClient (no caller-supplied one), so
        // disposal frees exactly what this server owns and never a client the caller still uses elsewhere.
        private readonly HttpClient? _ownedManagementHttpClient;

        /// <summary>Default port for the TLS-secured gRPC telemetry endpoint. See the constructor's <c>grpcPort</c> remarks.</summary>
        public const int DefaultGrpcPort = 5002;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProxyServer"/> class.
        /// </summary>
        /// <param name="logger">The logger for this instance (currently unused by Kestrel wiring, reserved for future diagnostics).</param>
        /// <param name="proxyMiddleware">
        /// The already-constructed middleware instance used to handle every request. Passed directly, rather than
        /// copying the application's DI container into the inner host, so the inner host can never end up with its
        /// own copy of application-level hosted service registrations (which previously caused unbounded recursive
        /// construction of <see cref="TotallyHot.ArcRouter.Hosting.ProxyHostedService"/>).
        /// </param>
        /// <param name="port">
        /// The localhost port Kestrel listens on for plain HTTP/1.1 LLM-forwarding traffic. Defaults to 5001.
        /// Pass 0 to bind an ephemeral port (useful in tests to avoid flaking when the default port is already in
        /// use); the resolved address is available via <see cref="Addresses"/> once <see cref="StartAsync"/>
        /// completes.
        /// </param>
        /// <param name="telemetryBroadcaster">
        /// The outer application's <see cref="TelemetryBroadcaster"/> singleton, registered into this inner host's
        /// own DI container so <see cref="TelemetryGrpcService"/> can be constructed with it. Optional and defaults
        /// to a fresh, private instance so existing callers/tests that construct a plain proxy-forwarding server,
        /// with nothing subscribed to it, are unaffected; the gRPC endpoint always exists either way, it just has
        /// nothing to broadcast when nobody supplied a shared broadcaster.
        /// </param>
        /// <param name="grpcPort">
        /// A second, dedicated localhost port for the TLS-secured gRPC telemetry endpoint (<see cref="DefaultGrpcPort"/>
        /// by default). Deliberately a separate port from <paramref name="port"/>, not a second protocol sharing the
        /// same port: <paramref name="port"/> must stay plain, unencrypted HTTP/1.1 for existing LLM-forwarding
        /// clients that already connect to it that way, so it cannot also become an HTTPS/2 endpoint. Pass 0 to bind
        /// an ephemeral port, mirroring <paramref name="port"/>'s test-friendly behavior.
        /// </param>
        /// <param name="providerConfigStore">
        /// Optional writable provider/model configuration store. When supplied, the <c>/admin/*</c>
        /// management REST API (see <see cref="ProviderAdminEndpoints"/>) is mapped onto the plain-HTTP
        /// <paramref name="port"/> so the Governance UI can add/remove/edit providers, credentials, and
        /// models with the running router reloading live. Defaults to <see langword="null"/> (management
        /// API disabled), so existing callers/tests that construct a plain forwarding server are unaffected.
        /// </param>
        /// <param name="environment">
        /// Optional environment-variable accessor used to resolve provider credentials for the management
        /// API's model-discovery endpoint. Defaults to a real <see cref="EnvironmentVariableProvider"/>.
        /// </param>
        /// <param name="managementHttpClient">
        /// Optional HTTP client used by the management API to query a provider's live model list. Defaults
        /// to a fresh <see cref="HttpClient"/> owned by this server.
        /// </param>
        /// <param name="managementToken">
        /// Optional shared secret required (in the <c>X-Admin-Token</c> header) on every <c>/admin/*</c>
        /// request when set, and - via <see cref="TelemetryAuthInterceptor"/> - on every call to
        /// the TLS <paramref name="grpcPort"/> gRPC endpoint (the telemetry stream and price-source admin
        /// service alike). Defaults to <see langword="null"/> (no inbound auth) - production wiring
        /// (<see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions"/>) always passes the
        /// <see cref="Management.ManagementAccessToken"/> value here, so both surfaces are gated by
        /// default; a caller passing <see langword="null"/> explicitly opts out (e.g. a test exercising
        /// forwarding only).
        /// </param>
        /// <param name="priceSourceToggleStore">
        /// Optional price-source toggle store. When supplied together with <paramref name="priceCatalogIngestionService"/>,
        /// <see cref="PriceSourceAdminGrpcService"/> is mapped onto the TLS <paramref name="grpcPort"/> so the
        /// Governance UI can enable/disable price feeds and pull on demand. Defaults to <see langword="null"/>
        /// (panel API absent), so existing callers/tests that construct a plain forwarding server are unaffected.
        /// </param>
        /// <param name="priceCatalogIngestionService">
        /// Optional ingestion service backing the panel's "Pull Now" action. Required alongside
        /// <paramref name="priceSourceToggleStore"/>; supplying only one of the pair leaves the API unmapped.
        /// </param>
        /// <param name="priceCatalogOptions">
        /// Optional price catalog configuration. Only its <c>PollIntervalHours</c> is used here, to report the
        /// poll cadence to the panel's countdown. Defaults to <see cref="PriceCatalogOptions"/>'s own defaults,
        /// so a caller that maps the panel API without passing config still reports a correct cadence unless it
        /// had overridden the interval.
        /// </param>
        /// <param name="providerBudgetStore">
        /// Optional per-provider budget store. When supplied alongside <paramref name="providerConfigStore"/>,
        /// the <c>/admin</c> API returns each provider's caps and current-month spend and accepts budget
        /// edits (<c>PUT /admin/providers/{key}/budget</c>). Defaults to <see langword="null"/>, in which case
        /// providers report no caps/spend and budget edits are unavailable.
        /// </param>
        /// <param name="endpointScanner">
        /// Optional prober for which API flavors a provider's endpoint answers
        /// (<c>docs/router/tool-call-normalization.md</c> §3.3). Supplied together with
        /// <paramref name="toolCallCapabilityStore"/>, it makes <c>POST /admin/providers/{key}/scan-capabilities</c>
        /// available and lets a provider save refresh its capability record. Either being
        /// <see langword="null"/> leaves capability scanning unavailable.
        /// </param>
        /// <param name="toolCallCapabilityStore">
        /// Optional store the scan results are persisted to. See <paramref name="endpointScanner"/>.
        /// </param>
        /// <param name="priceCatalogRepository">
        /// Optional price-catalog repository, passed to the management facade so a provider's captured
        /// <c>anthropic-ratelimit-*</c> header snapshot can be returned from <c>GET /admin/providers</c>
        /// (<c>docs/router/anthropic-reported-usage-plan.md</c> §5). Defaults to <see langword="null"/>, in
        /// which case every provider reports no rate-limit data.
        /// </param>
        /// <param name="modelAliasOverrideStore">
        /// Optional operator price-override store, passed to the management facade so
        /// <c>PUT/DELETE /admin/price-overrides</c> (§5.7) is available. Defaults to <see langword="null"/>,
        /// in which case those endpoints answer <see cref="Management.ManagementErrorType.Unavailable"/>.
        /// </param>
        /// <param name="usageRollupStore">
        /// Optional Phase 4 rollup store, passed to the management facade so <c>GET /admin/usage/summary</c>
        /// and <c>GET /admin/usage/rollup</c> (§5.15) are available. Defaults to <see langword="null"/>, in
        /// which case both answer <see cref="Management.ManagementErrorType.Unavailable"/>.
        /// </param>
        /// <param name="secretWriter">
        /// Optional writer for the protected secret store, passed to the management facade so a locked
        /// literal header is stored there instead of in <c>model-routing.json</c>
        /// (<c>docs/router/secrets-at-rest-plan.md</c> §3). Defaults to <see langword="null"/>, in which case
        /// a locked literal is stored in configuration exactly as before the store existed.
        /// </param>
        /// <param name="secretReader">
        /// Optional reader for the protected secret store, passed to the management facade so
        /// <c>POST /admin/providers/{key}/discover-models</c> can still authenticate a provider whose
        /// credential lives in the store. Defaults to <see langword="null"/>.
        /// </param>
        /// <param name="benchmarkDataStatusService">
        /// Optional CodeRouterBench freshness cache. When supplied together with
        /// <paramref name="benchmarkFileLedger"/> and <paramref name="benchmarkSyncService"/>,
        /// <see cref="CodeRouterBench.BenchmarkDataAdminGrpcService"/> is mapped onto the TLS
        /// <paramref name="grpcPort"/> so the Governance UI's Benchmark Data panel can read the corpus's
        /// sync state, recheck it, and run a sync. Defaults to <see langword="null"/> (panel API absent).
        /// </param>
        /// <param name="benchmarkFileLedger">Optional per-file sync ledger. See <paramref name="benchmarkDataStatusService"/>.</param>
        /// <param name="benchmarkSyncService">Optional sync service backing the panel's sync action. See <paramref name="benchmarkDataStatusService"/>.</param>
        /// <param name="benchmarkSyncOptions">
        /// Optional CodeRouterBench sync configuration. Only its <c>DatasetRef</c> is used here, to drive
        /// the panel's sync action. Defaults to <see cref="CodeRouterBench.BenchmarkSyncOptions"/>'s own defaults.
        /// </param>
        /// <param name="llmRouterModelOverrideStore">
        /// Optional llm_router active-model store. When supplied together with
        /// <paramref name="llmRouterModelSyncService"/>,
        /// <see cref="Router.TextGeneration.LlmRouterModelAdminGrpcService"/> is mapped onto the TLS
        /// <paramref name="grpcPort"/> so the Governance UI's Benchmark Data panel's "Local Voter Model"
        /// section can read the voter's file sync state, switch models, and run a sync. Defaults to
        /// <see langword="null"/> (panel API absent).
        /// </param>
        /// <param name="llmRouterModelSyncService">Optional sync service backing the panel's sync action. See <paramref name="llmRouterModelOverrideStore"/>.</param>
        /// <param name="routingOptions">
        /// Optional routing configuration backing <see cref="Router.RoutingModeAdminGrpcService"/>, which the
        /// Governance UI's Routing Mode panel reads for whether the Orchestrator is live, its voters'
        /// enablement/weight, and the exploration setting (docs/router/orchestrator-live-path-plan.md §M3.2).
        /// Unlike the optional feature stores above, that service is mapped onto the TLS
        /// <paramref name="grpcPort"/> <em>unconditionally</em> - routing configuration is core, not an add-on
        /// that can be absent - so this parameter changes only the values reported, never whether the panel
        /// API exists. Defaults to <see langword="null"/>, in which case <see cref="RoutingOptions"/>'s own
        /// coded defaults are reported.
        /// </param>
        /// <param name="taxonomyComparisonStore">
        /// Backs <c>GET /admin/usage/routing-roi</c>, the Cost Analytics "Routing ROI" feed
        /// (docs/router/self-organizing-classification-plan.md Phase T4). Defaults to
        /// <see langword="null"/>, in which case that endpoint answers
        /// <see cref="Management.ManagementErrorType.Unavailable"/> rather than an empty history.
        /// </param>
        /// <param name="clusterTrainingService">
        /// Optional cluster-model training service. When supplied together with
        /// <paramref name="memoryEntryStore"/>, <paramref name="transcriptStore"/>,
        /// <paramref name="transcriptOptions"/>, and <paramref name="storageOptions"/>,
        /// <see cref="Router.Orchestrator.ClusterModelAdminGrpcService"/> is mapped onto the TLS
        /// <paramref name="grpcPort"/> so the Governance UI's Cluster Model panel can read the trained
        /// artifact's status and run a retrain (docs/router/self-organizing-classification-plan.md Phase
        /// T5). Defaults to <see langword="null"/> (panel API absent).
        /// </param>
        /// <param name="memoryEntryStore">Optional live memory entry store. See <paramref name="clusterTrainingService"/>.</param>
        /// <param name="transcriptStore">Optional transcript store. See <paramref name="clusterTrainingService"/>.</param>
        /// <param name="transcriptOptions">Optional transcript retention configuration. See <paramref name="clusterTrainingService"/>.</param>
        /// <param name="storageOptions">Optional storage configuration naming the cluster model artifact's path. See <paramref name="clusterTrainingService"/>.</param>
        /// <param name="routerSettingsStore">
        /// Optional mutable settings store (docs/router/self-organizing-classification-plan.md Phase T6).
        /// When supplied, <see cref="Router.RouterSettingsAdminGrpcService"/> is mapped onto the TLS
        /// <paramref name="grpcPort"/> so the Governance UI's System Settings window can read and mutate
        /// the adaptive-routing toggle and embedding-memory sample size. Defaults to
        /// <see langword="null"/> (panel API absent).
        /// </param>
        /// <param name="routerSettingsReloadToken">
        /// Optional live-reload trigger, signaled after a successful save so
        /// <c>IOptionsMonitor&lt;RoutingOptions&gt;</c> recomputes immediately. Required alongside
        /// <paramref name="routerSettingsStore"/> for the panel API to be mapped.
        /// </param>
        /// <param name="routingOptionsMonitor">
        /// Optional live routing-options monitor, reported by <c>GetRouterSettings</c>/
        /// <c>UpdateRouterSettings</c> as the currently effective values. Required alongside
        /// <paramref name="routerSettingsStore"/> for the panel API to be mapped.
        /// </param>
        /// <param name="embeddingMemory">
        /// Optional embedding-memory working set, trimmed synchronously by <c>UpdateRouterSettings</c>
        /// when a save lowers the capacity, so the response reflects the trim rather than racing it. Not
        /// required for the panel API to be mapped - when omitted, the reactive
        /// <c>IOptionsMonitor.OnChange</c> trim still runs, just on its own schedule - and registered
        /// independently of <paramref name="routerSettingsStore"/>'s group, so supplying it alone is never
        /// silently dropped.
        /// </param>
        public ProxyServer(ILogger<ProxyServer> logger, ProxyMiddleware proxyMiddleware, int port = 5001, TelemetryBroadcaster? telemetryBroadcaster = null, int grpcPort = DefaultGrpcPort, IProviderConfigStore? providerConfigStore = null, IEnvironmentVariableProvider? environment = null, HttpClient? managementHttpClient = null, string? managementToken = null, PriceSourceToggleStore? priceSourceToggleStore = null, PriceCatalogIngestionService? priceCatalogIngestionService = null, PriceCatalogOptions? priceCatalogOptions = null, ProviderBudgetStore? providerBudgetStore = null, ProviderEndpointScanner? endpointScanner = null, ToolCallCapabilityStore? toolCallCapabilityStore = null, PriceCatalogRepository? priceCatalogRepository = null, ModelAliasOverrideStore? modelAliasOverrideStore = null, IUsageRollupStore? usageRollupStore = null, ISecretWriter? secretWriter = null, ISecretReader? secretReader = null, CodeRouterBench.BenchmarkDataStatusService? benchmarkDataStatusService = null, CodeRouterBench.BenchmarkFileLedger? benchmarkFileLedger = null, CodeRouterBench.BenchmarkSyncService? benchmarkSyncService = null, CodeRouterBench.BenchmarkSyncOptions? benchmarkSyncOptions = null, Router.TextGeneration.ILlmRouterModelOverrideStore? llmRouterModelOverrideStore = null, Router.TextGeneration.LlmRouterModelSyncService? llmRouterModelSyncService = null, IOptions<RoutingOptions>? routingOptions = null, Transcripts.ITaxonomyComparisonStore? taxonomyComparisonStore = null, Router.Orchestrator.IClusterTrainingService? clusterTrainingService = null, Router.IMemoryEntryStore? memoryEntryStore = null, Transcripts.ITranscriptStore? transcriptStore = null, IOptions<Transcripts.TranscriptOptions>? transcriptOptions = null, IOptions<PriceCatalog.StorageOptions>? storageOptions = null, Router.RouterSettingsStore? routerSettingsStore = null, Router.RouterSettingsReloadToken? routerSettingsReloadToken = null, IOptionsMonitor<RoutingOptions>? routingOptionsMonitor = null, Router.EmbeddingMemory? embeddingMemory = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(proxyMiddleware);
            ArgumentOutOfRangeException.ThrowIfNegative(port);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            ArgumentOutOfRangeException.ThrowIfNegative(grpcPort);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(grpcPort, 65535);

            var broadcaster = telemetryBroadcaster ?? new TelemetryBroadcaster();

            // Own (and later dispose) the management client only when the caller didn't supply one.
            _ownedManagementHttpClient = managementHttpClient is null ? new HttpClient() : null;
            var managementClient = managementHttpClient ?? _ownedManagementHttpClient!;

            // One expression per admin service, evaluated once here and used by both the service
            // registration (ConfigureServices) and the endpoint mapping (UseEndpoints) below. Each of these
            // conditions used to be written out twice, ~60-120 lines apart, with nothing keeping the two
            // copies in sync: editing one and not the other yields a service that is mapped but whose
            // dependencies were never registered, which fails on the first RPC call rather than at startup
            // (MapGrpcService only reflects over the service type - it never constructs it), and no test
            // calls these RPCs, so the suite would stay green through exactly that regression.
            var mapPriceSourceAdmin = priceSourceToggleStore is not null && priceCatalogIngestionService is not null;
            var mapBenchmarkDataAdmin = benchmarkDataStatusService is not null && benchmarkFileLedger is not null && benchmarkSyncService is not null;
            var mapLlmRouterModelAdmin = llmRouterModelOverrideStore is not null && llmRouterModelSyncService is not null;
            var mapClusterModelAdmin = clusterTrainingService is not null && memoryEntryStore is not null
                && transcriptStore is not null && transcriptOptions is not null && storageOptions is not null;
            var mapRouterSettingsAdmin = routerSettingsStore is not null && routerSettingsReloadToken is not null
                && routingOptionsMonitor is not null;

            _host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel(options =>
                    {
                        // Plain HTTP/1.1 only - this port is exclusively LLM-forwarding proxy traffic and
                        // /v1/models, both HTTP/1.1 clients today. gRPC no longer shares this port (see grpcPort
                        // below) - see docs/router/grpc-migration.md's "Transport" section for why: unencrypted
                        // HTTP/2 (h2c) turned out to be unreliable on at least one managed/corporate machine (every
                        // connection failed with the HTTP/2-level HTTP_1_1_REQUIRED error, consistent with
                        // something on the network path not understanding or mangling the h2c preface), so the
                        // gRPC endpoint moved to its own dedicated TLS port instead of trying to fix h2c itself.
                        if (port == 0)
                        {
                            // ListenLocalhost throws for port 0. Bind a single IPv4 loopback address instead of
                            // dual-stack, since binding IPv4 and IPv6 separately for an ephemeral port would
                            // assign two different port numbers.
                            options.Listen(IPAddress.Loopback, port);
                        }
                        else
                        {
                            // Preserve dual-stack (IPv4 + IPv6) localhost binding for fixed ports.
                            options.ListenLocalhost(port);
                        }

                        // The dedicated TLS/gRPC endpoint. HTTP/2 is negotiated via standard TLS ALPN here, not
                        // h2c prior-knowledge - the whole point of this port existing is to avoid the h2c
                        // reliability problem above. TelemetryTlsCertificate persists a self-signed cert per
                        // machine/user so the client doesn't need to re-trust a new one on every proxy restart.
                        // Certificate initialization is non-essential (telemetry is not critical to proxy operation),
                        // so catch any exceptions and skip binding the gRPC port if the cert fails to load/generate.
                        try
                        {
                            var certificate = TelemetryTlsCertificate.GetOrCreate();
                            if (grpcPort == 0)
                            {
                                options.Listen(IPAddress.Loopback, grpcPort, listenOptions =>
                                {
                                    listenOptions.Protocols = HttpProtocols.Http2;
                                    listenOptions.UseHttps(certificate);
                                });
                            }
                            else
                            {
                                options.ListenLocalhost(grpcPort, listenOptions =>
                                {
                                    listenOptions.Protocols = HttpProtocols.Http2;
                                    listenOptions.UseHttps(certificate);
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to initialize telemetry gRPC listener (certificate generation/load failed). Telemetry will be unavailable.");
                        }
                    });

                    // gRPC and the shared broadcaster are registered into this inner host's own DI container
                    // (deliberately separate from the outer application container - see the constructor remarks
                    // above), not the outer one.
                    webBuilder.ConfigureServices(services =>
                    {
                        // Gate every gRPC call (telemetry stream and price-source admin alike) behind the
                        // same shared management token the REST /admin/* API and MCP endpoint require -
                        // see TelemetryAuthInterceptor's remarks. Only registered when a token is
                        // configured, mirroring managementToken's REST/MCP gating: a null token means "no
                        // inbound auth", used by tests exercising forwarding only.
                        if (!string.IsNullOrWhiteSpace(managementToken))
                        {
                            services.AddSingleton(new TelemetryAuthInterceptor(managementToken));
                            services.AddGrpc(options => options.Interceptors.Add<TelemetryAuthInterceptor>());
                        }
                        else
                        {
                            services.AddGrpc();
                        }

                        services.AddSingleton(broadcaster);

                        // Same reasoning as the broadcaster: the price catalog singletons live in the outer
                        // container, so PriceSourceAdminGrpcService can only be constructed here if they are
                        // handed across explicitly. Registered as a pair - the service needs both.
                        if (mapPriceSourceAdmin)
                        {
                            services.AddSingleton(priceSourceToggleStore!);
                            services.AddSingleton(priceCatalogIngestionService!);

                            // The panel's countdown needs the poll cadence, and this inner container has no
                            // configuration bound into it - AddGrpc alone would leave the IOptions dependency
                            // unresolvable and fail on the first call, not at startup.
                            services.AddSingleton(Options.Create(priceCatalogOptions ?? new PriceCatalogOptions()));
                        }

                        // Same reasoning again: the CodeRouterBench singletons live in the outer container,
                        // so BenchmarkDataAdminGrpcService can only be constructed here if they are handed
                        // across explicitly. Registered as a trio - the service needs all three.
                        if (mapBenchmarkDataAdmin)
                        {
                            services.AddSingleton(benchmarkDataStatusService!);
                            services.AddSingleton(benchmarkFileLedger!);
                            services.AddSingleton(benchmarkSyncService!);
                            services.AddSingleton(Options.Create(benchmarkSyncOptions ?? new CodeRouterBench.BenchmarkSyncOptions()));
                        }

                        // Same reasoning again: the llm_router model override store and sync service live
                        // in the outer container, so LlmRouterModelAdminGrpcService can only be
                        // constructed here if they are handed across explicitly.
                        if (mapLlmRouterModelAdmin)
                        {
                            services.AddSingleton(llmRouterModelOverrideStore!);
                            services.AddSingleton(llmRouterModelSyncService!);
                        }

                        // Unlike the pairs above, RoutingModeAdminGrpcService's dependency is core
                        // configuration rather than an optional feature store, so it defaults to the
                        // caller's own RoutingOptions defaults instead of leaving the service unmapped.
                        services.AddSingleton(routingOptions ?? Options.Create(new RoutingOptions()));

                        // Same reasoning again: the cluster training service and its supporting stores live
                        // in the outer container, so ClusterModelAdminGrpcService can only be constructed
                        // here if they are handed across explicitly. Registered as a group of five - the
                        // service needs all of them.
                        if (mapClusterModelAdmin)
                        {
                            services.AddSingleton(clusterTrainingService!);
                            services.AddSingleton(memoryEntryStore!);
                            services.AddSingleton(transcriptStore!);
                            services.AddSingleton(transcriptOptions!);
                            services.AddSingleton(storageOptions!);
                        }

                        // The Governance UI's System Settings panel API (Phase T6). Same reasoning again:
                        // the settings store, reload token, and options monitor live in the outer
                        // container, so RouterSettingsAdminGrpcService can only be constructed here if
                        // they are handed across explicitly.
                        if (mapRouterSettingsAdmin)
                        {
                            services.AddSingleton(routerSettingsStore!);
                            services.AddSingleton(routerSettingsReloadToken!);
                            services.AddSingleton(routingOptionsMonitor!);
                        }

                        // Registered independently of the gate above rather than nested inside it, matching
                        // what this parameter's own documentation promises: it is optional even when the
                        // rest of the group is present, so a caller supplying it must never have it
                        // silently dropped. Its only consumer today is RouterSettingsAdminGrpcService, which
                        // takes it as an optional constructor parameter and copes with it being absent.
                        if (embeddingMemory is not null)
                        {
                            services.AddSingleton(embeddingMemory);
                        }
                    });

                    webBuilder.Configure(app =>
                    {
                        // UseRouting + mapped endpoints handle only requests matching the
                        // TelemetryService.StreamEvents RPC or (when a store is supplied) the /admin/*
                        // management API; every other request - which is all real LLM API traffic - falls
                        // through unmatched to the terminal app.Run below, completely unchanged from before
                        // these endpoints existed.
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGrpcService<TelemetryGrpcService>();

                            // The Governance UI's price-source panel API. Shares the TLS gRPC port with the
                            // telemetry stream - price data itself never crosses it (D5); this carries only
                            // feed metadata and the toggle/refresh commands.
                            if (mapPriceSourceAdmin)
                            {
                                endpoints.MapGrpcService<PriceSourceAdminGrpcService>();
                            }

                            // The Governance UI's Benchmark Data panel API. Shares the TLS gRPC port with
                            // the telemetry stream and price-source admin service.
                            if (mapBenchmarkDataAdmin)
                            {
                                endpoints.MapGrpcService<CodeRouterBench.BenchmarkDataAdminGrpcService>();
                            }

                            // The Governance UI's Benchmark Data panel's "Local Voter Model" section API.
                            // Shares the TLS gRPC port with the telemetry stream and the other admin services.
                            if (mapLlmRouterModelAdmin)
                            {
                                endpoints.MapGrpcService<Router.TextGeneration.LlmRouterModelAdminGrpcService>();
                            }

                            // The Governance UI's Routing Mode panel API. Shares the TLS gRPC port with the
                            // telemetry stream and the other admin services. Always mapped - see the
                            // routingOptions registration above.
                            endpoints.MapGrpcService<Router.RoutingModeAdminGrpcService>();

                            // The Governance UI's Cluster Model panel API (Phase T5). Shares the TLS gRPC
                            // port with the telemetry stream and the other admin services.
                            if (mapClusterModelAdmin)
                            {
                                endpoints.MapGrpcService<Router.Orchestrator.ClusterModelAdminGrpcService>();
                            }

                            // The Governance UI's System Settings panel API (Phase T6). Shares the TLS
                            // gRPC port with the telemetry stream and the other admin services.
                            if (mapRouterSettingsAdmin)
                            {
                                endpoints.MapGrpcService<Router.RouterSettingsAdminGrpcService>();
                            }

                            // The Governance UI's provider/credential/model management API. Only mapped
                            // when a writable store is supplied; shares this plain-HTTP loopback port with
                            // LLM forwarding (real traffic never targets /admin, so it's never intercepted).
                            // The facade is the same shared core the MCP endpoint's provider tools use -
                            // projection, merging, and credential/header masking live there, not here.
                            if (providerConfigStore is not null)
                            {
                                var facade = new ManagementFacade(
                                    providerConfigStore,
                                    environment ?? new EnvironmentVariableProvider(),
                                    managementClient,
                                    providerBudgetStore,
                                    endpointScanner,
                                    toolCallCapabilityStore,
                                    priceCatalogRepository,
                                    modelAliasOverrideStore,
                                    usageRollupStore,
                                    secretWriter: secretWriter,
                                    secretReader: secretReader,
                                    comparisonStore: taxonomyComparisonStore);
                                endpoints.MapProviderAdminEndpoints(facade, managementToken);
                                endpoints.MapUsageAdminEndpoints(facade, managementToken);
                            }
                        });
                        app.Run(context => proxyMiddleware.InvokeAsync(context, _ => Task.CompletedTask));
                    });
                })
                .Build();
        }

        /// <summary>
        /// Gets the addresses Kestrel is actually listening on. Only meaningful after <see cref="StartAsync"/> completes.
        /// </summary>
        public IReadOnlyCollection<string> Addresses
        {
            get
            {
                var addresses = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
                return addresses is null ? [] : new List<string>(addresses);
            }
        }

        /// <summary>
        /// Starts the proxy server. Unlike the SignalR-era implementation, no post-start attachment
        /// step is needed: the constructor already registered the shared <see cref="TelemetryBroadcaster"/>
        /// into this host's DI container, so <see cref="TelemetryGrpcService"/> can receive it as soon
        /// as the first <c>StreamEvents</c> call arrives.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return _host.StartAsync(cancellationToken);
        }

        /// <summary>
        /// Stops the proxy server.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return _host.StopAsync(cancellationToken);
        }

        /// <summary>
        /// Disposes the inner host and, when this server created it, the management <see cref="HttpClient"/>,
        /// so repeatedly creating and discarding servers (e.g. across tests) doesn't leak hosts or handlers.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _host.Dispose();
            }

            _ownedManagementHttpClient?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _host.Dispose();
            _ownedManagementHttpClient?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

