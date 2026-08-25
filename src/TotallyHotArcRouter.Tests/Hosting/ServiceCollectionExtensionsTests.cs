using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers service registration behavior for <see cref="ServiceCollectionExtensions"/>.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTotallyHotArcRouter_RegistersExpectedServiceDescriptors()
    {
        var services = new ServiceCollection();

        services.AddTotallyHotArcRouter();

        Assert.Contains(services, d => d.ServiceType == typeof(IRouterMemoryStore) && d.ImplementationType == typeof(SqliteRouterMemoryStore) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(RouterMemory) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(AgentAsARouter) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(CheckSyntax) && d.Lifetime == ServiceLifetime.Transient);
        Assert.Contains(services, d => d.ServiceType == typeof(IEnvironmentVariableProvider) && d.ImplementationType == typeof(EnvironmentVariableProvider) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IModelRouteResolver) && d.ImplementationType == typeof(ModelRouteResolver) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(RequestInterceptor) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(ProxyMiddleware) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IRoutingPolicy) && d.ImplementationType == typeof(CompositeRoutingPolicy) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(UtilityRoutingPolicy) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(AgentRouterPolicy) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task AddTotallyHotArcRouter_ResolvesRegisteredServices_WithSupportingDependencies()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<RoutingOptions>(_ => { });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddTotallyHotArcRouter();

        // await using, not using: RequestInterceptor now (docs/router/live-feedback-learning-plan.md Phase
        // 2b) takes IEmbeddingClient in its constructor, so resolving it below also constructs
        // OnnxEmbeddingClient, which implements only IAsyncDisposable - a synchronous Dispose() on this
        // scope would throw when it reached that singleton.
        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<RouterMemory>());
        Assert.NotNull(provider.GetRequiredService<CheckSyntax>());
        Assert.NotNull(provider.GetRequiredService<IModelRouteResolver>());
        Assert.NotNull(provider.GetRequiredService<RequestInterceptor>());
        Assert.NotNull(provider.GetRequiredService<ProxyMiddleware>());
        Assert.NotNull(provider.GetRequiredService<AgentAsARouter>());
        Assert.NotNull(provider.GetRequiredService<IRoutingPolicy>());
    }

    [Fact]
    public void AddTotallyHotArcRouter_NoAdminApiKeysConfigured_ResolvesEmptyReconcilerList()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<RoutingOptions>(_ => { });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddTotallyHotArcRouter();

        using var provider = services.BuildServiceProvider();

        Assert.Empty(provider.GetRequiredService<IReadOnlyList<IProviderCostReconciler>>());
    }

    [Fact]
    public void AddTotallyHotArcRouter_OpenAiAdminKeyConfigured_ResolvesOneReconciler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<RoutingOptions>(_ => { });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CostTracking:Reconciliation:Providers:openai:AdminApiKeyEnvVar"] = "TEST_OPENAI_ADMIN_KEY",
            })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddTotallyHotArcRouter();

        // Registered after AddTotallyHotArcRouter (which registers its own EnvironmentVariableProvider
        // default) so this stub wins the last-registration-wins resolution for IEnvironmentVariableProvider.
        services.AddSingleton<IEnvironmentVariableProvider>(new StubEnvironmentVariableProvider(
            new Dictionary<string, string> { ["TEST_OPENAI_ADMIN_KEY"] = "sk-admin-test" }));

        using var provider = services.BuildServiceProvider();
        var reconcilers = provider.GetRequiredService<IReadOnlyList<IProviderCostReconciler>>();

        var reconciler = Assert.Single(reconcilers);
        Assert.Equal("openai", reconciler.Provider);
    }

    private sealed class StubEnvironmentVariableProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetVariable(string name) => values.GetValueOrDefault(name);
    }
}

