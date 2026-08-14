using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="LlmRouterVoter"/>'s documented always-abstain stub behavior - PLAN.md Phase L's
/// deferred voter (see the class's own remarks for the scope cut).
/// </summary>
public class LlmRouterVoterTests
{
    [Fact]
    public async Task VoteAsync_AlwaysAbstains()
    {
        var voter = new LlmRouterVoter();
        var context = new VotingContext(
            "live:code_generation",
            [new RoutingCandidate("model-a", "openai", IsFree: false)],
            TaskEmbedding: [1f, 0f, 0f],
            TaskText: "some task text");

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
        Assert.Equal("llm_router", voter.Name);
    }
}
