using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Net.Sockets;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers the web GUI migration plan Phase P2 exit criteria: the web-interface listener serves gRPC-Web
/// (unary and server-streaming) correctly, while port scoping keeps gRPC/admin unreachable from the
/// plain-HTTP proxy ports and keeps real LLM-forwarding traffic unreachable from the web port. Drives a
/// real bound <see cref="ProxyServer"/> over the network - port scoping and the gRPC-Web wrapper are
/// properties of the actual Kestrel pipeline, not something a fake <see cref="ServerCallContext"/> could
/// exercise (see <see cref="TotallyHot.ArcRouter.Tests.Telemetry.TelemetryGrpcServiceTests"/> for that
/// lighter style, used where the distinction doesn't matter).
/// </summary>
[Collection("ProxyLifecycle")]
[Trait(name: "Category", value: "Integration")]
public sealed class ProxyServerWebInterfaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // GetRoutingMode has no dependencies (RoutingModeAdminGrpcService is always mapped - see
    // ProxyServer's remarks) and StreamEvents needs only the shared broadcaster, so both are reachable
    // off the same minimal server build every test below uses.
    private static readonly Method<Contract.GetRoutingModeRequest, Contract.RoutingModeResponse> GetRoutingModeMethod =
        new(
            type: MethodType.Unary,
            serviceName: "TotallyHot.ArcRouter.telemetry.v1.RoutingModeAdminService",
            name: "GetRoutingMode",
            requestMarshaller: CreateMarshaller(Contract.GetRoutingModeRequest.Parser),
            responseMarshaller: CreateMarshaller(Contract.RoutingModeResponse.Parser));

    private static readonly Method<Contract.StreamEventsRequest, Contract.TelemetryEvent> StreamEventsMethod = new(
        type: MethodType.ServerStreaming,
        serviceName: "TotallyHot.ArcRouter.telemetry.v1.TelemetryService",
        name: "StreamEvents",
        requestMarshaller: CreateMarshaller(Contract.StreamEventsRequest.Parser),
        responseMarshaller: CreateMarshaller(Contract.TelemetryEvent.Parser));

    private static Marshaller<T> CreateMarshaller<T>(MessageParser<T> parser) where T : IMessage<T>
    {
        return Marshallers.Create(serializer: m => m.ToByteArray(), deserializer: parser.ParseFrom);
    }

    /// <summary>
    /// Reserves <paramref name="count"/> distinct free loopback ports by briefly binding a
    /// <see cref="TcpListener"/> to each (port 0 asks the OS for an unused one) and releasing them
    /// together - the same trick <see cref="Hosting.ProxyHostedServiceTests"/> uses for its
    /// port-already-in-use test. Needed here (rather than each listener taking its own port: 0) because
    /// several of these tests must know in advance which fixed port is the web port vs. the two
    /// proxy-purpose ports, to target requests precisely.
    /// </summary>
    private static int[] GetFreePorts(int count)
    {
        var listeners = new TcpListener[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                listeners[i] = new TcpListener(IPAddress.Loopback, 0);
                listeners[i].Start();
            }

            return [.. listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (var listener in listeners) listener?.Stop();
        }
    }

    private static (ProxyServer Server, TelemetryBroadcaster Broadcaster, int ProxyPort, int WebPort,
        int PlainHttpPort) BuildServer(bool plainHttpEnabled = false)
    {
        var ports = GetFreePorts(plainHttpEnabled ? 3 : 2);
        var proxyPort = ports[0];
        var webPort = ports[1];
        var plainHttpPort = plainHttpEnabled ? ports[2] : 0;

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware =
            new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance, interceptor: interceptor);
        var broadcaster = new TelemetryBroadcaster();

        var server = new ProxyServer(
            logger: NullLogger<ProxyServer>.Instance,
            proxyMiddleware: proxyMiddleware,
            listenerOptions: new ProxyListenerOptions
            {
                Port = proxyPort,
                PlainHttp = new PlainHttpListenerOptions { Enabled = plainHttpEnabled, Port = plainHttpPort }
            },
            webInterfaceOptions: new WebInterfaceOptions { Port = webPort },
            dependencies: new ProxyServerDependencies
            {
                Telemetry = broadcaster,
                // TelemetryGrpcService.StreamEvents (mapped unconditionally, like RoutingModeAdminGrpcService)
                // needs an ITranscriptStore/IOptions<TranscriptOptions> in the inner container to be
                // constructible at all, but - unlike RoutingGateStore/UpdateStateStore/etc. - ProxyServer has
                // no unconditional fallback registration for them; production always supplies ClusterModelAdmin
                // (see AddProxyHost), which happens to carry both. Neither is ever called by StreamEvents
                // itself, so loose Moq stubs are enough to satisfy DI construction here.
                ClusterModelAdmin = new ClusterModelAdminDependencies(
                    TrainingService: Mock.Of<IClusterTrainingService>(),
                    MemoryEntryStore: Mock.Of<IMemoryEntryStore>(),
                    TranscriptStore: Mock.Of<ITranscriptStore>(),
                    TranscriptOptions: Options.Create(new TranscriptOptions()),
                    StorageOptions: Options.Create(new StorageOptions()))
            });

        return (server, broadcaster, proxyPort, webPort, plainHttpPort);
    }

    /// <summary>An <see cref="HttpClient"/> that trusts any certificate - the self-signed dev cert isn't otherwise trusted in a test process.</summary>
    private static HttpClient TrustingHttpClient(GrpcWebMode mode = GrpcWebMode.GrpcWeb)
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        return new HttpClient(new GrpcWebHandler(mode: mode, innerHandler: handler));
    }

    [Fact]
    public async Task WebPort_GrpcWebUnaryCall_Succeeds()
    {
        var (server, _, _, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var httpClient = TrustingHttpClient();
            using var channel = GrpcChannel.ForAddress($"https://localhost:{webPort}",
                new GrpcChannelOptions { HttpClient = httpClient });
            var invoker = channel.CreateCallInvoker();

            var response = await invoker.AsyncUnaryCall(method: GetRoutingModeMethod, host: null,
                options: new CallOptions(cancellationToken: Ct), new Contract.GetRoutingModeRequest());

            Assert.NotNull(response);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task WebPort_GrpcWebStreamEvents_ReceivesAtLeastTwoMessages()
    {
        var (server, broadcaster, _, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var httpClient = TrustingHttpClient();
            using var channel = GrpcChannel.ForAddress($"https://localhost:{webPort}",
                new GrpcChannelOptions { HttpClient = httpClient });
            var invoker = channel.CreateCallInvoker();

            using var call = invoker.AsyncServerStreamingCall(method: StreamEventsMethod, host: null,
                options: new CallOptions(cancellationToken: Ct), new Contract.StreamEventsRequest());

            // Give the subscription a moment to register before publishing, mirroring how a real dashboard
            // connects first and then observes live events - a publish before the subscriber is attached
            // would otherwise be missed and make this test flaky, not the plumbing under test.
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
            broadcaster.PublishLogLine(new LogLineEvent(TimestampUtc: DateTimeOffset.UtcNow, Level: "Information",
                Message: "probe-1"));
            broadcaster.PublishLogLine(new LogLineEvent(TimestampUtc: DateTimeOffset.UtcNow, Level: "Information",
                Message: "probe-2"));

            var received = 0;
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (received < 2 && await call.ResponseStream.MoveNext(readCts.Token))
                    received++;
            }
            catch (OperationCanceledException) when (readCts.IsCancellationRequested && !Ct.IsCancellationRequested)
            {
                // Timed out waiting - received below reports how far it got.
            }

            Assert.True(received >= 2, $"Expected at least 2 streamed messages, got {received}.");
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task ProxyPort_GrpcWebCall_DoesNotReachAService()
    {
        var (server, _, proxyPort, _, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            // The primary proxy port is TLS by default since web GUI migration plan Phase P7 (ADR-0013) -
            // trusting the test/dev cert here, not asserting anything about OS trust.
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var httpClient = new HttpClient(handler);
            var response = await httpClient.PostAsync(
                requestUri: $"https://localhost:{proxyPort}/TotallyHot.ArcRouter.telemetry.v1.RoutingModeAdminService/GetRoutingMode",
                content: new ByteArrayContent([0, 0, 0, 0, 0]) { Headers = { ContentType = new("application/grpc-web+proto") } },
                cancellationToken: Ct);

            // Never a grpc-web response - the request was routed straight into proxyMiddleware, which (with
            // no configured providers) answers with its own not-found/bad-request shape, not the gRPC-Web
            // framing the service would produce.
            Assert.NotEqual(expected: "application/grpc-web+proto", actual: response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task WebPort_RestAdminPath_Returns404()
    {
        var (server, _, _, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var trustingClient = new HttpClient(handler);
            var response = await trustingClient.GetAsync(requestUri: $"https://localhost:{webPort}/admin/providers",
                cancellationToken: Ct);

            Assert.Equal(expected: HttpStatusCode.NotFound, actual: response.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task WebPort_ProxyPath_Returns404()
    {
        var (server, _, _, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var trustingClient = new HttpClient(handler);
            var response = await trustingClient.GetAsync(requestUri: $"https://localhost:{webPort}/v1/chat/completions",
                cancellationToken: Ct);

            Assert.Equal(expected: HttpStatusCode.NotFound, actual: response.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task WebPort_Root_ServesTheWasmDashboard()
    {
        // Web GUI migration plan Phase P6: the WASM dashboard is served on the web port via a
        // ReferenceOutputAssembly="false" ProjectReference to Gui.Web (S2's mitigation for the
        // CS0433 proto-codegen collision) plus UseStaticWebAssets/UseDefaultFiles/UseStaticFiles in
        // ProxyServer.cs. This is the regression guard for that pipeline actually resolving Gui.Web's
        // static web assets at runtime - the exact thing the plan flagged as unverified by spike S2.
        var (server, _, _, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var trustingClient = new HttpClient(handler);

            var index = await trustingClient.GetAsync(requestUri: $"https://localhost:{webPort}/", cancellationToken: Ct);
            Assert.Equal(expected: HttpStatusCode.OK, actual: index.StatusCode);
            Assert.StartsWith(expectedStartString: "text/html",
                actualString: index.Content.Headers.ContentType?.MediaType, comparisonType: StringComparison.Ordinal);

            var script = await trustingClient.GetAsync(requestUri: $"https://localhost:{webPort}/_framework/blazor.webassembly.js",
                cancellationToken: Ct);
            Assert.Equal(expected: HttpStatusCode.OK, actual: script.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task NativeGrpcOnWebPort_UnaryCall_Succeeds()
    {
        // Phase P9 retired the formerly-dedicated native-gRPC port: native (non-grpc-web) gRPC now shares
        // the web port with gRPC-Web and the WASM static assets. This proves that merge didn't break
        // plain trailers-based gRPC framing on the shared port.
        var (server, _, _, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var httpClient = new HttpClient(handler);
            using var channel = GrpcChannel.ForAddress($"https://localhost:{webPort}",
                new GrpcChannelOptions { HttpClient = httpClient });
            var invoker = channel.CreateCallInvoker();

            var response = await invoker.AsyncUnaryCall(method: GetRoutingModeMethod, host: null,
                options: new CallOptions(cancellationToken: Ct), new Contract.GetRoutingModeRequest());

            Assert.NotNull(response);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task PlainHttpListener_WhenEnabled_ServesProxyPath_ButNotGrpc()
    {
        var (server, _, proxyPort, _, plainHttpPort) = BuildServer(plainHttpEnabled: true);
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            // Both the primary proxy port (TLS since Phase P7) and the opt-in plain-HTTP one must behave
            // identically as far as routing goes - proxy traffic reaches proxyMiddleware, gRPC/admin is
            // unreachable on either - even though only one of them is encrypted.
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var httpClient = new HttpClient(handler);
            foreach (var (scheme, port) in new[] { ("https", proxyPort), ("http", plainHttpPort) })
            {
                var response = await httpClient.PostAsync(
                    requestUri: $"{scheme}://localhost:{port}/TotallyHot.ArcRouter.telemetry.v1.RoutingModeAdminService/GetRoutingMode",
                    content: new ByteArrayContent([0, 0, 0, 0, 0]) { Headers = { ContentType = new("application/grpc-web+proto") } },
                    cancellationToken: Ct);

                Assert.NotEqual(expected: "application/grpc-web+proto", actual: response.Content.Headers.ContentType?.MediaType);
            }
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }
}
