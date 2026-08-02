using TotallyHot.ArcRouter.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Linq;
using System.Net.Sockets;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers lifecycle behavior for <see cref="ProxyServer"/>.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait("Category", "Integration")]
public class ProxyServerTests
{
    [Fact]
    public async Task ProxyServer_Starts_AcceptsConnection_AndStops()
    {
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware = new ProxyMiddleware(NullLogger<ProxyMiddleware>.Instance, interceptor);

        // grpcPort: 0 too - without this, the TLS/gRPC listener would still bind the fixed default
        // port (ProxyServer.DefaultGrpcPort) even in this ephemeral-port test, defeating the point of
        // port: 0 (test-to-test port-conflict flakiness) and generating/persisting a real self-signed
        // certificate under %LOCALAPPDATA% on every test run.
        using var server = new ProxyServer(new NullLogger<ProxyServer>(), proxyMiddleware, port: 0, grpcPort: 0);

        await server.StartAsync(TestContext.Current.CancellationToken);

        // Two listeners now (plain HTTP for LLM-forwarding, HTTPS for gRPC) - pick the plain HTTP one,
        // since that's what this test's plain TcpClient connection exercises.
        var boundPort = new Uri(server.Addresses.Single(a => a.StartsWith("http://", StringComparison.Ordinal))).Port;

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("127.0.0.1", boundPort, TestContext.Current.CancellationToken);

        Assert.True(tcpClient.Connected);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await server.StopAsync(stopCts.Token);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Constructor_PortOutOfRange_ThrowsArgumentOutOfRangeException(int port)
    {
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware = new ProxyMiddleware(NullLogger<ProxyMiddleware>.Instance, interceptor);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyServer(new NullLogger<ProxyServer>(), proxyMiddleware, port));
    }
}

