using System.Net;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Mcp.Tools;
using TotallyHot.ArcRouter.PriceCatalog;
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

namespace TotallyHot.ArcRouter.Mcp;

/// <summary>
/// Hosts the MCP (Model Context Protocol) management endpoint: a small, standalone Kestrel host - mirroring
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>'s dedicated TLS gRPC listener - that exposes
/// <see cref="ProviderMcpTools"/>, <see cref="PriceSourceMcpTools"/>, <see cref="TelemetryMcpTools"/>, and
/// <see cref="BenchmarkDataMcpTools"/>
/// over MCP's Streamable-HTTP transport on its own loopback port. Reuses
/// <see cref="TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate"/>'s self-signed <c>CN=localhost</c>
/// certificate (one trust story for every TLS management port), and gates every request behind
/// <see cref="McpBearerAuthMiddleware"/> using the shared <see cref="ManagementAccessToken"/>.
/// </summary>
/// <remarks>
/// Built as its own generic host with its own DI container, deliberately separate from the outer
/// application container - the same reasoning as <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>'s inner
/// host: the management facade and the price/telemetry stores it needs live in the outer container's
/// singletons, so they are handed across explicitly through this constructor rather than re-resolved here.
/// </remarks>
public sealed class McpServer : IAsyncDisposable, IDisposable
{
    private readonly IHost _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpServer"/> class.
    /// </summary>
    /// <param name="logger">The logger for this host.</param>
    /// <param name="managementFacade">The shared provider/model/budget management facade (also used by REST <c>/admin/*</c>).</param>
    /// <param name="priceSourceToggleStore">The price-source enable/disable/rank store.</param>
    /// <param name="priceCatalogIngestionService">The price-catalog ingestion cycle runner.</param>
    /// <param name="priceLookup">The per-model price lookup.</param>
    /// <param name="providerBudgetStore">The per-provider budget/spend store.</param>
    /// <param name="spendTracker">The process-lifetime running spend tracker.</param>
    /// <param name="benchmarkDataStatusService">The CodeRouterBench corpus freshness cache.</param>
    /// <param name="benchmarkSyncService">The CodeRouterBench corpus sync service.</param>
    /// <param name="benchmarkSyncOptions">The CodeRouterBench sync configuration (its dataset ref).</param>
    /// <param name="accessToken">The bearer token every request must present (see <see cref="ManagementAccessToken"/>).</param>
    /// <param name="port">The loopback TLS port to listen on. Defaults to <c>5003</c>.</param>
    public McpServer(
        ILogger<McpServer> logger,
        ManagementFacade managementFacade,
        PriceSourceToggleStore priceSourceToggleStore,
        PriceCatalogIngestionService priceCatalogIngestionService,
        IModelPriceLookup priceLookup,
        ProviderBudgetStore providerBudgetStore,
        ISpendTracker spendTracker,
        BenchmarkDataStatusService benchmarkDataStatusService,
        BenchmarkSyncService benchmarkSyncService,
        BenchmarkSyncOptions benchmarkSyncOptions,
        string accessToken,
        int port = 5003)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(managementFacade);
        ArgumentNullException.ThrowIfNull(priceSourceToggleStore);
        ArgumentNullException.ThrowIfNull(priceCatalogIngestionService);
        ArgumentNullException.ThrowIfNull(priceLookup);
        ArgumentNullException.ThrowIfNull(providerBudgetStore);
        ArgumentNullException.ThrowIfNull(spendTracker);
        ArgumentNullException.ThrowIfNull(benchmarkDataStatusService);
        ArgumentNullException.ThrowIfNull(benchmarkSyncService);
        ArgumentNullException.ThrowIfNull(benchmarkSyncOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _host = Host.CreateDefaultBuilder()
            // Same reasoning as ProxyServer's identical filter: this inner host is an implementation
            // detail, it never gets Serilog, and its default console provider would otherwise report a
            // bind failure with a full stack alongside McpHostedService's own one-line report of it.
            .ConfigureLogging(logging => logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None))
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel(options =>
                {
                    // A certificate failure here must NOT be swallowed: without a configured listener,
                    // Kestrel falls back to its default URL binding (e.g. http://localhost:5000 or
                    // ASPNETCORE_URLS) instead of simply not listening, which would silently start an
                    // unintended, unauthenticated port rather than making MCP "unavailable" as intended.
                    // Letting this throw fails the whole McpServer construction, which McpHostedService's
                    // own try/catch logs and swallows at the top level - the same "MCP is non-essential,
                    // don't fail the process" posture, just enforced one level up.
                    var certificate = TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate.GetOrCreate();
                    if (port == 0)
                    {
                        options.Listen(IPAddress.Loopback, port, listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                            listenOptions.UseHttps(certificate);
                        });
                    }
                    else
                    {
                        options.ListenLocalhost(port, listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                            listenOptions.UseHttps(certificate);
                        });
                    }
                });

                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(managementFacade);
                    services.AddSingleton(priceSourceToggleStore);
                    services.AddSingleton(priceCatalogIngestionService);
                    services.AddSingleton(priceLookup);
                    services.AddSingleton(providerBudgetStore);
                    services.AddSingleton(spendTracker);
                    services.AddSingleton(benchmarkDataStatusService);
                    services.AddSingleton(benchmarkSyncService);
                    services.AddSingleton(Options.Create(benchmarkSyncOptions));

                    services.AddTransient<ProviderMcpTools>();
                    services.AddTransient<PriceSourceMcpTools>();
                    services.AddTransient<TelemetryMcpTools>();
                    services.AddTransient<BenchmarkDataMcpTools>();

                    services.AddMcpServer()
                        .WithHttpTransport()
                        .WithTools<ProviderMcpTools>()
                        .WithTools<PriceSourceMcpTools>()
                        .WithTools<TelemetryMcpTools>()
                        .WithTools<BenchmarkDataMcpTools>();
                });

                webBuilder.Configure(app =>
                {
                    // Every request - list or mutate - must present the shared token; there is no unauthenticated
                    // route on this host (unlike the plain-HTTP proxy port, this one carries only management
                    // traffic, so there's nothing that needs to fall through ungated).
                    app.UseMiddleware<McpBearerAuthMiddleware>(accessToken);
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapMcp());
                });
            })
            .Build();
    }

    /// <summary>
    /// Gets the addresses Kestrel is actually listening on. Only meaningful after <see cref="StartAsync"/>
    /// completes; empty when the TLS listener failed to initialize (see the constructor's remarks).
    /// </summary>
    public IReadOnlyCollection<string> Addresses
    {
        get
        {
            var addresses = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            return addresses is null ? [] : new List<string>(addresses);
        }
    }

    /// <summary>Starts the MCP endpoint.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => _host.StartAsync(cancellationToken);

    /// <summary>Stops the MCP endpoint.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => _host.StopAsync(cancellationToken);

    /// <summary>Disposes the inner host.</summary>
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

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Dispose() => _host.Dispose();
}

