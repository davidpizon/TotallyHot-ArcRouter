using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Mcp;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Router;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Mcp;

/// <summary>
/// Covers the MCP endpoint's registration in <see cref="ServiceCollectionExtensions.AddTotallyHotArcRouter"/>:
/// the shared <see cref="ManagementFacade"/>, bound <see cref="McpOptions"/>, and
/// <see cref="McpHostedService"/> all land in the outer container. The MCP tool types themselves are
/// registered inside <see cref="McpServer"/>'s own inner host container (mirroring how
/// <c>TelemetryGrpcService</c>/<c>PriceSourceAdminGrpcService</c> live only in
/// <c>TotallyHot.ArcRouter.Proxy.ProxyServer</c>'s inner container), so they are not resolvable from the outer
/// provider and are covered instead by the <c>Mcp.Tools</c> unit tests.
/// </summary>
public sealed class McpServiceRegistrationTests
{
    [Fact]
    public void AddTotallyHotArcRouter_RegistersManagementFacadeAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddTotallyHotArcRouter();

        Assert.Contains(services, d => d.ServiceType == typeof(ManagementFacade) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddTotallyHotArcRouter_RegistersMcpHostedService()
    {
        var services = new ServiceCollection();

        services.AddTotallyHotArcRouter();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(McpHostedService));
    }

    [Fact]
    public void AddTotallyHotArcRouter_McpOptions_DefaultsToEnabledOnPort5003()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<RoutingOptions>(_ => { });
        services.AddSingleton<IRouterModelClient>(Moq.Mock.Of<IRouterModelClient>());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddTotallyHotArcRouter();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(5003, options.Port);
    }

    [Fact]
    public void AddTotallyHotArcRouter_ManagementFacade_ResolvesWithSupportingDependencies()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<RoutingOptions>(_ => { });
        services.AddSingleton<IRouterModelClient>(Moq.Mock.Of<IRouterModelClient>());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddTotallyHotArcRouter();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ManagementFacade>());
    }
}

