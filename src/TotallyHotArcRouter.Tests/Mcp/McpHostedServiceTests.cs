using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Net.Sockets;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Mcp;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Mcp;

/// <summary>
/// Covers <see cref="McpHostedService"/>'s start-failure behavior: MCP is a management convenience rather
/// than a dependency of core proxying, so a port it cannot bind must leave the router running and must be
/// reported as one actionable line rather than a Kestrel stack.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WhenThePortIsAlreadyInUse_LogsOneWarningWithoutTheStack()
    {
        // Take an ephemeral port and hold it, so the bind is guaranteed to collide without hard-coding a
        // port that might be free or busy on someone else's machine.
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<RoutingOptions>(_ => { });
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddTotallyHotArcRouter();

            using var provider = services.BuildServiceProvider();

            var loggerMock = new Mock<ILogger<McpHostedService>>();
            await using var hostedService = ActivatorUtilities.CreateInstance<McpHostedService>(
                provider,
                loggerMock.Object,
                Options.Create(new McpOptions { Enabled = true, Port = port }));

            // The whole point: MCP failing to bind must not take the router down with it.
            await hostedService.StartAsync(TestContext.Current.CancellationToken);
            await hostedService.StopAsync(TestContext.Current.CancellationToken);

            // The null exception argument is the assertion that matters: passing the exception is what
            // makes the logger render the full Kestrel stack.
            loggerMock.Verify(
                logger => logger.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) =>
                        state.ToString()!.Contains("The MCP endpoint could not start", StringComparison.Ordinal)),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        finally
        {
            occupied.Stop();
            occupied.Dispose();
        }
    }
}
