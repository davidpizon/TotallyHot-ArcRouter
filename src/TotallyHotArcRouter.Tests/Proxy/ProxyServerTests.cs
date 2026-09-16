using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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

        await using var server = new ProxyServer(logger: new NullLogger<ProxyServer>(),
            proxyMiddleware: proxyMiddleware, listenerOptions: new ProxyListenerOptions { Port = 0 },
            webInterfaceOptions: new WebInterfaceOptions { Port = 0 });

        await server.StartAsync(TestContext.Current.CancellationToken);

        // Two listeners now, both HTTPS since Phase P7 (ADR-0013) - either one proves the lifecycle
        // this test cares about (starts, accepts a connection, stops); this is a raw TCP connect with
        // no TLS handshake, so which listener answers doesn't matter.
        var boundPort = new Uri(server.Addresses.First()).Port;

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
            new ProxyServer(logger: new NullLogger<ProxyServer>(), proxyMiddleware: proxyMiddleware,
                listenerOptions: new ProxyListenerOptions { Port = port }));
    }

    [Fact]
    public async Task ProxyServer_BindAddressAny_StartsAndAcceptsConnection()
    {
        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: ModelRouteResolverTestFactory.Empty());
        var proxyMiddleware =
            new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance, interceptor: interceptor);

        // "any" is the Docker-facing bind mode (D6a): port 0 still binds a single representative
        // address (see KestrelBindAddress's remarks), so this exercises that resolution path without
        // depending on a specific non-loopback interface being present on the test runner.
        await using var server = new ProxyServer(logger: new NullLogger<ProxyServer>(),
            proxyMiddleware: proxyMiddleware,
            listenerOptions: new ProxyListenerOptions { Port = 0, BindAddress = "any" },
            webInterfaceOptions: new WebInterfaceOptions { Port = 0 });

        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var boundPort = new Uri(server.Addresses.First()).Port;

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host: "127.0.0.1", port: boundPort,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(tcpClient.Connected);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(stopCts.Token);
        }
    }

    /// <summary>
    /// Web GUI migration plan Phase P1's exit criterion: the inner host's content root must come from
    /// <c>AppContext.BaseDirectory</c>, not the process's current directory - otherwise static-asset
    /// serving (Phase P2/P6) 404s under the installed Windows service, whose working directory is
    /// <c>C:\Windows\System32</c>.
    /// </summary>
    [Fact]
    public async Task ProxyServer_ContentRootIsIndependentOfCurrentDirectory()
    {
        // Environment.CurrentDirectory is process-global, and ASP.NET Core's own default content root
        // (Directory.GetCurrentDirectory()) already coincides with AppContext.BaseDirectory when running
        // a built test executable - so asserting equality alone, with cwd left untouched, would pass
        // even without the UseContentRoot fix this test exists to guard. Changing cwd for this test's
        // short, try/finally-bounded window is the only way to make that distinction observable; this
        // class's [Collection("ProxyLifecycle")] attribute is what keeps it from interleaving with any
        // other test in that collection while it does.
        var originalCwd = Environment.CurrentDirectory;
        var tempCwd = Directory.CreateTempSubdirectory().FullName;

        try
        {
            Environment.CurrentDirectory = tempCwd;

            var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
                modelRouteResolver: ModelRouteResolverTestFactory.Empty());
            var proxyMiddleware =
                new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance, interceptor: interceptor);

            await using var server = new ProxyServer(logger: new NullLogger<ProxyServer>(),
                proxyMiddleware: proxyMiddleware,
                listenerOptions: new ProxyListenerOptions { Port = 0 },
                webInterfaceOptions: new WebInterfaceOptions { Port = 0 });

            await server.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                var environment = server.Services.GetRequiredService<IWebHostEnvironment>();

                Assert.Equal(
                    expected: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    actual: environment.ContentRootPath.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
                Assert.NotEqual(expected: tempCwd, actual: environment.ContentRootPath);
            }
            finally
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await server.StopAsync(stopCts.Token);
            }
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Directory.Delete(path: tempCwd, recursive: true);
        }
    }
}