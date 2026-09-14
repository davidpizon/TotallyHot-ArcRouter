using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Serilog.Extensions.Logging;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Mcp.Tools;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;

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
    /// <param name="port">The TLS port to listen on. Defaults to <c>5003</c>.</param>
    /// <param name="bindAddress">
    /// The address <paramref name="port"/> binds to: <c>"loopback"</c> (the default), <c>"any"</c>/
    /// <c>"0.0.0.0"</c>/<c>"::"</c>, or a literal IP address - see <c>McpOptions.BindAddress</c>'s
    /// remarks. MCP's own bearer-token auth (<see cref="McpBearerAuthMiddleware"/>) gates every request
    /// regardless of bind address, so widening this is a deliberate operator choice, not a new
    /// unauthenticated surface.
    /// </param>
    /// <param name="serilogLogger">
    /// The outer host's Serilog logger, so this inner host's own framework/request logs reach the same
    /// sinks the rest of the application logs through instead of the default console provider.
    /// <see langword="null"/> falls back to the pre-existing filtered default console provider, used by
    /// tests that build a <see cref="McpServer"/> directly with no Serilog pipeline available.
    /// </param>
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
        int port = 5003,
        string bindAddress = "loopback",
        Serilog.ILogger? serilogLogger = null)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(bindAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: port, 65535);

        _host = Host.CreateDefaultBuilder()
            // Same reasoning as ProxyServer's identical logging setup: when the outer host handed
            // across its Serilog logger, route this inner host's own logs through it instead of the
            // default console provider, matching how the rest of the application logs. A caller with no
            // Serilog pipeline available (most unit tests) falls back to the pre-existing filtered
            // default console provider, so a bind failure is still reported once, not twice, alongside
            // McpHostedService's own one-line report of it.
            .ConfigureLogging(logging =>
            {
                if (serilogLogger is not null)
                {
                    logging.ClearProviders();
                    logging.AddProvider(new SerilogLoggerProvider(logger: serilogLogger, dispose: false));
                }
                else
                {
                    logging
                        .AddFilter(category: "Microsoft.Extensions.Hosting.Internal.Host", level: LogLevel.None)
                        .AddFilter(category: "Microsoft", level: LogLevel.Warning);
                }
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                // Same reasoning as ProxyServer's identical calls - a Windows Service's working
                // directory is C:\Windows\System32, not the install directory.
                webBuilder.UseContentRoot(AppContext.BaseDirectory);
                webBuilder.UseSetting(key: WebHostDefaults.ApplicationKey, value: "TotallyHotArcRouter.McpServer");

                webBuilder.UseKestrel(options =>
                {
                    // A certificate failure here must NOT be swallowed: without a configured listener,
                    // Kestrel falls back to its default URL binding (e.g. http://localhost:5000 or
                    // ASPNETCORE_URLS) instead of simply not listening, which would silently start an
                    // unintended, unauthenticated port rather than making MCP "unavailable" as intended.
                    // Letting this throw fails the whole McpServer construction, which McpHostedService's
                    // own try/catch logs and swallows at the top level - the same "MCP is non-essential,
                    // don't fail the process" posture, just enforced one level up.
                    var certificate = TelemetryTlsCertificate.GetOrCreate();
                    KestrelBindAddress.Listen(options: options, bindAddress: bindAddress, port: port,
                        configure: listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                            listenOptions.UseHttps(certificate);
                        });
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
            var addresses = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?.Addresses;
            return addresses is null ? [] : new List<string>(addresses);
        }
    }

    /// <summary>Disposes the inner host.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable asyncHost)
            await asyncHost.DisposeAsync().ConfigureAwait(false);
        else
            _host.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _host.Dispose();
    }

    /// <summary>Starts the MCP endpoint.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _host.StartAsync(cancellationToken);
    }

    /// <summary>Stops the MCP endpoint.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _host.StopAsync(cancellationToken);
    }
}