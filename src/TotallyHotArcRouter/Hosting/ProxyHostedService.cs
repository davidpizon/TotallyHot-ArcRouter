using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using System.Threading.Tasks;

namespace TotallyHot.ArcRouter.Hosting
{
    /// <summary>
    /// A hosted service that manages the lifecycle of the proxy server.
    /// </summary>
    public class ProxyHostedService : IHostedService
    {
        private readonly ILogger<ProxyHostedService> _logger;
        private readonly ProxyServer _proxyServer;

        /// <summary>
        /// Constructs the underlying <see cref="ProxyServer"/> from its dependencies. Optional
        /// parameters (telemetry, provider config store, environment, management HTTP client/token, price
        /// catalog services, the protected secret store's reader/writer, CodeRouterBench corpus services,
        /// the llm_router model override store and sync service, routing configuration) let callers omit
        /// pieces they don't need wired up, mirroring <see cref="ProxyServer"/>'s own constructor.
        /// </summary>
        public ProxyHostedService(
            ILogger<ProxyHostedService> logger,
            ILogger<ProxyServer> proxyLogger,
            ProxyMiddleware proxyMiddleware,
            int port = 5001,
            TelemetryBroadcaster? telemetryBroadcaster = null,
            int grpcPort = ProxyServer.DefaultGrpcPort,
            IProviderConfigStore? providerConfigStore = null,
            IEnvironmentVariableProvider? environment = null,
            HttpClient? managementHttpClient = null,
            string? managementToken = null,
            PriceSourceToggleStore? priceSourceToggleStore = null,
            PriceCatalogIngestionService? priceCatalogIngestionService = null,
            PriceCatalogOptions? priceCatalogOptions = null,
            ProviderBudgetStore? providerBudgetStore = null,
            ProviderEndpointScanner? endpointScanner = null,
            ToolCallCapabilityStore? toolCallCapabilityStore = null,
            PriceCatalogRepository? priceCatalogRepository = null,
            ModelAliasOverrideStore? modelAliasOverrideStore = null,
            IUsageRollupStore? usageRollupStore = null,
            ISecretWriter? secretWriter = null,
            ISecretReader? secretReader = null,
            BenchmarkDataStatusService? benchmarkDataStatusService = null,
            BenchmarkFileLedger? benchmarkFileLedger = null,
            BenchmarkSyncService? benchmarkSyncService = null,
            BenchmarkSyncOptions? benchmarkSyncOptions = null,
            ILlmRouterModelOverrideStore? llmRouterModelOverrideStore = null,
            LlmRouterModelSyncService? llmRouterModelSyncService = null,
            IOptions<RoutingOptions>? routingOptions = null)
        {
            _logger = logger;
            _proxyServer = new ProxyServer(
                proxyLogger,
                proxyMiddleware,
                port,
                telemetryBroadcaster,
                grpcPort,
                providerConfigStore,
                environment,
                managementHttpClient,
                managementToken,
                priceSourceToggleStore,
                priceCatalogIngestionService,
                priceCatalogOptions,
                providerBudgetStore,
                endpointScanner,
                toolCallCapabilityStore,
                priceCatalogRepository,
                modelAliasOverrideStore,
                usageRollupStore,
                secretWriter,
                secretReader,
                benchmarkDataStatusService,
                benchmarkFileLedger,
                benchmarkSyncService,
                benchmarkSyncOptions,
                llmRouterModelOverrideStore,
                llmRouterModelSyncService,
                routingOptions);
        }

        /// <summary>
        /// Gets the addresses the underlying <see cref="ProxyServer"/> is actually listening on. Only meaningful
        /// after <see cref="StartAsync"/> completes.
        /// </summary>
        public System.Collections.Generic.IReadOnlyCollection<string> Addresses => _proxyServer.Addresses;

        /// <summary>
        /// Starts the proxy server.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Proxy Hosted Service is starting.");
            return _proxyServer.StartAsync(cancellationToken);
        }

        /// <summary>
        /// Stops the proxy server.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Proxy Hosted Service is stopping.");
            return _proxyServer.StopAsync(cancellationToken);
        }
    }
}

