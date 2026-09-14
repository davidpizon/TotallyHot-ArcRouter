using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Serilog.Extensions.Logging;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
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
        ProxyServerDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(proxyMiddleware);

        var listener = listenerOptions ?? new ProxyListenerOptions();
        var port = listener.Port;
        var grpcPort = listener.GrpcPort;
        ArgumentOutOfRangeException.ThrowIfNegative(port, paramName: nameof(listenerOptions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: port, 65535, paramName: nameof(listenerOptions));
        ArgumentOutOfRangeException.ThrowIfNegative(grpcPort, paramName: nameof(listenerOptions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: grpcPort, 65535, paramName: nameof(listenerOptions));

        var broadcaster = dependencies?.Telemetry ?? new TelemetryBroadcaster();
        var managementToken = dependencies?.ManagementToken;
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

                webBuilder.UseKestrel(options =>
                {
                    // Plain HTTP/1.1 only - this port is exclusively LLM-forwarding proxy traffic and
                    // /v1/models, both HTTP/1.1 clients today. gRPC no longer shares this port (see grpcPort
                    // below) - see docs/router/grpc-migration.md's "Transport" section for why: unencrypted
                    // HTTP/2 (h2c) turned out to be unreliable on at least one managed/corporate machine (every
                    // connection failed with the HTTP/2-level HTTP_1_1_REQUIRED error, consistent with
                    // something on the network path not understanding or mangling the h2c preface), so the
                    // gRPC endpoint moved to its own dedicated TLS port instead of trying to fix h2c itself.
                    // BindAddress lets this move off loopback for a Docker deployment (migration plan D6a's
                    // sibling decision); see KestrelBindAddress's remarks for the ephemeral-port special case.
                    KestrelBindAddress.Listen(options: options, bindAddress: listener.BindAddress, port: port);

                    // The dedicated TLS/gRPC endpoint. HTTP/2 is negotiated via standard TLS ALPN here, not
                    // h2c prior-knowledge - the whole point of this port existing is to avoid the h2c
                    // reliability problem above. TelemetryTlsCertificate persists a self-signed cert per
                    // machine/user so the client doesn't need to re-trust a new one on every proxy restart.
                    // Certificate initialization is non-essential (telemetry is not critical to proxy operation),
                    // so catch any exceptions and skip binding the gRPC port if the cert fails to load/generate.
                    // Always loopback, independent of BindAddress - see ProxyListenerOptions.GrpcPort's
                    // remarks: it is being retired (migration plan Phase P9), not extended.
                    try
                    {
                        var certificate = TelemetryTlsCertificate.GetOrCreate();
                        KestrelBindAddress.Listen(options: options, bindAddress: "loopback", port: grpcPort,
                            configure: listenOptions =>
                            {
                                listenOptions.Protocols = HttpProtocols.Http2;
                                listenOptions.UseHttps(certificate);
                            });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(exception: ex,
                            message:
                            "Failed to initialize telemetry gRPC listener (certificate generation/load failed). Telemetry will be unavailable.");
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
                    // UseRouting + mapped endpoints handle only requests matching the
                    // TelemetryService.StreamEvents RPC or (when a store is supplied) the /admin/*
                    // management API; every other request - which is all real LLM API traffic - falls
                    // through unmatched to the terminal app.Run below, completely unchanged from before
                    // these endpoints existed.
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
                        // Only mapped when a writable store is supplied. Both the gRPC services (§7's
                        // migration target, sharing the :5002 TLS loopback endpoint like every other
                        // admin service) and the REST surface they are replacing are mapped side by side
                        // for now, resolving the same ManagementFacade/ManagementReportingService
                        // singletons registered above so both transports see one shared state - see
                        // docs/router/tracked-todos.md #7.
                        if (managementApi is not null)
                        {
                            var facade = app.ApplicationServices.GetRequiredService<ManagementFacade>();
                            var reportingService = app.ApplicationServices.GetRequiredService<ManagementReportingService>();

                            endpoints.MapGrpcService<ProviderAdminGrpcService>();
                            endpoints.MapGrpcService<UsageAdminGrpcService>();

                            endpoints.MapProviderAdminEndpoints(facade: facade, managementToken: managementToken);
                            endpoints.MapUsageAdminEndpoints(reportingService: reportingService,
                                managementToken: managementToken);
                        }
                    });
                    app.Run(context => proxyMiddleware.InvokeAsync(context: context, next: _ => Task.CompletedTask));
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