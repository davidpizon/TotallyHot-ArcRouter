using TotallyHot.ArcRouter.Models;
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
        /// <param name="grpcPort">
        /// A second, dedicated localhost port for the TLS-secured gRPC telemetry endpoint (<see cref="DefaultGrpcPort"/>
        /// by default). Deliberately a separate port from <paramref name="port"/>, not a second protocol sharing the
        /// same port: <paramref name="port"/> must stay plain, unencrypted HTTP/1.1 for existing LLM-forwarding
        /// clients that already connect to it that way, so it cannot also become an HTTPS/2 endpoint. Pass 0 to bind
        /// an ephemeral port, mirroring <paramref name="port"/>'s test-friendly behavior.
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
            int port = 5001,
            int grpcPort = DefaultGrpcPort,
            ProxyServerDependencies? dependencies = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(proxyMiddleware);
            ArgumentOutOfRangeException.ThrowIfNegative(port);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            ArgumentOutOfRangeException.ThrowIfNegative(grpcPort);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(grpcPort, 65535);

            var broadcaster = dependencies?.Telemetry ?? new TelemetryBroadcaster();
            var managementToken = dependencies?.ManagementToken;
            var routingOptions = dependencies?.RoutingOptions;

            // Each feature group is either wholly present or wholly absent, so a single null check per group
            // decides both whether its services are registered below and whether its endpoint is mapped. The
            // members within a present group are non-nullable by type, which is what makes "supplied two of
            // the three" impossible to express: previously each of these was a two-to-five-way conjunction
            // written out twice, ~60-120 lines apart, and the two copies drifting apart would have produced a
            // service that maps successfully and then throws on its first RPC call - MapGrpcService only
            // reflects over the service type, it never constructs it, so nothing fails at startup.
            var managementApi = dependencies?.ManagementApi;
            var priceSourceAdmin = dependencies?.PriceSourceAdmin;
            var benchmarkDataAdmin = dependencies?.BenchmarkDataAdmin;
            var llmRouterModelAdmin = dependencies?.LlmRouterModelAdmin;
            var clusterModelAdmin = dependencies?.ClusterModelAdmin;
            var logRegModelAdmin = dependencies?.LogRegModelAdmin;
            var routerSettingsAdmin = dependencies?.RouterSettingsAdmin;

            // Update (docs/router/auto-update-plan.md Phase 2) is mapped unconditionally, like
            // RoutingModeAdminGrpcService above - so unlike every optional group, this one always has
            // something to register, falling back to harmless no-ops when the caller didn't supply a group.
            var updateAdmin = dependencies?.UpdateAdmin;

            // The GUI system tray's routing kill switch (Governance-adjacent, but tray-controlled rather
            // than a Governance panel) is mapped unconditionally, the same way UpdateAdminGrpcService above
            // is - so this always has something to register, falling back to a private, unpersisted gate
            // (not the instance ProxyMiddleware checks) when the caller didn't supply the real one.
            var routingGateAdmin = dependencies?.RoutingGateAdmin;

            // Own (and later dispose) the management client only when the caller didn't supply one. Note this
            // runs whether or not the management API is enabled, exactly as before the parameter moved into
            // ManagementApiDependencies - the client is this server's to dispose either way.
            _ownedManagementHttpClient = managementApi?.HttpClient is null ? new HttpClient() : null;
            var managementClient = managementApi?.HttpClient ?? _ownedManagementHttpClient!;

            _host = Host.CreateDefaultBuilder()
                // This inner host is an implementation detail of ProxyServer: the outer application host
                // owns the process's lifecycle logging. Without this filter a bind failure is reported
                // twice - once here with a full stack through the default console provider (this host never
                // gets Serilog), and again by the outer host as ProxyHostedService's start failure - so the
                // inner copy is suppressed and ProxyHostedService is left to report the condition once.
                .ConfigureLogging(logging => logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None))
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
                        if (priceSourceAdmin is not null)
                        {
                            services.AddSingleton(priceSourceAdmin.ToggleStore);
                            services.AddSingleton(priceSourceAdmin.IngestionService);

                            // The panel's countdown needs the poll cadence, and this inner container has no
                            // configuration bound into it - AddGrpc alone would leave the IOptions dependency
                            // unresolvable and fail on the first call, not at startup.
                            services.AddSingleton(Options.Create(priceSourceAdmin.Options ?? new PriceCatalogOptions()));
                        }

                        // Same reasoning again: the CodeRouterBench singletons live in the outer container,
                        // so BenchmarkDataAdminGrpcService can only be constructed here if they are handed
                        // across explicitly. Registered as a trio - the service needs all three.
                        if (benchmarkDataAdmin is not null)
                        {
                            services.AddSingleton(benchmarkDataAdmin.StatusService);
                            services.AddSingleton(benchmarkDataAdmin.FileLedger);
                            services.AddSingleton(benchmarkDataAdmin.SyncService);
                            services.AddSingleton(Options.Create(benchmarkDataAdmin.Options ?? new CodeRouterBench.BenchmarkSyncOptions()));
                        }

                        // Same reasoning again: the llm_router model override store and sync service live
                        // in the outer container, so LlmRouterModelAdminGrpcService can only be
                        // constructed here if they are handed across explicitly.
                        if (llmRouterModelAdmin is not null)
                        {
                            services.AddSingleton(llmRouterModelAdmin.OverrideStore);
                            services.AddSingleton(llmRouterModelAdmin.SyncService);
                        }

                        // Unlike the pairs above, RoutingModeAdminGrpcService's dependency is core
                        // configuration rather than an optional feature store, so it defaults to the
                        // caller's own RoutingOptions defaults instead of leaving the service unmapped.
                        services.AddSingleton(routingOptions ?? Options.Create(new RoutingOptions()));

                        // Same reasoning again: the cluster training service and its supporting stores live
                        // in the outer container, so ClusterModelAdminGrpcService can only be constructed
                        // here if they are handed across explicitly. Registered as a group of five - the
                        // service needs all of them.
                        if (clusterModelAdmin is not null)
                        {
                            services.AddSingleton(clusterModelAdmin.TrainingService);
                            services.AddSingleton(clusterModelAdmin.MemoryEntryStore);
                            services.AddSingleton(clusterModelAdmin.TranscriptStore);
                            services.AddSingleton(clusterModelAdmin.TranscriptOptions);
                            services.AddSingleton(clusterModelAdmin.StorageOptions);
                        }

                        // Same reasoning again: the logreg training service and its supporting store live
                        // in the outer container, so LogRegModelAdminGrpcService can only be constructed
                        // here if they are handed across explicitly. Its IOptions<RoutingOptions> dependency
                        // is already covered by the unconditional registration above, so only the
                        // training-specific pair is registered here.
                        if (logRegModelAdmin is not null)
                        {
                            services.AddSingleton(logRegModelAdmin.TrainingService);
                            services.AddSingleton(logRegModelAdmin.MemoryEntryStore);
                            services.AddSingleton(logRegModelAdmin.StorageOptions);
                        }

                        // The Governance UI's System Settings panel API (Phase T6). Same reasoning again:
                        // the settings store, reload token, and options monitor live in the outer
                        // container, so RouterSettingsAdminGrpcService can only be constructed here if
                        // they are handed across explicitly. The embedding memory is optional within the
                        // group - the service takes it as an optional constructor parameter and falls back
                        // to the reactive OnChange trim when it is absent.
                        if (routerSettingsAdmin is not null)
                        {
                            services.AddSingleton(routerSettingsAdmin.Store);
                            services.AddSingleton(routerSettingsAdmin.ReloadToken);
                            services.AddSingleton(routerSettingsAdmin.OptionsMonitor);
                            services.AddSingleton(routerSettingsAdmin.JudgeOptionsMonitor);
                            services.AddSingleton(routerSettingsAdmin.JudgeModelSelector);

                            if (routerSettingsAdmin.EmbeddingMemory is not null)
                            {
                                services.AddSingleton(routerSettingsAdmin.EmbeddingMemory);
                            }
                        }

                        // The Governance UI's "Software Update" section API (Phase 2). Always registered -
                        // see the updateAdmin local's remarks above.
                        services.AddSingleton(updateAdmin?.StateStore ?? new Update.UpdateStateStore());
                        services.AddSingleton(updateAdmin?.ReleaseCheckClient ?? new Update.NullReleaseCheckClient());

                        // The GUI system tray's routing kill switch API. Always registered - see the
                        // routingGateAdmin local's remarks above.
                        services.AddSingleton(routingGateAdmin?.Gate ?? new Router.RoutingGateStore());
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
                            if (priceSourceAdmin is not null)
                            {
                                endpoints.MapGrpcService<PriceSourceAdminGrpcService>();
                            }

                            // The Governance UI's Benchmark Data panel API. Shares the TLS gRPC port with
                            // the telemetry stream and price-source admin service.
                            if (benchmarkDataAdmin is not null)
                            {
                                endpoints.MapGrpcService<CodeRouterBench.BenchmarkDataAdminGrpcService>();
                            }

                            // The Governance UI's Benchmark Data panel's "Local Voter Model" section API.
                            // Shares the TLS gRPC port with the telemetry stream and the other admin services.
                            if (llmRouterModelAdmin is not null)
                            {
                                endpoints.MapGrpcService<Router.TextGeneration.LlmRouterModelAdminGrpcService>();
                            }

                            // The Governance UI's Routing Mode panel API. Shares the TLS gRPC port with the
                            // telemetry stream and the other admin services. Always mapped - see the
                            // routingOptions registration above.
                            endpoints.MapGrpcService<Router.RoutingModeAdminGrpcService>();

                            // The Governance UI's Cluster Model panel API (Phase T5). Shares the TLS gRPC
                            // port with the telemetry stream and the other admin services.
                            if (clusterModelAdmin is not null)
                            {
                                endpoints.MapGrpcService<Router.Orchestrator.ClusterModelAdminGrpcService>();
                            }

                            // The Governance UI's Router Model panel API (live-feedback-learning-plan.md
                            // Phase 5). Shares the TLS gRPC port with the telemetry stream and the other
                            // admin services.
                            if (logRegModelAdmin is not null)
                            {
                                endpoints.MapGrpcService<Router.Orchestrator.LogRegModelAdminGrpcService>();
                            }

                            // The Governance UI's System Settings panel API (Phase T6). Shares the TLS
                            // gRPC port with the telemetry stream and the other admin services.
                            if (routerSettingsAdmin is not null)
                            {
                                endpoints.MapGrpcService<Router.RouterSettingsAdminGrpcService>();
                            }

                            // The Governance UI's "Software Update" section API (Phase 2). Always mapped -
                            // see the updateAdmin local's remarks above.
                            endpoints.MapGrpcService<Update.UpdateAdminGrpcService>();

                            // The GUI system tray's routing kill switch API. Shares the TLS gRPC port with
                            // the telemetry stream and the other admin services. Always mapped - see the
                            // routingGateAdmin local's remarks above.
                            endpoints.MapGrpcService<Router.RoutingGateAdminGrpcService>();

                            // The Governance UI's provider/credential/model management API. Only mapped
                            // when a writable store is supplied; shares this plain-HTTP loopback port with
                            // LLM forwarding (real traffic never targets /admin, so it's never intercepted).
                            // The facade is the same shared core the MCP endpoint's provider tools use -
                            // projection, merging, and credential/header masking live there, not here.
                            if (managementApi is not null)
                            {
                                var facade = new ManagementFacade(
                                    managementApi.ConfigStore,
                                    managementApi.Environment ?? new EnvironmentVariableProvider(),
                                    managementClient,
                                    new ManagementFacadeDependencies
                                    {
                                        BudgetStore = managementApi.BudgetStore,
                                        EndpointScanner = managementApi.EndpointScanner,
                                        CapabilityStore = managementApi.CapabilityStore,
                                        PriceCatalogRepository = managementApi.PriceCatalogRepository,
                                        OverrideStore = managementApi.ModelAliasOverrideStore,
                                        RollupStore = managementApi.UsageRollupStore,
                                        SecretWriter = managementApi.SecretWriter,
                                        SecretReader = managementApi.SecretReader,
                                        ComparisonStore = managementApi.TaxonomyComparisonStore,
                                        // Pure in-memory and dependency-free, so it's constructed directly
                                        // here rather than threaded through ManagementApiOptions like the
                                        // other collaborators above - there is nothing for a caller to wire.
                                        InteractionStatusStore = new ProviderInteractionStatusStore(),
                                    });
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

