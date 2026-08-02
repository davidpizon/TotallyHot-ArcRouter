using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Tests.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
        var proxyLogger = new NullLogger<ProxyServer>();
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware = new ProxyMiddleware(NullLogger<ProxyMiddleware>.Instance, interceptor);

        // grpcPort: 0 too - see ProxyServerTests.cs's matching comment for why (avoids fixed-port
        // flakiness and generating/persisting a real self-signed certificate during unit test runs).
        var hostedService = new ProxyHostedService(loggerMock.Object, proxyLogger, proxyMiddleware, port: 0, grpcPort: 0);

        await hostedService.StartAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await hostedService.StopAsync(cts.Token);

        VerifyLogContains(loggerMock, LogLevel.Information, "Proxy Hosted Service is starting.");
        VerifyLogContains(loggerMock, LogLevel.Information, "Proxy Hosted Service is stopping.");
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

