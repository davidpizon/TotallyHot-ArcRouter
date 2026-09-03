using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="AgentRouterPolicy"/>'s delegation to <see cref="AgentAsARouter"/> and its
/// <see cref="RoutingContext.Candidates"/> contract enforcement.
/// </summary>
public class AgentRouterPolicyTests
{
    private const string Dimension = "live:general";

    [Fact]
    public async Task SelectModelAsync_EmptyCandidates_Throws()
    {
        var policy = Build();
        var context = new RoutingContext(Dimension: Dimension, false, Candidates: []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.SelectModelAsync(context: context, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SelectModelAsync_RouterSelectionIneligible_FallsBackToFirstCandidate()
    {
        var memory = new RouterMemory();
        var policy = Build(memory: memory, defaultModel: "not-a-candidate");
        var context = new RoutingContext(
            Dimension: Dimension,
            false,
            Candidates: [new RoutingCandidate(ModelName: "candidate-a", Provider: "openai", false)]);

        var selected =
            await policy.SelectModelAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "candidate-a", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_RouterSelectionEligible_ReturnsIt()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Dimension, model: "candidate-a", 0.9);
        var policy = Build(memory: memory, defaultModel: "candidate-b");
        var context = new RoutingContext(
            Dimension: Dimension,
            false,
            Candidates:
            [
                new RoutingCandidate(ModelName: "candidate-a", Provider: "openai", false),
                new RoutingCandidate(ModelName: "candidate-b", Provider: "openai", false)
            ]);

        var selected =
            await policy.SelectModelAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "candidate-a", actual: selected);
    }

    private static AgentRouterPolicy Build(RouterMemory? memory = null, string defaultModel = "default-model")
    {
        var options = Options.Create(new RoutingOptions { DefaultModel = defaultModel, EnableExploration = false });
        var router = new AgentAsARouter(logger: NullLogger<AgentAsARouter>.Instance, options: options,
            memory: memory ?? new RouterMemory());
        return new AgentRouterPolicy(router);
    }
}