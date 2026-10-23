using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>Covers auto-route pin exclusion for the teacher model (issue #304 / ADR-0027).</summary>
public class RoutingCandidateBuilderTests
{
    [Fact]
    public void GetEligibleRoutes_ExcludesPinnedJudge()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("local-judge", "lmstudio", "qwen"),
            ("kimi-k2.5", "moonshot", "kimi"));
        var builder = new RoutingCandidateBuilder(
            circuitBreaker: new CircuitBreaker(),
            modelRouteResolver: resolver,
            routerMemory: null,
            interactionStatusStore: null,
            logger: NullLogger.Instance,
            judgeOptions: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { ModelName = "local-judge" }));

        var eligible = builder.GetEligibleRoutes([]);

        Assert.DoesNotContain(collection: eligible, filter: e => e.ModelName == "local-judge");
        Assert.Contains(collection: eligible, filter: e => e.ModelName == "kimi-k2.5");
    }

    [Fact]
    public void GetEligibleRoutes_NoPin_LeavesEveryConfiguredModel()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("local-judge", "lmstudio", "qwen"),
            ("kimi-k2.5", "moonshot", "kimi"));
        var builder = new RoutingCandidateBuilder(
            circuitBreaker: new CircuitBreaker(),
            modelRouteResolver: resolver,
            routerMemory: null,
            interactionStatusStore: null,
            logger: NullLogger.Instance);

        var eligible = builder.GetEligibleRoutes([]);

        Assert.Equal(2, actual: eligible.Count);
    }
}
