using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TotallyHot.ArcRouter.Quality.Analysis;
using TotallyHot.ArcRouter.Quality.Extraction;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Quality.Ingress;
using TotallyHot.ArcRouter.Quality.Parsing;
using TotallyHot.ArcRouter.Quality.Scoring;

namespace TotallyHot.ArcRouter.Quality.DependencyInjection;

/// <summary>
/// Registration helpers for the quality verifier. The host application calls <see cref="AddQuality"/>
/// after registering its own <see cref="IQualityScoreObserver"/> adapter, and optionally its own
/// <see cref="IJudgeAvailability"/> to enable the judge axis.
/// </summary>
public static class QualityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the quality grader, its parser and static analyzers, the bounded work queue, the ingress
    /// façade, the judge-join aggregator, and the two background services. Binds
    /// <see cref="QualityOptions"/> from the <c>Quality</c> configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// There is deliberately no host-capability probing here and no conditional, platform-gated
    /// registration. The predecessor of this method registered a Linux jail runtime behind an
    /// <c>OperatingSystem.IsLinux()</c> check and a Firecracker microVM runtime behind a <c>/dev/kvm</c>
    /// probe, because what it registered could execute model-generated code and therefore needed a kernel
    /// to isolate it. Nothing registered below can run anything, so the graph is identical on every host
    /// and there is no degraded mode to detect or report.
    /// </remarks>
    public static IServiceCollection AddQuality(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<QualityOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(QualityOptions.SectionName).Bind(options));

        services.TryAddSingleton<IStructuralParser, StructuralParser>();
        services.TryAddSingleton<IQualityScorer, QualityScorer>();
        services.TryAddSingleton<IDimensionInferrer, KeywordDimensionInferrer>();
        services.TryAddSingleton<ISignalExtractor, CodeBlockSignalExtractor>();
        services.TryAddSingleton<IQualityGrader, QualityGrader>();
        services.TryAddSingleton<IQualityQueue, QualityWorkQueue>();
        services.TryAddSingleton<IQualityIngress, QualityIngress>();

        // Order is the composition order of the analysis axis; each analyzer abstains on input it has no
        // opinion about, so registering all six on every host costs nothing for a language one can't read.
        // Phase Q2 adds RelevanceAnalyzer (needs the prompt; abstains without one) and SmellDensityAnalyzer
        // (docs/research/code-quality-metrics-assessment.md §5.1) to the original four.
        services.TryAddEnumerable(
        [
            ServiceDescriptor.Singleton<IStaticAnalyzer, DiagnosticSeverityAnalyzer>(),
            ServiceDescriptor.Singleton<IStaticAnalyzer, PlaceholderAnalyzer>(),
            ServiceDescriptor.Singleton<IStaticAnalyzer, TruncationAnalyzer>(),
            ServiceDescriptor.Singleton<IStaticAnalyzer, ComplexityAnalyzer>(),
            ServiceDescriptor.Singleton<IStaticAnalyzer, RelevanceAnalyzer>(),
            ServiceDescriptor.Singleton<IStaticAnalyzer, SmellDensityAnalyzer>()
        ]);
        services.TryAddSingleton<CompositeStaticAnalyzer>();

        services.TryAddSingleton<IQualityScoreAggregator, QualityScoreAggregator>();

        // Safe defaults when the host has not supplied its own: no judge is expected (so every score is
        // written from static analysis alone), no Q3 portfolio grader is expected either, no asynchronous
        // grader is ever dispatched, and scores go nowhere.
        services.TryAddSingleton<IJudgeAvailability, NoJudgeAvailability>();
        services.TryAddSingleton<IPortfolioGraderAvailability, NoPortfolioGraderAvailability>();
        services.TryAddSingleton<IAsyncGraderDispatcher, NoAsyncGraderDispatcher>();
        services.TryAddSingleton<IQualityScoreObserver, NullQualityScoreObserver>();

        services.AddHostedService<QualityGradingService>();
        services.AddHostedService<QualityJoinSweepService>();

        return services;
    }
}