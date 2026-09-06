using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Tests.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="PortfolioGraderAvailability"/>: a grader is held open only when its own flag is on
/// <em>and</em> a backbone is actually resolvable - the same two-part test <see cref="JudgeAvailability"/>
/// applies for the G-Eval judge.
/// </summary>
public class PortfolioGraderAvailabilityTests
{
    [Fact]
    public void DetermineGraderKeys_AllEnabledWithBackbone_ReturnsAllThree()
    {
        var availability = new PortfolioGraderAvailability(
            options: AllEnabled(),
            modelSelector: SelectorOver(FreeResolver()));

        var keys = availability.DetermineGraderKeys(new QualityResult { RequestCorrelationId = "corr-1" });

        Assert.Equal(new HashSet<string> { GraderKeys.CodeJudge, GraderKeys.IceScore, GraderKeys.Race }, actual: keys);
    }

    [Fact]
    public void DetermineGraderKeys_OnlyOneEnabled_ReturnsOnlyThatOne()
    {
        var options = new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
        { CodeJudgeEnabled = true, IceScoreEnabled = false, RaceEnabled = false });
        var availability = new PortfolioGraderAvailability(options: options, modelSelector: SelectorOver(FreeResolver()));

        var keys = availability.DetermineGraderKeys(new QualityResult { RequestCorrelationId = "corr-1" });

        Assert.Equal(new HashSet<string> { GraderKeys.CodeJudge }, actual: keys);
    }

    [Fact]
    public void DetermineGraderKeys_AllFlagsOff_ReturnsEmptyWithoutResolvingABackbone()
    {
        var options = new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
        { CodeJudgeEnabled = false, IceScoreEnabled = false, RaceEnabled = false });
        // A resolver that would throw if ever queried - proves the backbone check is skipped entirely
        // when every flag is off.
        var availability = new PortfolioGraderAvailability(options: options,
            modelSelector: SelectorOver(new ThrowingResolver()));

        var keys = availability.DetermineGraderKeys(new QualityResult { RequestCorrelationId = "corr-1" });

        Assert.Empty(keys);
    }

    [Fact]
    public void DetermineGraderKeys_EnabledButNoBackbone_ReturnsEmpty()
    {
        var paidOnly = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://api.openai.com", isFree: false);
        var availability = new PortfolioGraderAvailability(options: AllEnabled(), modelSelector: SelectorOver(paidOnly));

        var keys = availability.DetermineGraderKeys(new QualityResult { RequestCorrelationId = "corr-1" });

        Assert.Empty(keys);
    }

    private static StaticOptionsMonitor<PortfolioGraderOptions> AllEnabled()
    {
        return new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
        { CodeJudgeEnabled = true, IceScoreEnabled = true, RaceEnabled = true });
    }

    private static JudgeModelSelector SelectorOver(IModelRouteResolver resolver)
    {
        return new JudgeModelSelector(routeResolver: resolver,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<JudgeModelSelector>.Instance);
    }

    private static IModelRouteResolver FreeResolver()
    {
        return ModelRouteResolverTestFactory.Create(
            modelName: "local-judge", providerModelId: "qwen2.5-7b-instruct", baseUrl: "http://localhost:1234/v1",
            isFree: true);
    }

    /// <summary>A resolver that fails any call, proving a code path never reaches it.</summary>
    private sealed class ThrowingResolver : IModelRouteResolver
    {
        public IReadOnlyList<AvailableModel> ListModels()
        {
            throw new InvalidOperationException("should not be called");
        }

        public bool TryResolve(string? modelName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ResolvedModelRoute? route)
        {
            throw new InvalidOperationException("should not be called");
        }

        public bool IsProviderEnabled(string provider)
        {
            throw new InvalidOperationException("should not be called");
        }

        public bool IsModelEnabled(string modelName)
        {
            throw new InvalidOperationException("should not be called");
        }
    }
}
