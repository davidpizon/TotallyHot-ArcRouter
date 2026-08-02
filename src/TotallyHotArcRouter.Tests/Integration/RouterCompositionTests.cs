using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Integration;

/// <summary>
/// Covers integration composition for router services.
/// </summary>
[Collection("Integration")]
public class RouterCompositionTests
{
    [Fact]
    public async Task RouterComposition_ObserveThenRoute_SelectsBestModel()
    {
        var provider = BuildProvider(new RoutingOptions
        {
            EnableExploration = false,
            DefaultModel = RouterConstants.DefaultModel
        });

        var router = provider.GetRequiredService<AgentAsARouter>();

        await router.ObserveAsync("code_gen", "gpt-5.4", 0.9);
        await router.ObserveAsync("code_gen", "qwen3-max", 0.7);

        var result = await router.RouteAsync("prompt", "code_gen", TestContext.Current.CancellationToken);

        Assert.Equal("gpt-5.4", result.Decision.SelectedModel);
        Assert.Equal("response:gpt-5.4", result.Response);
    }

    [Fact]
    public async Task RouterComposition_WithoutHistory_UsesFallbackDefaultModel()
    {
        var provider = BuildProvider(new RoutingOptions
        {
            EnableExploration = false,
            DefaultModel = RouterConstants.DefaultModel
        });

        var router = provider.GetRequiredService<AgentAsARouter>();

        var result = await router.RouteAsync("prompt", "unknown_dimension", TestContext.Current.CancellationToken);

        Assert.Equal(RouterConstants.DefaultModel, result.Decision.SelectedModel);
        Assert.Equal(RouterConstants.FallbackReason, result.Decision.Rationale);
    }

    [Fact]
    public void RouterComposition_CanResolveCoreServices()
    {
        var provider = BuildProvider(new RoutingOptions());

        Assert.NotNull(provider.GetRequiredService<AgentAsARouter>());
        Assert.NotNull(provider.GetRequiredService<RouterMemory>());
        Assert.NotNull(provider.GetRequiredService<RequestInterceptor>());
    }

    private static ServiceProvider BuildProvider(RoutingOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(options));
        services.AddSingleton<IRouterModelClient, StubRouterModelClient>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddTotallyHotArcRouter();

        return services.BuildServiceProvider();
    }

    private sealed class StubRouterModelClient : IRouterModelClient
    {
        public Task<string> GetResponseAsync(string model, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult($"response:{model}");
    }
}

