using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Net.Sockets;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Tests.Proxy;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers hosted service lifecycle behavior for <see cref="ProxyHostedService"/>.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait("Category", "Integration")]
public class ProxyHostedServiceTests
{
    [Fact]
    public async Task StartAndStopAsync_StartsAndStopsProxy_AndLogsLifecycle()
    {
        var loggerMock = new Mock<ILogger<ProxyHostedService>>();

        // grpcPort: 0 too - see ProxyServerTests.cs's matching comment for why (avoids fixed-port
        // flakiness and generating/persisting a real self-signed certificate during unit test runs).
        var hostedService = CreateService(loggerMock, Mock.Of<IHostApplicationLifetime>(), port: 0);

        await hostedService.StartAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await hostedService.StopAsync(cts.Token);

        VerifyLogContains(loggerMock, LogLevel.Information, "Proxy Hosted Service is starting.");
        VerifyLogContains(loggerMock, LogLevel.Information, "Proxy Hosted Service is stopping.");
    }

    [Fact]
    public async Task StartAsync_WhenThePortIsAlreadyInUse_LogsOneErrorAndStopsTheHost()
    {
        // Take an ephemeral port and hold it, so the proxy's bind is guaranteed to collide without
        // hard-coding a port that might be free or busy on someone else's machine.
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        var loggerMock = new Mock<ILogger<ProxyHostedService>>();
        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        var originalExitCode = Environment.ExitCode;

        try
        {
            var hostedService = CreateService(loggerMock, lifetimeMock.Object, port);

            // The whole point: a taken port is an operator condition, so StartAsync must not throw.
            await hostedService.StartAsync(TestContext.Current.CancellationToken);

            // StopAsync must stay quiet too - nothing was ever listening to stop.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await hostedService.StopAsync(cts.Token);

            VerifyLogContains(loggerMock, LogLevel.Error, "The proxy could not start");
            lifetimeMock.Verify(lifetime => lifetime.StopApplication(), Times.Once);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            occupied.Stop();
            occupied.Dispose();
        }
    }

    private static ProxyHostedService CreateService(
        Mock<ILogger<ProxyHostedService>> loggerMock,
        IHostApplicationLifetime lifetime,
        int port)
    {
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware = new ProxyMiddleware(NullLogger<ProxyMiddleware>.Instance, interceptor);

        return new ProxyHostedService(
            loggerMock.Object,
            NullLogger<ProxyServer>.Instance,
            proxyMiddleware,
            lifetime,
            port: port,
            grpcPort: 0);
    }

    private static void VerifyLogContains(Mock<ILogger<ProxyHostedService>> loggerMock, LogLevel level, string expectedText)
    {
        loggerMock.Verify(
            logger => logger.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(expectedText, StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
