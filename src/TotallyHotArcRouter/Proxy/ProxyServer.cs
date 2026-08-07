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
        /// request when set. Defaults to <see langword="null"/> (no inbound auth) - production wiring
        /// (<see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions"/>) always passes the
        /// <see cref="Management.ManagementAccessToken"/> value here, so <c>/admin/*</c> is gated by
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
        public ProxyServer(ILogger<ProxyServer> logger, ProxyMiddleware proxyMiddleware, int port = 5001, TelemetryBroadcaster? telemetryBroadcaster = null, int grpcPort = DefaultGrpcPort, IProviderConfigStore? providerConfigStore = null, IEnvironmentVariableProvider? environment = null, HttpClient? managementHttpClient = null, string? managementToken = null, PriceSourceToggleStore? priceSourceToggleStore = null, PriceCatalogIngestionService? priceCatalogIngestionService = null, PriceCatalogOptions? priceCatalogOptions = null, ProviderBudgetStore? providerBudgetStore = null, ProviderEndpointScanner? endpointScanner = null, ToolCallCapabilityStore? toolCallCapabilityStore = null, PriceCatalogRepository? priceCatalogRepository = null)
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
                        services.AddGrpc();
                        services.AddSingleton(broadcaster);

                        // Same reasoning as the broadcaster: the price catalog singletons live in the outer
                        // container, so PriceSourceAdminGrpcService can only be constructed here if they are
                        // handed across explicitly. Registered as a pair - the service needs both.
                        if (priceSourceToggleStore is not null && priceCatalogIngestionService is not null)
                        {
                            services.AddSingleton(priceSourceToggleStore);
                            services.AddSingleton(priceCatalogIngestionService);

                            // The panel's countdown needs the poll cadence, and this inner container has no
                            // configuration bound into it - AddGrpc alone would leave the IOptions dependency
                            // unresolvable and fail on the first call, not at startup.
                            services.AddSingleton(Options.Create(priceCatalogOptions ?? new PriceCatalogOptions()));
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
                            if (priceSourceToggleStore is not null && priceCatalogIngestionService is not null)
                            {
                                endpoints.MapGrpcService<PriceSourceAdminGrpcService>();
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
                                    priceCatalogRepository);
                                endpoints.MapProviderAdminEndpoints(facade, managementToken);
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

