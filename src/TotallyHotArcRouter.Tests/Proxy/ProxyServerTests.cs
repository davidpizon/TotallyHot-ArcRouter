using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers lifecycle behavior for <see cref="ProxyServer"/>.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait(name: "Category", value: "Integration")]
public class ProxyServerTests
{
    [Fact]
    public async Task ProxyServer_Starts_AcceptsConnection_AndStops()
    {
        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware =
            new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance, interceptor: interceptor);

        // grpcPort: 0 too - without this, the TLS/gRPC listener would still bind the fixed default
        // port (ProxyServer.DefaultGrpcPort) even in this ephemeral-port test, defeating the point of
        // port: 0 (test-to-test port-conflict flakiness) and generating/persisting a real self-signed
        // certificate under %LOCALAPPDATA% on every test run.
        await using var server =
            new ProxyServer(logger: new NullLogger<ProxyServer>(), proxyMiddleware: proxyMiddleware, 0, 0);

        await server.StartAsync(TestContext.Current.CancellationToken);

        // Two listeners now (plain HTTP for LLM-forwarding, HTTPS for gRPC) - pick the plain HTTP one,
        // since that's what this test's plain TcpClient connection exercises.
        var boundPort =
            new Uri(server.Addresses.Single(a =>
                a.StartsWith(value: "http://", comparisonType: StringComparison.Ordinal))).Port;

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host: "127.0.0.1", port: boundPort,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(tcpClient.Connected);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await server.StopAsync(stopCts.Token);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Constructor_PortOutOfRange_ThrowsArgumentOutOfRangeException(int port)
    {
        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware =
            new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance, interceptor: interceptor);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProxyServer(logger: new NullLogger<ProxyServer>(), proxyMiddleware: proxyMiddleware, port: port));
    }
}