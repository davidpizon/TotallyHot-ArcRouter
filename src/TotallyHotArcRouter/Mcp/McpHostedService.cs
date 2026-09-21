using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Serilog;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Mcp;

/// <summary>
/// A hosted service that manages the lifecycle of the MCP management endpoint (<see cref="McpServer"/>),
/// mirroring <see cref="TotallyHot.ArcRouter.Hosting.ProxyHostedService"/>'s role for the proxy. Registered after
/// <c>ProxyHostedService</c> in
/// <see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions.AddTotallyHotArcRouter"/>;
/// registration order isn't load-bearing here the way it is for the startup health check, since MCP has no
/// dependency on Kestrel having bound yet.
/// </summary>
public sealed class McpHostedService : IHostedService, IAsyncDisposable
{
    private readonly BenchmarkDataStatusService _benchmarkDataStatusService;
    private readonly BenchmarkSyncOptions _benchmarkSyncOptions;
    private readonly BenchmarkSyncService _benchmarkSyncService;
    private readonly ILogger<McpHostedService> _logger;
    private readonly ManagementFacade _managementFacade;
    private readonly ILogger<McpServer> _mcpServerLogger;
    private readonly McpOptions _options;
    private readonly PriceCatalogIngestionService _priceCatalogIngestionService;
    private readonly IModelPriceLookup _priceLookup;
    private readonly PriceSourceToggleStore _priceSourceToggleStore;
    private readonly ProviderBudgetStore _providerBudgetStore;
    private readonly ISpendTracker _spendTracker;
    private readonly IManagementTokenProvider _tokenProvider;

    private McpServer? _server;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpHostedService"/> class.
    /// </summary>
    public McpHostedService(
        ILogger<McpHostedService> logger,
        ILogger<McpServer> mcpServerLogger,
        IOptions<McpOptions> options,
        ManagementFacade managementFacade,
        PriceSourceToggleStore priceSourceToggleStore,
        PriceCatalogIngestionService priceCatalogIngestionService,
        IModelPriceLookup priceLookup,
        ProviderBudgetStore providerBudgetStore,
        ISpendTracker spendTracker,
        BenchmarkDataStatusService benchmarkDataStatusService,
        BenchmarkSyncService benchmarkSyncService,
        IOptions<BenchmarkSyncOptions> benchmarkSyncOptions,
        IManagementTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mcpServerLogger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(managementFacade);
        ArgumentNullException.ThrowIfNull(priceSourceToggleStore);
        ArgumentNullException.ThrowIfNull(priceCatalogIngestionService);
        ArgumentNullException.ThrowIfNull(priceLookup);
        ArgumentNullException.ThrowIfNull(providerBudgetStore);
        ArgumentNullException.ThrowIfNull(spendTracker);
        ArgumentNullException.ThrowIfNull(benchmarkDataStatusService);
        ArgumentNullException.ThrowIfNull(benchmarkSyncService);
        ArgumentNullException.ThrowIfNull(benchmarkSyncOptions);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        _logger = logger;
        _mcpServerLogger = mcpServerLogger;
        _options = options.Value;
        _managementFacade = managementFacade;
        _priceSourceToggleStore = priceSourceToggleStore;
        _priceCatalogIngestionService = priceCatalogIngestionService;
        _priceLookup = priceLookup;
        _providerBudgetStore = providerBudgetStore;
        _spendTracker = spendTracker;
        _benchmarkDataStatusService = benchmarkDataStatusService;
        _benchmarkSyncService = benchmarkSyncService;
        _benchmarkSyncOptions = benchmarkSyncOptions.Value;
        _tokenProvider = tokenProvider;
    }

    /// <summary>Disposes the underlying <see cref="McpServer"/>, if it was created.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Generates/loads the shared management token and starts the MCP endpoint, unless
    /// <see cref="McpOptions.Enabled"/> is <see langword="false"/>. Failures are logged and swallowed
    /// (not fatal to the router): MCP is a management convenience, not a dependency of core proxying.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("MCP endpoint disabled (Mcp:Enabled=false); skipping.");
            return;
        }

        try
        {
            _server = new McpServer(
                logger: _mcpServerLogger,
                managementFacade: _managementFacade,
                priceSourceToggleStore: _priceSourceToggleStore,
                priceCatalogIngestionService: _priceCatalogIngestionService,
                priceLookup: _priceLookup,
                providerBudgetStore: _providerBudgetStore,
                spendTracker: _spendTracker,
                benchmarkDataStatusService: _benchmarkDataStatusService,
                benchmarkSyncService: _benchmarkSyncService,
                benchmarkSyncOptions: _benchmarkSyncOptions,
                // The same outer-container IManagementTokenProvider singleton ProxyServiceCollectionExtensions
                // hands into the proxy inner host (see ProxyServerDependencies.ManagementTokenProvider), so
                // MCP and the TLS gRPC/web endpoints share exactly one live token - a Regenerate call from
                // either surface is visible to both without a restart.
                tokenProvider: _tokenProvider,
                port: _options.Port,
                bindAddress: _options.BindAddress,
                // Routes this inner host's own logs through the same Serilog pipeline the rest of the
                // application uses - see McpServer's serilogLogger remarks.
                serilogLogger: Log.Logger);

            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
            // Log the actual bound address(es) rather than the configured port: when Port is 0 (an
            // ephemeral port, which McpServer explicitly supports), the configured value is never the
            // real listening port.
            _logger.LogInformation(message: "MCP endpoint listening on {Addresses}.",
                string.Join(separator: ", ", values: _server.Addresses));
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            // A taken port is an operator condition, not a defect, so it gets the one actionable line
            // Kestrel's own message already carries rather than the exception's several frames of stack -
            // none of which say anything the operator can act on. ex.Message names the address, which is
            // more precise than the configured port (McpOptions.Port may be 0, an ephemeral port).
            _logger.LogWarning(
                message:
                "The MCP endpoint could not start: {Reason} The router continues without it; set Mcp:Port to a free port, or Mcp:Enabled=false to stop trying.",
                ex.Message);
            await DisposeServerAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(exception: ex, message: "Failed to start the MCP endpoint; it will be unavailable.");
            await DisposeServerAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops the MCP endpoint, if it started.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null) await _server.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes a partially-constructed server and clears the field, so a start that got as far as
    /// building the inner host but never bound a listener does not leave that host undisposed for the
    /// life of the process - <see cref="DisposeAsync"/> only ever sees a server that started.
    /// </summary>
    private async Task DisposeServerAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }
}