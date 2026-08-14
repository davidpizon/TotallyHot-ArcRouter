using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="OrchestratorRoutingPolicy"/>'s weighted vote, argmax, and voter-abstention degrade
/// path - PLAN.md Phase L's exit criterion is <see cref="DecideAsync_ResearchDocWorkedExample_ResolvesToKimiK25AtWeightedScore1_47"/>.
/// Every voter is a deterministic <see cref="FakeVoter"/> so this stays fast and requires no real
/// embedding/benchmark/model state (AGENTS.md's 5-second heavy-test bound).
/// </summary>
public class OrchestratorRoutingPolicyTests
{
    private static readonly RoutingCandidate MiniMax = new("minimax-m2.7", "openai", IsFree: false);
    private static readonly RoutingCandidate Glm = new("glm-5", "openai", IsFree: false);
    private static readonly RoutingCandidate Kimi = new("kimi-k2.5", "openai", IsFree: false);

    /// <summary>
    /// PLAN.md Phase L's exit criterion: research-doc §3.3's worked example - <c>api_llm</c> (this
    /// codebase's <c>llm_router</c>) votes MiniMax-M2.7, <c>logreg</c> votes GLM-5, <c>memory_kNN</c> and
    /// <c>dim_best</c> both vote Kimi-K2.5 - resolves to Kimi-K2.5 at weighted score 1.47, with
    /// MiniMax-M2.7 at 0.64 and GLM-5 at 0.43 (research-doc §3.3). The default option weights
    /// (<see cref="RoutingOptions.DimBestVoterWeight"/> = 0.9, <see cref="RoutingOptions.MemoryKnnVoterWeight"/> = 0.57,
    /// <see cref="RoutingOptions.LogRegVoterWeight"/> = 0.43, <see cref="RoutingOptions.LlmRouterVoterWeight"/> = 0.64)
    /// are exactly what reproduces these numbers when every fake voter reports full (1.0) confidence.
    /// </summary>
    [Fact]
    public async Task DecideAsync_ResearchDocWorkedExample_ResolvesToKimiK25AtWeightedScore1_47()
    {
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.MemoryKnn, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.LogReg, "glm-5", confidence: 1.0),
            new FakeVoter(VoterNames.LlmRouter, "minimax-m2.7", confidence: 1.0),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [MiniMax, Glm, Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Equal(1.47, decision.CandidateScores["kimi-k2.5"], precision: 6);
        Assert.Equal(0.64, decision.CandidateScores["minimax-m2.7"], precision: 6);
        Assert.Equal(0.43, decision.CandidateScores["glm-5"], precision: 6);
    }

    [Fact]
    public async Task SelectModelAsync_ReturnsTheDecisionsSelectedModel()
    {
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0) };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var selected = await policy.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", selected);
    }

    [Fact]
    public async Task DecideAsync_LlmRouterAbstains_DegradesToThreeVoterVote()
    {
        // llm_router always abstains in this phase (LlmRouterVoter) - this is the exact "no model
        // artifact yet" degrade path PLAN.md Phase L requires: the ensemble still resolves cleanly.
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.MemoryKnn, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.LogReg, "glm-5", confidence: 1.0),
            new LlmRouterVoter(),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Glm, Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.False(decision.CandidateScores.ContainsKey("minimax-m2.7"));
    }

    [Fact]
    public async Task DecideAsync_EveryVoterAbstains_FallsBackToDefaultModel()
    {
        var voters = new IRoutingVoter[] { new LlmRouterVoter() };
        var policy = CreatePolicy(voters, defaultModel: "kimi-k2.5");
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Equal(0, decision.Confidence);
        Assert.Equal(RouterConstants.FallbackReason, decision.Rationale);
    }

    [Fact]
    public async Task DecideAsync_DisabledVoter_DoesNotParticipate()
    {
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "minimax-m2.7", confidence: 1.0),
            new FakeVoter(VoterNames.LogReg, "kimi-k2.5", confidence: 1.0),
        };
        var policy = CreatePolicy(voters, enableLogReg: false);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [MiniMax, Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("minimax-m2.7", decision.SelectedModel);
        Assert.False(decision.CandidateScores.ContainsKey("kimi-k2.5"));
    }

    [Fact]
    public async Task DecideAsync_VoterThrows_TreatedAsAbstentionRatherThanFailingTheDecision()
    {
        var voters = new IRoutingVoter[]
        {
            new ThrowingVoter(VoterNames.DimBest),
            new FakeVoter(VoterNames.LogReg, "kimi-k2.5", confidence: 1.0),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
    }

    [Fact]
    public async Task DecideAsync_NoCandidates_Throws()
    {
        var policy = CreatePolicy([]);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, []);

        await Assert.ThrowsAsync<ArgumentException>(
            () => policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken));
    }

    private static OrchestratorRoutingPolicy CreatePolicy(
        IEnumerable<IRoutingVoter> voters,
        string defaultModel = "kimi-k2.5",
        bool enableLogReg = true) =>
        new(
            voters,
            Options.Create(new RoutingOptions
            {
                DefaultModel = defaultModel,
                DimBestVoterWeight = 0.9,
                MemoryKnnVoterWeight = 0.57,
                LogRegVoterWeight = 0.43,
                LlmRouterVoterWeight = 0.64,
                EnableLogRegVoter = enableLogReg,
            }),
            NullLogger<OrchestratorRoutingPolicy>.Instance);

    private sealed class FakeVoter(string name, string modelName, double confidence) : IRoutingVoter
    {
        public string Name { get; } = name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VoterVote(Name, modelName, confidence));
    }

    private sealed class ThrowingVoter(string name) : IRoutingVoter
    {
        public string Name { get; } = name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated voter failure.");
    }
}
