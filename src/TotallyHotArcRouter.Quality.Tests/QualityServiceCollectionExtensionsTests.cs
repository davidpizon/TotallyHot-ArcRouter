using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TotallyHot.ArcRouter.Quality.Analysis;
using TotallyHot.ArcRouter.Quality.DependencyInjection;
using TotallyHot.ArcRouter.Quality.Extraction;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Quality.Ingress;
using TotallyHot.ArcRouter.Quality.Parsing;
using TotallyHot.ArcRouter.Quality.Scoring;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers <see cref="QualityServiceCollectionExtensions.AddQuality"/>'s registration surface. Unlike the
/// executing verifier this replaced, there are no platform-gated branches to assert around: the graph is
/// identical on every host, which <see cref="AddQuality_GraphIsPlatformIndependent"/> pins down.
/// </summary>
public class QualityServiceCollectionExtensionsTests
{
    /// <summary>Builds a provider with the minimum ambient services <c>AddQuality</c> needs.</summary>
    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        configure?.Invoke(services);
        services.AddQuality();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddQuality_RejectsNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => QualityServiceCollectionExtensions.AddQuality(null!));
    }

    [Fact]
    public void AddQuality_RegistersBasePipeline()
    {
        using var provider = BuildProvider();

        Assert.IsType<StructuralParser>(provider.GetRequiredService<IStructuralParser>());
        Assert.IsType<QualityScorer>(provider.GetRequiredService<IQualityScorer>());
        Assert.IsType<KeywordDimensionInferrer>(provider.GetRequiredService<IDimensionInferrer>());
        Assert.IsType<CodeBlockSignalExtractor>(provider.GetRequiredService<ISignalExtractor>());
        Assert.IsType<QualityGrader>(provider.GetRequiredService<IQualityGrader>());
        Assert.IsType<QualityWorkQueue>(provider.GetRequiredService<IQualityQueue>());
        Assert.IsType<QualityIngress>(provider.GetRequiredService<IQualityIngress>());
        Assert.IsType<QualityScoreAggregator>(provider.GetRequiredService<IQualityScoreAggregator>());
        Assert.IsType<NullQualityScoreObserver>(provider.GetRequiredService<IQualityScoreObserver>());
        Assert.IsType<NoJudgeAvailability>(provider.GetRequiredService<IJudgeAvailability>());

        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(collection: hosted, filter: s => s is QualityGradingService);
        Assert.Contains(collection: hosted, filter: s => s is QualityJoinSweepService);
    }

    [Fact]
    public void AddQuality_RegistersEveryStaticAnalyzer()
    {
        using var provider = BuildProvider();

        var analyzers = provider.GetServices<IStaticAnalyzer>().ToList();

        Assert.Contains(collection: analyzers, filter: a => a is DiagnosticSeverityAnalyzer);
        Assert.Contains(collection: analyzers, filter: a => a is PlaceholderAnalyzer);
        Assert.Contains(collection: analyzers, filter: a => a is TruncationAnalyzer);
        Assert.Contains(collection: analyzers, filter: a => a is ComplexityAnalyzer);

        // The composite must not be enumerable as one of the analyzers it composes, or it would recurse.
        Assert.DoesNotContain(collection: analyzers, filter: a => a is CompositeStaticAnalyzer);
        Assert.NotNull(provider.GetRequiredService<CompositeStaticAnalyzer>());
    }

    [Fact]
    public void AddQuality_HonorsPreRegisteredObserver()
    {
        var custom = new NullQualityScoreObserver();
        using var provider = BuildProvider(s => s.AddSingleton<IQualityScoreObserver>(custom));

        Assert.Same(expected: custom, actual: provider.GetRequiredService<IQualityScoreObserver>());
    }

    [Fact]
    public void AddQuality_HonorsPreRegisteredJudgeAvailability()
    {
        var custom = new AlwaysJudge();
        using var provider = BuildProvider(s => s.AddSingleton<IJudgeAvailability>(custom));

        Assert.Same(expected: custom, actual: provider.GetRequiredService<IJudgeAvailability>());
    }

    // The predecessor of this registration decided at startup whether the host could execute code, and
    // registered a Linux jail runtime and a Firecracker runtime only when it could. Nothing registered now
    // can run anything, so there is no probe, no platform branch, and nothing that resolves differently on
    // one OS than another - this test exists to keep that property from quietly regressing.
    [Fact]
    public void AddQuality_GraphIsPlatformIndependent()
    {
        using var provider = BuildProvider();

        var registeredTypes = provider.GetServices<IHostedService>().Select(s => s.GetType().Name).Order().ToList();

        Assert.Equal(expected: [nameof(QualityGradingService), nameof(QualityJoinSweepService)],
            actual: registeredTypes);
        Assert.Null(provider.GetService<IStaticAnalyzer>() as CompositeStaticAnalyzer);
    }

    /// <summary>A stand-in availability that always asks for a judge, used to prove the host's registration wins.</summary>
    private sealed class AlwaysJudge : IJudgeAvailability
    {
        public bool WillJudge(QualityResult result)
        {
            return true;
        }
    }
}