using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Tests.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeAvailability"/>'s use of the graded result: a result with no correlation id is
/// not held, and that answer does not resolve a backbone.
/// </summary>
public class JudgeAvailabilityTests
{
    [Fact]
    public void WillJudge_NoCorrelationId_ReturnsFalseWithoutResolvingABackbone()
    {
        var availability = new JudgeAvailability(
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true }),
            modelSelector: new JudgeModelSelector(
                routeResolver: new ThrowingResolver(),
                options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
                logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<JudgeModelSelector>.Instance));

        Assert.False(availability.WillJudge(new QualityResult()));
    }

    [Fact]
    public void WillJudge_EnabledWithBackboneAndCorrelationId_ReturnsTrue()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "local-judge", providerModelId: "qwen2.5-7b-instruct", baseUrl: "http://localhost:1234/v1",
            isFree: true);
        var availability = new JudgeAvailability(
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true }),
            modelSelector: new JudgeModelSelector(
                routeResolver: resolver,
                options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
                logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<JudgeModelSelector>.Instance));

        Assert.True(availability.WillJudge(new QualityResult { RequestCorrelationId = "corr-1" }));
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
