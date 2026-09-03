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
    private static OrchestratorRoutingPolicy BuildPolicy(RecordingVoter voter)
    {
        return new OrchestratorRoutingPolicy(voters: [voter],
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            { EnableExploration = false, ExplorationRate = 0d }),
            logger: NullLogger<OrchestratorRoutingPolicy>.Instance);
    }

    [Fact]
    public void Route_KnownTaskId_PassesItsPrecomputedEmbeddingAndTaskText()
    {
        var embedding = new[] { 1f, 0f };
        var voter = new RecordingVoter(name: "dim_best",
            vote: _ => new VoterVote(VoterName: "dim_best", ModelName: "model-a", 0.9));
        var baseline = new OrchestratorArmBaseline(
            policy: BuildPolicy(voter),
            embeddingsByTaskId: new Dictionary<string, float[]> { ["t1"] = embedding });

        var context = new RegretReplayContext(TaskId: "t1", Dimension: "bug_fixing",
            CandidateModelIds: ["model-a", "model-b"], TaskText: "fix the bug");
        var result = baseline.Route(context);

        Assert.Equal(expected: "model-a", actual: result);
        Assert.NotNull(voter.LastContext);
        Assert.Same(expected: embedding, actual: voter.LastContext!.TaskEmbedding);
        Assert.Equal(expected: "fix the bug", actual: voter.LastContext.TaskText);
        Assert.Equal(expected: "bug_fixing", actual: voter.LastContext.Dimension);
        Assert.Equal(expected: ["model-a", "model-b"], actual: voter.LastContext.Candidates.Select(c => c.ModelName));
    }

    [Fact]
    public void Route_TaskIdWithNoPrecomputedEmbedding_PassesNullEmbedding()
    {
        var voter = new RecordingVoter(name: "dim_best",
            vote: _ => new VoterVote(VoterName: "dim_best", ModelName: "model-a", 0.9));
        var baseline = new OrchestratorArmBaseline(policy: BuildPolicy(voter),
            embeddingsByTaskId: new Dictionary<string, float[]>());

        baseline.Route(new RegretReplayContext(TaskId: "id-test-task", Dimension: "bug_fixing",
            CandidateModelIds: ["model-a"]));

        Assert.Null(voter.LastContext!.TaskEmbedding);
    }

    [Fact]
    public void Route_EveryVoterAbstains_FallsBackToDefaultModel_ReturnsNullAsNotComputable()
    {
        var voter = new RecordingVoter(name: "dim_best", vote: _ => VoterVote.Abstain("dim_best"));
        var baseline = new OrchestratorArmBaseline(policy: BuildPolicy(voter),
            embeddingsByTaskId: new Dictionary<string, float[]>());

        var result = baseline.Route(new RegretReplayContext(TaskId: "t1", Dimension: "bug_fixing",
            CandidateModelIds: ["model-a", "model-b"]));

        Assert.Null(result);
    }

    [Fact]
    public void Name_IsOrchestrator()
    {
        Assert.Equal(
            expected: "orchestrator",
            actual: new OrchestratorArmBaseline(
                policy: BuildPolicy(new RecordingVoter(name: "dim_best", vote: _ => VoterVote.Abstain("dim_best"))),
                embeddingsByTaskId: new Dictionary<string, float[]>()).Name);
    }

    [Fact]
    public void Constructor_NullPolicy_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OrchestratorArmBaseline(policy: null!, embeddingsByTaskId: new Dictionary<string, float[]>()));
    }

    [Fact]
    public void Constructor_NullEmbeddings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OrchestratorArmBaseline(
            policy: BuildPolicy(new RecordingVoter(name: "dim_best", vote: _ => VoterVote.Abstain("dim_best"))),
            embeddingsByTaskId: null!));
    }

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