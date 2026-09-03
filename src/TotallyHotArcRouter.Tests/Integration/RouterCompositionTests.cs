using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;

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

        await router.ObserveAsync(dimension: "code_gen", model: "gpt-5.4", 0.9);
        await router.ObserveAsync(dimension: "code_gen", model: "qwen3-max", 0.7);

        var decision = await router.SelectModelAsync(dimension: "code_gen",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "gpt-5.4", actual: decision.SelectedModel);
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

        var decision = await router.SelectModelAsync(dimension: "unknown_dimension",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: RouterConstants.DefaultModel, actual: decision.SelectedModel);
        Assert.Equal(expected: RouterConstants.FallbackReason, actual: decision.Rationale);
    }

    [Fact]
    public void RouterComposition_CanResolveCoreServices()
    {
        var provider = BuildProvider(new RoutingOptions());

        Assert.NotNull(provider.GetRequiredService<AgentAsARouter>());
        Assert.NotNull(provider.GetRequiredService<RouterMemory>());
        Assert.NotNull(provider.GetRequiredService<RequestInterceptor>());
        Assert.NotNull(provider.GetRequiredService<IRoutingPolicy>());
    }

    private static ServiceProvider BuildProvider(RoutingOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(options));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddTotallyHotArcRouter();

        return services.BuildServiceProvider();
    }
}