using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Mcp;

/// <summary>
/// A hosted service that manages the lifecycle of the MCP management endpoint (<see cref="McpServer"/>),
/// mirroring <see cref="TotallyHot.ArcRouter.Hosting.ProxyHostedService"/>'s role for the proxy. Registered after
/// <c>ProxyHostedService</c> in <see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions.AddTotallyHotArcRouter"/>;
/// registration order isn't load-bearing here the way it is for the startup health check, since MCP has no
/// dependency on Kestrel having bound yet.
/// </summary>
public sealed class McpHostedService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<McpHostedService> _logger;
    private readonly ILogger<McpServer> _mcpServerLogger;
    private readonly McpOptions _options;
    private readonly ManagementFacade _managementFacade;
    private readonly PriceSourceToggleStore _priceSourceToggleStore;
    private readonly PriceCatalogIngestionService _priceCatalogIngestionService;
    private readonly IModelPriceLookup _priceLookup;
    private readonly ProviderBudgetStore _providerBudgetStore;
    private readonly ISpendTracker _spendTracker;

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
        ISpendTracker spendTracker)
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

        _logger = logger;
        _mcpServerLogger = mcpServerLogger;
        _options = options.Value;
        _managementFacade = managementFacade;
        _priceSourceToggleStore = priceSourceToggleStore;
        _priceCatalogIngestionService = priceCatalogIngestionService;
        _priceLookup = priceLookup;
        _providerBudgetStore = providerBudgetStore;
        _spendTracker = spendTracker;
    }

    /// <summary>
    /// Gets the addresses the underlying <see cref="McpServer"/> is listening on, or empty when disabled
    /// or not yet started. Only meaningful after <see cref="StartAsync"/> completes.
    /// </summary>
    public IReadOnlyCollection<string> Addresses => _server?.Addresses ?? [];

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
            // No per-surface path override: MCP and REST /admin/* must always resolve to the same file
            // (ManagementAccessToken's default path) so they share exactly one token.
            var accessToken = ManagementAccessToken.GetOrCreate();
            _server = new McpServer(
                _mcpServerLogger,
                _managementFacade,
                _priceSourceToggleStore,
                _priceCatalogIngestionService,
                _priceLookup,
                _providerBudgetStore,
                _spendTracker,
                accessToken,
                _options.Port);

            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
            // Log the actual bound address(es) rather than the configured port: when Port is 0 (an
            // ephemeral port, which McpServer explicitly supports), the configured value is never the
            // real listening port.
            _logger.LogInformation("MCP endpoint listening on {Addresses}.", string.Join(", ", _server.Addresses));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start the MCP endpoint; it will be unavailable.");
            _server = null;
        }
    }

    /// <summary>Stops the MCP endpoint, if it started.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            await _server.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes the underlying <see cref="McpServer"/>, if it was created.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}

