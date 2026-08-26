using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="OrchestratorArmBaseline.Route"/>'s wiring into the real
/// <see cref="OrchestratorRoutingPolicy"/>: building <see cref="RoutingCandidate"/>s from
/// <see cref="RegretReplayContext.CandidateModelIds"/>, looking up a query task's precomputed embedding by
/// id (or passing <see langword="null"/> when the task has none), forwarding
/// <see cref="RegretReplayContext.TaskText"/>, and treating an all-abstain fallback decision as
/// "not computable" the same way every other baseline reports it.
/// </summary>
public class OrchestratorArmBaselineTests
{
    private static OrchestratorRoutingPolicy BuildPolicy(RecordingVoter voter) =>
        new([voter], new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { EnableExploration = false, ExplorationRate = 0d }),
            NullLogger<OrchestratorRoutingPolicy>.Instance);

    [Fact]
    public void Route_KnownTaskId_PassesItsPrecomputedEmbeddingAndTaskText()
    {
        var embedding = new float[] { 1f, 0f };
        var voter = new RecordingVoter("dim_best", _ => new VoterVote("dim_best", "model-a", 0.9));
        var baseline = new OrchestratorArmBaseline(
            BuildPolicy(voter),
            new Dictionary<string, float[]> { ["t1"] = embedding });

        var context = new RegretReplayContext("t1", "bug_fixing", ["model-a", "model-b"], "fix the bug");
        var result = baseline.Route(context);

        Assert.Equal("model-a", result);
        Assert.NotNull(voter.LastContext);
        Assert.Same(embedding, voter.LastContext!.TaskEmbedding);
        Assert.Equal("fix the bug", voter.LastContext.TaskText);
        Assert.Equal("bug_fixing", voter.LastContext.Dimension);
        Assert.Equal(["model-a", "model-b"], voter.LastContext.Candidates.Select(c => c.ModelName));
    }

    [Fact]
    public void Route_TaskIdWithNoPrecomputedEmbedding_PassesNullEmbedding()
    {
        var voter = new RecordingVoter("dim_best", _ => new VoterVote("dim_best", "model-a", 0.9));
        var baseline = new OrchestratorArmBaseline(BuildPolicy(voter), new Dictionary<string, float[]>());

        baseline.Route(new RegretReplayContext("id-test-task", "bug_fixing", ["model-a"]));

        Assert.Null(voter.LastContext!.TaskEmbedding);
    }

    [Fact]
    public void Route_EveryVoterAbstains_FallsBackToDefaultModel_ReturnsNullAsNotComputable()
    {
        var voter = new RecordingVoter("dim_best", _ => VoterVote.Abstain("dim_best"));
        var baseline = new OrchestratorArmBaseline(BuildPolicy(voter), new Dictionary<string, float[]>());

        var result = baseline.Route(new RegretReplayContext("t1", "bug_fixing", ["model-a", "model-b"]));

        Assert.Null(result);
    }

    [Fact]
    public void Name_IsOrchestrator() =>
        Assert.Equal(
            "orchestrator",
            new OrchestratorArmBaseline(
                BuildPolicy(new RecordingVoter("dim_best", _ => VoterVote.Abstain("dim_best"))),
                new Dictionary<string, float[]>()).Name);

    [Fact]
    public void Constructor_NullPolicy_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new OrchestratorArmBaseline(null!, new Dictionary<string, float[]>()));

    [Fact]
    public void Constructor_NullEmbeddings_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new OrchestratorArmBaseline(
            BuildPolicy(new RecordingVoter("dim_best", _ => VoterVote.Abstain("dim_best"))), null!));

    private sealed class RecordingVoter(string name, Func<VotingContext, VoterVote> vote) : IRoutingVoter
    {
        public VotingContext? LastContext { get; private set; }

        public string Name => name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(vote(context));
        }
    }
}
