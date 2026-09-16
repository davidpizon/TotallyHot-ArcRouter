using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Auth;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.Proxy.Management;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers ADR-0012/Phase P4's exit matrix over a real bound <see cref="ProxyServer"/> - the Host/Origin
/// guard, loopback session issuance, token login with throttling, and rotation invalidating an
/// outstanding session - the same "drive the real Kestrel pipeline over the network" style
/// <see cref="ProxyServerWebInterfaceTests"/> uses, for the same reason: these are properties of the
/// actual pipeline (cookies, TLS, connection-level routing), not something a fake context could exercise.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait(name: "Category", value: "Integration")]
public sealed class ProxyServerAuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Method<Contract.GetRoutingModeRequest, Contract.RoutingModeResponse> GetRoutingModeMethod =
        new(
            type: MethodType.Unary,
            serviceName: "TotallyHot.ArcRouter.telemetry.v1.RoutingModeAdminService",
            name: "GetRoutingMode",
            requestMarshaller: Marshallers.Create(serializer: m => m.ToByteArray(),
                deserializer: Contract.GetRoutingModeRequest.Parser.ParseFrom),
            responseMarshaller: Marshallers.Create(serializer: m => m.ToByteArray(),
                deserializer: Contract.RoutingModeResponse.Parser.ParseFrom));

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

    private static (ProxyServer Server, int WebPort, IManagementTokenProvider TokenProvider) BuildServer(
        bool trustLoopback = true)
    {
        var ports = GetFreePorts(2);
        var proxyPort = ports[0];
        var webPort = ports[1];

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware =
            new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance, interceptor: interceptor);
        var tokenProvider = new FakeManagementTokenProvider("expected-token");

        var server = new ProxyServer(
            logger: NullLogger<ProxyServer>.Instance,
            proxyMiddleware: proxyMiddleware,
            listenerOptions: new ProxyListenerOptions { Port = proxyPort },
            webInterfaceOptions: new WebInterfaceOptions { Port = webPort, TrustLoopback = trustLoopback },
            dependencies: new ProxyServerDependencies
            {
                Telemetry = new TelemetryBroadcaster(),
                ManagementTokenProvider = tokenProvider,
                // Same reasoning as ProxyServerWebInterfaceTests.BuildServer: TelemetryGrpcService needs
                // these to be constructible at all, even though this suite never calls it.
                ClusterModelAdmin = new ClusterModelAdminDependencies(
                    TrainingService: Mock.Of<IClusterTrainingService>(),
                    MemoryEntryStore: Mock.Of<IMemoryEntryStore>(),
                    TranscriptStore: Mock.Of<ITranscriptStore>(),
                    TranscriptOptions: Options.Create(new TranscriptOptions()),
                    StorageOptions: Options.Create(new StorageOptions()))
            });

        return (server, webPort, tokenProvider);
    }

    private static HttpClientHandler TrustingHandler(CookieContainer? cookies = null)
    {
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            CookieContainer = cookies ?? new CookieContainer()
        };
    }

    [Fact]
    public async Task RequestGuard_AttackerHost_Returns403()
    {
        var (server, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient(TrustingHandler());
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{webPort}/auth/session");
            request.Headers.Host = "attacker.test";

            var response = await client.SendAsync(request, Ct);

            Assert.Equal(expected: HttpStatusCode.Forbidden, actual: response.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task RequestGuard_ForeignOrigin_Returns403()
    {
        var (server, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient(TrustingHandler());
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{webPort}/auth/session");
            request.Headers.Add("Origin", "https://evil.example");

            var response = await client.SendAsync(request, Ct);

            Assert.Equal(expected: HttpStatusCode.Forbidden, actual: response.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task AuthSession_LoopbackCaller_IssuesCookieThatAuthorizesGrpc()
    {
        var (server, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            var cookies = new CookieContainer();
            using var handler = TrustingHandler(cookies);
            using var authClient = new HttpClient(handler, disposeHandler: false);

            var sessionResponse = await authClient.PostAsync($"https://localhost:{webPort}/auth/session",
                content: null, cancellationToken: Ct);
            Assert.Equal(expected: HttpStatusCode.NoContent, actual: sessionResponse.StatusCode);

            using var grpcHttpClient = new HttpClient(new GrpcWebHandler(mode: GrpcWebMode.GrpcWeb, innerHandler: handler));
            using var channel = GrpcChannel.ForAddress($"https://localhost:{webPort}",
                new GrpcChannelOptions { HttpClient = grpcHttpClient });
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
    public async Task AuthSession_WithTrustLoopbackDisabled_Returns403()
    {
        var (server, webPort, _) = BuildServer(trustLoopback: false);
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient(TrustingHandler());

            var response = await client.PostAsync($"https://localhost:{webPort}/auth/session", content: null, Ct);

            Assert.Equal(expected: HttpStatusCode.Forbidden, actual: response.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task GrpcCall_NoCredential_IsUnauthenticated()
    {
        var (server, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var handler = TrustingHandler();
            using var grpcHttpClient = new HttpClient(new GrpcWebHandler(mode: GrpcWebMode.GrpcWeb, innerHandler: handler));
            using var channel = GrpcChannel.ForAddress($"https://localhost:{webPort}",
                new GrpcChannelOptions { HttpClient = grpcHttpClient });
            var invoker = channel.CreateCallInvoker();

            var exception = await Assert.ThrowsAsync<RpcException>(() => invoker.AsyncUnaryCall(
                method: GetRoutingModeMethod, host: null, options: new CallOptions(cancellationToken: Ct),
                new Contract.GetRoutingModeRequest()).ResponseAsync);

            Assert.Equal(expected: StatusCode.Unauthenticated, actual: exception.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task AuthLogin_CorrectToken_IssuesCookie()
    {
        var (server, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient(TrustingHandler());

            var response = await client.PostAsJsonAsync($"https://localhost:{webPort}/auth/login",
                new { token = "expected-token" }, Ct);

            Assert.Equal(expected: HttpStatusCode.NoContent, actual: response.StatusCode);
            Assert.Contains(response.Headers, h => h.Key == "Set-Cookie");
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task AuthLogin_WrongToken_Returns401()
    {
        var (server, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient(TrustingHandler());

            var response = await client.PostAsJsonAsync($"https://localhost:{webPort}/auth/login",
                new { token = "wrong-token" }, Ct);

            Assert.Equal(expected: HttpStatusCode.Unauthorized, actual: response.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task AuthLogin_RepeatedWrongTokens_EventuallyThrottles()
    {
        var (server, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient(TrustingHandler());
            HttpResponseMessage? last = null;

            for (var i = 0; i < LoginRateLimiter.MaxAttempts + 1; i++)
                last = await client.PostAsJsonAsync($"https://localhost:{webPort}/auth/login",
                    new { token = "wrong-token" }, Ct);

            Assert.Equal(expected: HttpStatusCode.TooManyRequests, actual: last!.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task TokenRotation_InvalidatesAnOutstandingSessionCookie()
    {
        var (server, webPort, tokenProvider) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            var cookies = new CookieContainer();
            using var handler = TrustingHandler(cookies);
            using var authClient = new HttpClient(handler, disposeHandler: false);
            var sessionResponse = await authClient.PostAsync($"https://localhost:{webPort}/auth/session",
                content: null, cancellationToken: Ct);
            Assert.Equal(expected: HttpStatusCode.NoContent, actual: sessionResponse.StatusCode);

            tokenProvider.Regenerate();

            using var grpcHttpClient = new HttpClient(new GrpcWebHandler(mode: GrpcWebMode.GrpcWeb, innerHandler: handler));
            using var channel = GrpcChannel.ForAddress($"https://localhost:{webPort}",
                new GrpcChannelOptions { HttpClient = grpcHttpClient });
            var invoker = channel.CreateCallInvoker();

            var exception = await Assert.ThrowsAsync<RpcException>(() => invoker.AsyncUnaryCall(
                method: GetRoutingModeMethod, host: null, options: new CallOptions(cancellationToken: Ct),
                new Contract.GetRoutingModeRequest()).ResponseAsync);

            Assert.Equal(expected: StatusCode.Unauthenticated, actual: exception.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task AuthLogout_ClearsTheCookie()
    {
        var (server, webPort, _) = BuildServer();
        await using var _ = server;
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient(TrustingHandler());

            var response = await client.PostAsync($"https://localhost:{webPort}/auth/logout", content: null, Ct);

            Assert.Equal(expected: HttpStatusCode.NoContent, actual: response.StatusCode);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }
}
