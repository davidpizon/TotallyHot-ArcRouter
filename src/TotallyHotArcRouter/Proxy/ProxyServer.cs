using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Serilog.Extensions.Logging;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Auth;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Update;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Represents the proxy server, responsible for building and managing the Kestrel web host.
/// </summary>
public class ProxyServer : IAsyncDisposable, IDisposable
{
    /// <summary>Default port for the TLS-secured gRPC telemetry endpoint. See <see cref="ProxyListenerOptions.GrpcPort"/>'s remarks.</summary>
    public const int DefaultGrpcPort = 5002;

    private readonly IHost _host;

    // Non-null only when this server created its own management HttpClient (no caller-supplied one), so
    // disposal frees exactly what this server owns and never a client the caller still uses elsewhere.
    private readonly HttpClient? _ownedManagementHttpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyServer"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger for this instance (currently unused by Kestrel wiring, reserved for future
    /// diagnostics).
    /// </param>
    /// <param name="proxyMiddleware">
    /// The already-constructed middleware instance used to handle every request. Passed directly, rather than
    /// copying the application's DI container into the inner host, so the inner host can never end up with its
    /// own copy of application-level hosted service registrations (which previously caused unbounded recursive
    /// construction of <see cref="TotallyHot.ArcRouter.Hosting.ProxyHostedService"/>).
    /// </param>
    /// <param name="listenerOptions">
    /// The proxy's port and bind-address configuration - see <see cref="ProxyListenerOptions"/>. Defaults to
    /// <see langword="null"/>, which behaves identically to a freshly-constructed
    /// <see cref="ProxyListenerOptions"/>: port 5001, loopback-bound, gRPC on <see cref="DefaultGrpcPort"/>,
    /// and the opt-in plain-HTTP listener off. Pass <see cref="ProxyListenerOptions.Port"/>/
    /// <see cref="ProxyListenerOptions.GrpcPort"/> as 0 to bind ephemeral ports (useful in tests to avoid
    /// flaking when the default ports are already in use); the resolved addresses are available via
    /// <see cref="Addresses"/> once <see cref="StartAsync"/> completes.
    /// </param>
    /// <param name="webInterfaceOptions">
    /// The router-hosted web GUI/gRPC-Web listener's port and bind-address configuration - see
    /// <see cref="WebInterfaceOptions"/>. Defaults to <see langword="null"/>, which behaves identically to
    /// a freshly-constructed <see cref="WebInterfaceOptions"/>: port 5004, loopback-bound. Shares its TLS
    /// certificate with the gRPC endpoint (<see cref="ProxyListenerOptions.GrpcPort"/>), so both fail to
    /// bind together if certificate initialization fails.
    /// </param>
    /// <param name="dependencies">
    /// Everything that has to be hand-carried across the boundary into the inner host's own DI container,
    /// grouped by feature - see <see cref="ProxyServerDependencies"/>, whose members document what each
    /// group enables and what its absence leaves unmapped. Defaults to <see langword="null"/>, which
    /// behaves identically to supplying an instance with every group unset: a plain proxy-forwarding
    /// server with the telemetry stream and the Routing Mode panel API, and no other admin surface.
    /// </param>
    public ProxyServer(
        ILogger<ProxyServer> logger,
        ProxyMiddleware proxyMiddleware,
        ProxyListenerOptions? listenerOptions = null,
        WebInterfaceOptions? webInterfaceOptions = null,
        ProxyServerDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(proxyMiddleware);

        var listener = listenerOptions ?? new ProxyListenerOptions();
        var webInterface = webInterfaceOptions ?? new WebInterfaceOptions();
        var port = listener.Port;
        var grpcPort = listener.GrpcPort;
        var webPort = webInterface.Port;
        ArgumentOutOfRangeException.ThrowIfNegative(port, paramName: nameof(listenerOptions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: port, 65535, paramName: nameof(listenerOptions));
        ArgumentOutOfRangeException.ThrowIfNegative(grpcPort, paramName: nameof(listenerOptions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: grpcPort, 65535, paramName: nameof(listenerOptions));
        ArgumentOutOfRangeException.ThrowIfNegative(webPort, paramName: nameof(webInterfaceOptions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: webPort, 65535, paramName: nameof(webInterfaceOptions));
        // 0 (ephemeral) stays allowed here, same as port/grpcPort/webPort above, so tests can bind the
        // opt-in listener without picking a fixed port. ProxyListenerOptionsValidator enforces the
        // stronger "must be a real, stable port when enabled" rule for DI-bound configuration, where an
        // operator pointing an already-configured tool at this listener needs it to not move on restart.
        if (listener.PlainHttp.Enabled)
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: listener.PlainHttp.Port, 65535,
                paramName: nameof(listenerOptions));

        var broadcaster = dependencies?.Telemetry ?? new TelemetryBroadcaster();
        var managementTokenProvider = dependencies?.ManagementTokenProvider;
        var routingOptions = dependencies?.RoutingOptions;

        var managementApi = dependencies?.ManagementApi;

        // Each optional feature group is either wholly present or wholly absent, and each now owns both
        // halves of its own wiring through IAdminServiceModule - registration and endpoint mapping - so
        // the two can no longer drift apart. They used to be written out twice, ~120 lines apart, and two
        // copies disagreeing would have produced a service that maps successfully and then throws on its
        // first RPC call: MapGrpcService only reflects over the service type, it never constructs it, so
        // nothing fails at startup. Materialized once because it is walked twice, below.
        var adminModules = dependencies?.AdminModules ?? [];

        // Update (docs/router/auto-update-plan.md Phase 2) is mapped unconditionally, like
        // RoutingModeAdminGrpcService above - so unlike every optional group, this one always has
        // something to register, falling back to harmless no-ops when the caller didn't supply a group.
        var updateAdmin = dependencies?.UpdateAdmin;

        // The GUI system tray's routing kill switch (Governance-adjacent, but tray-controlled rather
        // than a Governance panel) is mapped unconditionally, the same way UpdateAdminGrpcService above
        // is - so this always has something to register, falling back to a private, unpersisted gate
        // (not the instance ProxyMiddleware checks) when the caller didn't supply the real one.
        var routingGateAdmin = dependencies?.RoutingGateAdmin;

        // The Governance UI's Regret Harness panel (docs/router/regret-evaluation-harness-plan.md N6) is
        // mapped unconditionally, the same way UpdateAdminGrpcService/RoutingGateAdminGrpcService above
        // are - so this always has something to register, falling back to a runner that always declines
        // when the caller didn't supply the real one.
        var regretHarnessAdmin = dependencies?.RegretHarnessAdmin;
        var judgeCalibrationAdmin = dependencies?.JudgeCalibrationAdmin;

        // Own (and later dispose) the management client only when the caller didn't supply one. Note this
        // runs whether or not the management API is enabled, exactly as before the parameter moved into
        // ManagementApiDependencies - the client is this server's to dispose either way.
        _ownedManagementHttpClient = managementApi?.HttpClient is null ? new HttpClient() : null;
        var managementClient = managementApi?.HttpClient ?? _ownedManagementHttpClient!;

        var serilogLogger = dependencies?.SerilogLogger;

        _host = Host.CreateDefaultBuilder()
            // This inner host is an implementation detail of ProxyServer, but its logs matter - Kestrel
            // bind failures and per-request routing among them - so when the outer host handed across its
            // Serilog logger (see ProxyServerDependencies.SerilogLogger's remarks), route everything
            // through it instead of the default console provider, matching how the rest of the
            // application logs. A caller that built this server directly with no Serilog pipeline
            // available (most unit tests) falls back to the pre-existing filtered default console
            // provider so a bind failure is still reported once, not twice: without the filter it would
            // otherwise surface both here (full stack, default console provider) and again through the
            // outer host as ProxyHostedService's own start-failure log line.
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
                // A Windows Service's working directory is C:\Windows\System32, not the install
                // directory - without an explicit content root, static-asset serving (the web GUI,
                // migration plan Phase P2/P6) would resolve wwwroot from there and 404 everything.
                // ApplicationName likewise defaults from the entry assembly in a plain console run, but
                // an explicit value keeps the static-web-assets manifest lookup independent of how the
                // process was launched (dotnet run, the installed service, a test host).
                webBuilder.UseContentRoot(AppContext.BaseDirectory);
                webBuilder.UseSetting(key: WebHostDefaults.ApplicationKey, value: "TotallyHotArcRouter.ProxyServer");
                // Resolves the WASM dashboard's static assets (Phase P6) from Gui.Web's generated
                // staticwebassets manifest at development-time run (dotnet run / the built exe run
                // directly, neither of which is a `dotnet publish` output). WebApplication.CreateBuilder
                // calls this automatically; the older Host.CreateDefaultBuilder().ConfigureWebHostDefaults
                // pattern this inner host uses does not, and IWebHostEnvironment.WebRootFileProvider
                // resolves to nothing without it (confirmed empirically: omitting this line logs "The
                // WebRootPath was not found" and 404s every dashboard asset). A published build's static
                // web assets are physically copied into this app's own wwwroot at publish time, so this
                // call is a no-op there, not a behavior difference between dev and published.
                webBuilder.UseStaticWebAssets();

                webBuilder.UseKestrel(options =>
                {
                    // Reaped dead browser gRPC-Web/StreamEvents connections (a laptop sleep, a dropped
                    // Wi-Fi) rather than holding them open indefinitely - host-wide, so it applies to
                    // every HTTP/2 listener below. Web GUI migration plan Phase P2.
                    options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
                    options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);

                    // The router's own name-constrained local CA (ADR-0013, web GUI migration plan Phase
                    // P7) issues every leaf below. Resolved once per Kestrel configuration (not once per
                    // connection) purely to fail loudly and immediately if certificate generation is
                    // fundamentally broken (e.g. the data directory is unwritable) - the leaf itself is
                    // still re-resolved on every TLS handshake via ServerCertificateSelector below, which
                    // is the actual hot-swap mechanism: LocalCertificateAuthority.GetOrCreateLeaf()
                    // transparently mints and persists a replacement once the cached leaf enters its
                    // renewal window, with no restart and no re-trust needed by an already-trusting
                    // client. Deliberately not caught here the way the old TelemetryTlsCertificate call
                    // was: with the LLM proxy port now also TLS-only by default, a certificate failure
                    // means the router cannot serve its core purpose at all, not just that telemetry is
                    // unavailable - so this now fails ProxyServer construction outright rather than
                    // silently degrading.
                    LocalCertificateAuthority.GetOrCreateLeaf();

                    // The primary LLM-forwarding proxy port (Phase P7): HTTPS by default (D6), with the
                    // same shared leaf every other listener presents - ADR-0013's whole point is one
                    // trusted root instead of per-port trust. Http1AndHttp2: existing HTTP/1.1 clients
                    // are unaffected, and TLS ALPN (not h2c) is exactly what made the dedicated gRPC port
                    // reliable in the first place, so there's no reason to withhold HTTP/2 here now that
                    // this port is TLS too. BindAddress lets this move off loopback for a Docker
                    // deployment (migration plan D6a's sibling decision); see KestrelBindAddress's
                    // remarks for the ephemeral-port special case. Tagged as a proxy-port connection (see
                    // TagAsProxyPort) so the pipeline below routes it straight to proxyMiddleware and
                    // never through gRPC/admin endpoint routing.
                    KestrelBindAddress.Listen(options: options, bindAddress: listener.BindAddress, port: port,
                        configure: listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                            listenOptions.UseHttps(httpsOptions =>
                                httpsOptions.ServerCertificateSelector =
                                    (_, _) => LocalCertificateAuthority.GetOrCreateLeaf());
                            TagAsProxyPort(listenOptions);
                        });

                    // The opt-in plain-HTTP LLM-proxy listener (D6a): an escape hatch for tools that
                    // ignore the OS trust store and cannot be pointed at the router's local CA. Always
                    // loopback, regardless of the primary port's BindAddress - see
                    // PlainHttpListenerOptions' remarks for why it has no bind-address setting of its own.
                    // Tagged as a proxy-port connection, same as the primary port above.
                    if (listener.PlainHttp.Enabled)
                    {
                        logger.LogWarning(
                            message:
                            "The opt-in plain-HTTP LLM-proxy listener is enabled on port {Port}. Traffic to it is unencrypted (loopback-only, but plaintext on the wire to that first hop). Prefer the HTTPS proxy port unless your client cannot trust the router's local CA.",
                            listener.PlainHttp.Port);
                        KestrelBindAddress.Listen(options: options, bindAddress: "loopback",
                            port: listener.PlainHttp.Port,
                            configure: listenOptions =>
                            {
                                listenOptions.Protocols = HttpProtocols.Http1;
                                TagAsProxyPort(listenOptions);
                            });
                    }

                    // The dedicated TLS/gRPC endpoint. HTTP/2 is negotiated via standard TLS ALPN here, not
                    // h2c prior-knowledge - the whole point of this port existing is to avoid the h2c
                    // reliability problem the proxy port itself now also avoids by being TLS. Always
                    // loopback, independent of BindAddress - see ProxyListenerOptions.GrpcPort's remarks:
                    // it is being retired (migration plan Phase P9), not extended.
                    KestrelBindAddress.Listen(options: options, bindAddress: "loopback", port: grpcPort,
                        configure: listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http2;
                            listenOptions.UseHttps(httpsOptions =>
                                httpsOptions.ServerCertificateSelector =
                                    (_, _) => LocalCertificateAuthority.GetOrCreateLeaf());
                        });

                    // The web GUI/gRPC-Web endpoint (Phase P2). Http1AndHttp2 (not Http2-only like the
                    // native gRPC port above): browsers negotiate HTTP/2 via ALPN where they can, but
                    // gRPC-Web itself works equally over HTTP/1.1, and the WASM static assets (Phase P6)
                    // are ordinary HTTP/1.1 GETs. BindAddress lets this move off loopback for Docker,
                    // same as the primary proxy port.
                    KestrelBindAddress.Listen(options: options, bindAddress: webInterface.BindAddress,
                        port: webPort,
                        configure: listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                            listenOptions.UseHttps(httpsOptions =>
                                httpsOptions.ServerCertificateSelector =
                                    (_, _) => LocalCertificateAuthority.GetOrCreateLeaf());
                        });
                });

                // gRPC and the shared broadcaster are registered into this inner host's own DI container
                // (deliberately separate from the outer application container - see the constructor remarks
                // above), not the outer one.
                webBuilder.ConfigureServices(services =>
                {
                    // Gate every gRPC call (telemetry stream and price-source admin alike) behind the
                    // shared management token or an ADR-0012 session cookie - see
                    // TelemetryAuthInterceptor's remarks. Also backs the web port's /auth/* endpoints
                    // below. Only registered when a token provider is configured, mirroring the previous
                    // managementToken gating: a null provider means "no inbound auth", used by tests
                    // exercising forwarding only.
                    if (managementTokenProvider is not null)
                    {
                        services.AddSingleton(managementTokenProvider);
                        services.AddSingleton<ManagementSessionTicketService>();
                        services.AddSingleton<LoginRateLimiter>();
                        services.AddSingleton(sp => new TelemetryAuthInterceptor(
                            tokenProvider: sp.GetRequiredService<IManagementTokenProvider>(),
                            sessionTickets: sp.GetRequiredService<ManagementSessionTicketService>()));
                        services.AddGrpc(options => options.Interceptors.Add<TelemetryAuthInterceptor>());
                    }
                    else
                    {
                        services.AddGrpc();
                    }

                    // Backs the web port's Host allowlist guard (WebPortRequestGuardMiddleware) and the
                    // /auth/session loopback-fast-path check (ManagementAuthEndpoints).
                    services.AddSingleton(webInterface);

                    services.AddSingleton(broadcaster);

                    // Same reasoning as the broadcaster: every optional admin feature's collaborators live
                    // in the outer container, so its gRPC service can only be constructed here if they are
                    // handed across explicitly. Each group knows its own registrations - see
                    // IAdminServiceModule.
                    foreach (var module in adminModules) module.Register(services);

                    // Unlike the optional groups above, RoutingModeAdminGrpcService's dependency is core
                    // configuration rather than an optional feature store, so it defaults to the
                    // caller's own RoutingOptions defaults instead of leaving the service unmapped. It is
                    // also what covers LogRegModelAdminGrpcService's own IOptions<RoutingOptions>.
                    services.AddSingleton(routingOptions ?? Options.Create(new RoutingOptions()));


                    // The Governance UI's "Software Update" section API (Phase 2). Always registered -
                    // see the updateAdmin local's remarks above.
                    services.AddSingleton(updateAdmin?.StateStore ?? new UpdateStateStore());
                    services.AddSingleton(updateAdmin?.ReleaseCheckClient ?? new NullReleaseCheckClient());

                    // The GUI system tray's routing kill switch API. Always registered - see the
                    // routingGateAdmin local's remarks above.
                    services.AddSingleton(routingGateAdmin?.Gate ?? new RoutingGateStore());

                    // The Governance UI's Regret Harness panel API (Phase N6). Always registered - see
                    // the regretHarnessAdmin local's remarks above.
                    services.AddSingleton(regretHarnessAdmin?.Runner ?? new NullRegretHarnessRunner());

                    // The Governance UI's Judge Calibration panel API (Phase G2). Always registered -
                    // see the judgeCalibrationAdmin local's remarks above.
                    services.AddSingleton(judgeCalibrationAdmin?.Analyzer ?? new NullJudgeCalibrationAnalyzer());

                    // docs/router/tracked-todos.md #7: ProviderAdminGrpcService/UsageAdminGrpcService are
                    // resolved from this inner host's own DI container (gRPC services always are), so the
                    // facade/reporting-service instances backing them are registered here rather than
                    // constructed inline in the endpoint-mapping block below - and that same block reuses
                    // these singletons for the REST surface instead of building a second copy, so both
                    // transports share one facade instance (and therefore one in-memory state) while the
                    // migration keeps both mapped side by side.
                    if (managementApi is not null)
                    {
                        var facade = new ManagementFacade(
                            store: managementApi.ConfigStore,
                            environment: managementApi.Environment ?? new EnvironmentVariableProvider(),
                            httpClient: managementClient,
                            dependencies: new ManagementFacadeDependencies
                            {
                                BudgetStore = managementApi.BudgetStore,
                                EndpointScanner = managementApi.EndpointScanner,
                                CapabilityStore = managementApi.CapabilityStore,
                                PriceRepository = managementApi.PriceRepository,
                                RateLimitRepository = managementApi.RateLimitRepository,
                                ReportedUsageRepository = managementApi.ReportedUsageRepository,
                                OverrideStore = managementApi.ModelAliasOverrideStore,
                                SecretWriter = managementApi.SecretWriter,
                                SecretReader = managementApi.SecretReader,
                                InteractionStatusStore = managementApi.InteractionStatusStore ??
                                                         new ProviderInteractionStatusStore()
                            });
                        var reportingService = new ManagementReportingService(
                            rollupStore: managementApi.UsageRollupStore,
                            comparisonStore: managementApi.TaxonomyComparisonStore);

                        services.AddSingleton(facade);
                        services.AddSingleton(reportingService);
                    }
                });

                webBuilder.Configure(app =>
                {
                    // Port scoping (web GUI migration plan Phase P2): a request is routed by which
                    // physical listener accepted its connection (tagged via TagAsProxyPort at Kestrel
                    // configuration time above), never by the spoofable Host header (ADR-0012's
                    // DNS-rebinding concern applies to auth, but the same spoofability argument rules out
                    // using RequireHost for transport-level scoping too). A proxy-port connection is
                    // handed straight to proxyMiddleware and never reaches routing/gRPC endpoints at all -
                    // this is what keeps gRPC/admin surfaces unreachable from the plain-HTTP proxy ports.
                    app.Use(async (context, next) =>
                    {
                        if (context.Features.Get<ProxyPortMarker>() is not null)
                        {
                            await proxyMiddleware.InvokeAsync(context: context, next: _ => Task.CompletedTask)
                                .ConfigureAwait(false);
                            return;
                        }

                        await next(context).ConfigureAwait(false);
                    });

                    // Everything below only ever runs for a gRPC/web-port connection (grpcPort or webPort) -
                    // proxy-port connections returned via the gate above and never reach here.

                    // ADR-0012's per-request guard (Phase P4): Host allowlist and Origin/same-origin check,
                    // ahead of everything else on this pipeline - gRPC-Web calls and the /auth/* endpoints
                    // mapped below alike. See WebPortRequestGuardMiddleware's remarks for why this never
                    // enables ForwardedHeaders.
                    app.UseMiddleware<WebPortRequestGuardMiddleware>(webInterface);

                    // Serves the WASM dashboard's static assets (web GUI migration plan Phase P6) - the
                    // web port only. grpcPort accepts native gRPC and (until Phase P9) exists purely for
                    // that, so a browser never requests a static file from it; UseWhen keeps this branch
                    // out of that connection's pipeline entirely rather than relying on every asset
                    // request happening to 404 on its own. UseDefaultFiles rewrites a bare "/" to
                    // "/index.html" before UseStaticFiles serves it - the dashboard has no server- or
                    // client-side routing of its own (no @page/<Router> anywhere in Gui.Components; every
                    // "tab" is in-component state, not a navigable URL), so unlike a typical Blazor SPA
                    // this needs no MapFallbackToFile for arbitrary deep links - only "/" itself ever
                    // needs to resolve to index.html. Deliberately NOT a broader "unmatched GET with no
                    // file extension" heuristic: that shape also matches the deleted REST /admin/* and
                    // the LLM-proxy /v1/* paths, which must keep 404ing on the web port (see
                    // WebPort_RestAdminPath_Returns404/WebPort_ProxyPath_Returns404).
                    app.UseWhen(
                        predicate: context => context.Connection.LocalPort == webPort,
                        configuration: webApp =>
                        {
                            webApp.UseDefaultFiles();
                            webApp.UseStaticFiles();
                        });

                    // Lets a browser call the gRPC services mapped below over grpc-web framing (HTTP/1.1-
                    // or HTTP/2-safe, unlike trailers-based native gRPC) without opting in per service -
                    // DefaultEnabled applies it to every MapGrpcService call, native gRPC callers on
                    // grpcPort included (grpc-web detection is by content-type, so a native gRPC client's
                    // ordinary requests pass through unaffected).
                    app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

                    // Baseline hardening for the one browser-reachable listener among these (webPort);
                    // harmless on grpcPort, which no browser ever talks to directly. frame-ancestors 'none'
                    // and script-src 'self' 'wasm-unsafe-eval' anticipate Phase P6's WASM GUI; nosniff
                    // guards the eventual static assets against content-type sniffing.
                    app.Use(async (context, next) =>
                    {
                        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                        context.Response.Headers["Content-Security-Policy"] =
                            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; frame-ancestors 'none'";
                        await next(context).ConfigureAwait(false);
                    });

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGrpcService<TelemetryGrpcService>();

                        // Every optional Governance panel API - price sources, benchmark data, the
                        // "Local Voter Model" section, cluster model, router model, and System Settings.
                        // All share the TLS gRPC port with the telemetry stream, and each maps itself
                        // right where it registered its collaborators - see IAdminServiceModule.
                        foreach (var module in adminModules) module.Map(endpoints);

                        // The Governance UI's Routing Mode panel API. Shares the TLS gRPC port with the
                        // telemetry stream and the other admin services. Always mapped - see the
                        // routingOptions registration above.
                        endpoints.MapGrpcService<RoutingModeAdminGrpcService>();

                        // The Governance UI's "Software Update" section API (Phase 2). Always mapped -
                        // see the updateAdmin local's remarks above.
                        endpoints.MapGrpcService<UpdateAdminGrpcService>();

                        // The GUI system tray's routing kill switch API. Shares the TLS gRPC port with
                        // the telemetry stream and the other admin services. Always mapped - see the
                        // routingGateAdmin local's remarks above.
                        endpoints.MapGrpcService<RoutingGateAdminGrpcService>();

                        // The Governance UI's Regret Harness panel API (Phase N6). Shares the TLS gRPC
                        // port with the telemetry stream and the other admin services. Always mapped -
                        // see the regretHarnessAdmin local's remarks above.
                        endpoints.MapGrpcService<RegretHarnessAdminGrpcService>();

                        // The Governance UI's Judge Calibration panel API (Phase G2). Shares the TLS
                        // gRPC port with the telemetry stream and the other admin services. Always
                        // mapped - see the judgeCalibrationAdmin local's remarks above.
                        endpoints.MapGrpcService<JudgeCalibrationAdminGrpcService>();

                        // The Governance UI's provider/credential/model management and usage-query APIs.
                        // Only mapped when a writable store is supplied. gRPC-only since the web GUI
                        // migration plan's Phase P2 deleted the REST /admin/* surface these once shared
                        // this endpoint with (docs/router/tracked-todos.md #7's migration is complete) -
                        // both services resolve the same ManagementFacade/ManagementReportingService
                        // singletons registered above from their own constructor injection.
                        if (managementApi is not null)
                        {
                            endpoints.MapGrpcService<ProviderAdminGrpcService>();
                            endpoints.MapGrpcService<UsageAdminGrpcService>();
                        }

                        // ADR-0012's session endpoints (Phase P4): loopback fast-path issuance, token
                        // login, and logout. Only mapped alongside real inbound auth - see
                        // ManagementAuthEndpoints' and managementTokenProvider's remarks.
                        if (managementTokenProvider is not null) ManagementAuthEndpoints.Map(endpoints);

                        // The WASM dashboard's fingerprinted assets (web GUI migration plan Phase P6) -
                        // endpoint-routing metadata (cache headers, content negotiation) on top of the
                        // UseStaticFiles branch above, which already scopes physical file serving to the
                        // web port; this only adds routing information for the same files.
                        endpoints.MapStaticAssets();
                    });
                    // Reached only on a gRPC/web-port connection that matched no mapped endpoint and no
                    // static file (including "/", via UseDefaultFiles above) - the gate above already
                    // sent every proxy-port connection to proxyMiddleware, so this is never real LLM
                    // traffic. An explicit 404 (ASP.NET Core's own unmatched-endpoint default is 200 with
                    // an empty body, not 404) rather than silently falling through to the proxy.
                    app.Run(context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    });
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
            var addresses = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?.Addresses;
            return addresses is null ? [] : new List<string>(addresses);
        }
    }

    /// <summary>
    /// The inner host's service provider. Internal - exists so tests can verify hosting configuration
    /// (e.g. content root independence from the process's current directory) that has no other externally
    /// observable surface, without exposing the inner container as public API.
    /// </summary>
    internal IServiceProvider Services => _host.Services;

    /// <summary>
    /// A per-connection marker set by <see cref="TagAsProxyPort"/> on every connection accepted by a
    /// proxy-purpose listener (the plain-HTTP forwarding port and, when enabled, the opt-in plain-HTTP
    /// listener). <see cref="Microsoft.AspNetCore.Http.HttpContext.Features"/> falls back to the
    /// underlying connection's feature collection for any feature type it does not itself implement, so a
    /// feature set here at the connection level is visible to HTTP middleware via
    /// <c>context.Features.Get&lt;ProxyPortMarker&gt;()</c> - see the pipeline gate in the constructor's
    /// <c>Configure</c> callback. This is what makes port scoping immune to a spoofed <c>Host</c> header:
    /// the marker reflects which physical listener accepted the TCP connection, not anything the client
    /// sent.
    /// </summary>
    private sealed class ProxyPortMarker
    {
        public static readonly ProxyPortMarker Instance = new();
    }

    /// <summary>
    /// Registers connection-level middleware on <paramref name="listenOptions"/> that stamps every
    /// connection this specific listener accepts with <see cref="ProxyPortMarker"/>. Pass as (part of) a
    /// <c>configure</c> callback to <see cref="KestrelBindAddress.Listen"/> for a proxy-purpose listener
    /// only - never for the gRPC or web-interface listeners.
    /// </summary>
    private static void TagAsProxyPort(ListenOptions listenOptions)
    {
        listenOptions.Use(middleware: next => context =>
        {
            context.Features.Set(instance: ProxyPortMarker.Instance);
            return next(context);
        });
    }

    /// <summary>
    /// Disposes the inner host and, when this server created it, the management <see cref="HttpClient"/>,
    /// so repeatedly creating and discarding servers (e.g. across tests) doesn't leak hosts or handlers.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable asyncHost)
            await asyncHost.DisposeAsync().ConfigureAwait(false);
        else
            _host.Dispose();

        _ownedManagementHttpClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _host.Dispose();
        _ownedManagementHttpClient?.Dispose();
        GC.SuppressFinalize(this);
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
}