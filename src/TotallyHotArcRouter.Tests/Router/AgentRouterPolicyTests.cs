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
        var context = new RoutingContext(Dimension, IsUtility: false, Candidates: []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.SelectModelAsync(context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SelectModelAsync_RouterSelectionIneligible_FallsBackToFirstCandidate()
    {
        var memory = new RouterMemory();
        var policy = Build(memory, defaultModel: "not-a-candidate");
        var context = new RoutingContext(
            Dimension,
            IsUtility: false,
            Candidates: [new RoutingCandidate("candidate-a", "openai", IsFree: false)]);

        var selected = await policy.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("candidate-a", selected);
    }

    [Fact]
    public async Task SelectModelAsync_RouterSelectionEligible_ReturnsIt()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(Dimension, "candidate-a", 0.9);
        var policy = Build(memory, defaultModel: "candidate-b");
        var context = new RoutingContext(
            Dimension,
            IsUtility: false,
            Candidates:
            [
                new RoutingCandidate("candidate-a", "openai", IsFree: false),
                new RoutingCandidate("candidate-b", "openai", IsFree: false),
            ]);

        var selected = await policy.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("candidate-a", selected);
    }

    private static AgentRouterPolicy Build(RouterMemory? memory = null, string defaultModel = "default-model")
    {
        var options = Options.Create(new RoutingOptions { DefaultModel = defaultModel, EnableExploration = false });
        var router = new AgentAsARouter(NullLogger<AgentAsARouter>.Instance, options, memory ?? new RouterMemory());
        return new AgentRouterPolicy(router);
    }
}
