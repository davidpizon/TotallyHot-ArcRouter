using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tools;
using TotallyHot.ArcRouter.Transcripts;

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

        Assert.Contains(collection: services,
            filter: d =>
                d.ServiceType == typeof(IRouterMemoryStore) &&
                d.ImplementationType == typeof(SqliteRouterMemoryStore) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d => d.ServiceType == typeof(RouterMemory) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d => d.ServiceType == typeof(AgentAsARouter) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d => d.ServiceType == typeof(CheckSyntax) && d.Lifetime == ServiceLifetime.Transient);
        Assert.Contains(collection: services,
            filter: d =>
                d.ServiceType == typeof(IEnvironmentVariableProvider) &&
                d.ImplementationType == typeof(EnvironmentVariableProvider) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d =>
                d.ServiceType == typeof(IModelRouteResolver) && d.ImplementationType == typeof(ModelRouteResolver) &&
                d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d => d.ServiceType == typeof(RequestInterceptor) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d => d.ServiceType == typeof(ProxyMiddleware) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d =>
                d.ServiceType == typeof(IRoutingPolicy) && d.ImplementationType == typeof(CompositeRoutingPolicy) &&
                d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d => d.ServiceType == typeof(UtilityRoutingPolicy) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services,
            filter: d => d.ServiceType == typeof(AgentRouterPolicy) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(collection: services, filter: d => d.ServiceType == typeof(IHostedService));
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

    /// <summary>
    /// Both capability stores must be registered, and must resolve to the <em>same</em> instance.
    /// </summary>
    /// <remarks>
    /// <see cref="ProxyMiddleware"/> takes them as optional constructor parameters, so a missing
    /// registration would not fail resolution - it would silently bind <see langword="null"/> and ship
    /// <c>/api/show</c>'s <c>model_info</c> permanently absent, with every test that passes the stores
    /// explicitly still green. This is the assertion that catches that.
    /// <para>
    /// Identity matters as much as presence: they are two read interfaces over one
    /// <see cref="ToolCallCapabilityStore"/>, so that a single <c>Reload</c> refreshes both. Two instances
    /// would mean two snapshots and a scan visible through one interface but not the other.
    /// </para>
    /// </remarks>
    [Fact]
    public void AddTotallyHotArcRouter_RegistersBothCapabilityStores_AsTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<RoutingOptions>(_ => { });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddTotallyHotArcRouter();

        using var provider = services.BuildServiceProvider();

        var capabilityStore = provider.GetRequiredService<IToolCallCapabilityStore>();
        var contextWindowStore = provider.GetRequiredService<IModelContextWindowStore>();

        Assert.NotNull(capabilityStore);
        Assert.NotNull(contextWindowStore);
        Assert.Same(expected: capabilityStore, actual: contextWindowStore);
        Assert.Same(expected: provider.GetRequiredService<ToolCallCapabilityStore>(), actual: contextWindowStore);
    }

    /// <summary>
    /// docs/router/judge-join-deadlock-fix-plan.md: the judge dispatcher must be reachable through
    /// <see cref="IAsyncGraderDispatcher"/> - the seam <see cref="QualityScoreAggregator"/> actually calls
    /// at hold-time - and must be absent from the write-time <see cref="IQualityScoreObserver"/> fan-out it
    /// used to occupy. A regression back to registering it as an observer would leave both assertions below
    /// green individually but silently reintroduce the deadlock, which is why they are asserted together.
    /// </summary>
    [Fact]
    public async Task AddTotallyHotArcRouter_ResolvesJudgeDispatcher_AbsentFromObserverFanOut()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<RoutingOptions>(_ => { });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddTotallyHotArcRouter();

        // await using, not using: resolving IAsyncGraderDispatcher/IQualityScoreObserver below pulls in
        // the same singleton graph as AddTotallyHotArcRouter_ResolvesRegisteredServices_WithSupportingDependencies
        // above, including OnnxEmbeddingClient, which implements only IAsyncDisposable - a synchronous
        // Dispose() on this scope would throw when it reached that singleton.
        await using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IAsyncGraderDispatcher>();
        Assert.IsType<JudgeShadowScoreDispatcher>(dispatcher);

        var observer = provider.GetRequiredService<IQualityScoreObserver>();
        var composite = Assert.IsType<CompositeRouterScoreObserver>(observer);

        // JudgeShadowScoreDispatcher no longer implements IQualityScoreObserver at all - the whole point
        // of the seam split - so this checks the concrete type of every fanned-out observer rather than an
        // "is JudgeShadowScoreDispatcher" pattern the compiler would reject as always false. Asserting the
        // full expected membership, not just an absence, is what keeps this test meaningful rather than
        // tautological.
        Assert.Equal(
            expected: [typeof(RouterMemoryScoreObserver), typeof(EmbeddingMemoryScoreObserver), typeof(TranscriptScoreObserver)],
            actual: composite.Observers.Select(o => o.GetType()));
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
                ["CostTracking:Reconciliation:Providers:openai:AdminApiKeyEnvVar"] = "TEST_OPENAI_ADMIN_KEY"
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
        Assert.Equal(expected: "openai", actual: reconciler.Provider);
    }

    private sealed class StubEnvironmentVariableProvider(IReadOnlyDictionary<string, string> values)
        : IEnvironmentVariableProvider
    {
        public string? GetVariable(string name)
        {
            return values.GetValueOrDefault(name);
        }
    }
}