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
    public async Task DecideAsync_VoterPicksAModelNotAmongCandidates_TreatedAsAbstention()
    {
        // A buggy voter (or a future implementation) that picks a model outside the current candidate set
        // must not corrupt the decision - it degrades to an abstention like any other unusable vote.
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "not-a-candidate", confidence: 1.0),
            new FakeVoter(VoterNames.LogReg, "kimi-k2.5", confidence: 1.0),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.False(decision.CandidateScores.ContainsKey("not-a-candidate"));
    }

    [Fact]
    public async Task DecideAsync_VoterPicksDifferentlyCasedModelName_CandidateScoresKeyUsesCanonicalCasing()
    {
        // Votes are matched against candidates case-insensitively, so a voter is free to echo back a
        // different casing than the candidate list uses. CandidateScores keys must still come out in the
        // candidate's own casing so they line up with context.Candidates and SelectedModel when enumerated.
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "KIMI-K2.5", confidence: 1.0) };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Contains("kimi-k2.5", decision.CandidateScores.Keys);
        Assert.DoesNotContain("KIMI-K2.5", decision.CandidateScores.Keys);
        Assert.Contains($"voter:{VoterNames.DimBest}:kimi-k2.5", decision.CandidateScores.Keys);
    }

    [Fact]
    public async Task DecideAsync_VoterPicksDottedVersionSeparatorVariantOfCandidateModelName_MatchesTheCandidate()
    {
        // Matching tolerates a "." vs "-" version separator (cosmetic spelling only) - the candidate list
        // spells this model "claude-opus-4-6", and a voter returning the dotted "claude-opus-4.6" still
        // means the exact same, interchangeable model.
        var opus = new RoutingCandidate("claude-opus-4-6", "anthropic", IsFree: false);
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "claude-opus-4.6", confidence: 1.0) };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [opus]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("claude-opus-4-6", decision.SelectedModel);
        Assert.Contains("claude-opus-4-6", decision.CandidateScores.Keys);
    }

    [Fact]
    public async Task DecideAsync_VoterPicksDatedSnapshotOfCandidateModelName_TreatedAsADistinctModelAndAbstained()
    {
        // A dated snapshot pins a specific, non-interchangeable release: "claude-opus-4.6-20250929" is not
        // the same model as "claude-opus-4-6" and must not be treated as a match, unlike the purely
        // cosmetic casing/separator differences covered above.
        var opus = new RoutingCandidate("claude-opus-4-6", "anthropic", IsFree: false);
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "claude-opus-4.6-20250929", confidence: 1.0) };
        var policy = CreatePolicy(voters, defaultModel: "claude-opus-4-6");
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [opus]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.False(decision.CandidateScores.ContainsKey("claude-opus-4-6"));
        Assert.False(decision.CandidateScores.ContainsKey("claude-opus-4.6-20250929"));
    }

    [Fact]
    public async Task DecideAsync_VoterConfidenceOutsideZeroToOne_IsClampedBeforeWeighting()
    {
        // A voter's Confidence is not itself range-validated (unlike RoutingDecision.Confidence), so the
        // ensemble must clamp it before using it as a weight multiplier rather than trusting it verbatim.
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 5.0) };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Equal(0.9, decision.CandidateScores["kimi-k2.5"], precision: 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task DecideAsync_VoterConfidenceIsNonFinite_TreatedAsAbstentionRatherThanPoisoningTheScore(double confidence)
    {
        // Math.Clamp does not sanitize NaN/Infinity, so a non-finite confidence must be caught before it
        // can turn contribution (and everything summed from it) into NaN.
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "minimax-m2.7", confidence),
            new FakeVoter(VoterNames.LogReg, "kimi-k2.5", confidence: 1.0),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi, MiniMax]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.False(double.IsNaN(decision.Confidence));
        Assert.False(decision.CandidateScores.ContainsKey("minimax-m2.7"));
    }

    [Fact]
    public async Task DecideAsync_TiedWeightedScores_BreaksTieDeterministicallyByModelName()
    {
        // Two single-voter picks at equal weight/confidence tie exactly. OrderByDescending alone would
        // leave the winner dependent on Dictionary<TKey,TValue> enumeration order; the explicit
        // ThenBy(model name) tie-break must make this reproducible instead.
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "minimax-m2.7", confidence: 1.0),
            new FakeVoter(VoterNames.MemoryKnn, "glm-5", confidence: 1.0),
        };
        var policy = new OrchestratorRoutingPolicy(
            voters,
            Options.Create(new RoutingOptions
            {
                DefaultModel = "kimi-k2.5",
                DimBestVoterWeight = 0.5,
                MemoryKnnVoterWeight = 0.5,
            }),
            NullLogger<OrchestratorRoutingPolicy>.Instance);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [MiniMax, Glm]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("glm-5", decision.SelectedModel);
    }

    [Fact]
    public async Task DecideAsync_CandidateModelNameStartsWithVoterPrefix_StillEligibleToWin()
    {
        // The argmax must select only from context.Candidates rather than filtering candidateScores keys
        // by a "voter:" prefix - a real candidate model named "voter:custom" would otherwise be wrongly
        // excluded from its own win.
        var prefixedCandidate = new RoutingCandidate("voter:custom", "openai", IsFree: false);
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "voter:custom", confidence: 1.0) };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [prefixedCandidate]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("voter:custom", decision.SelectedModel);
    }

    [Fact]
    public async Task DecideAsync_VoterReturnsBlankModelName_TreatedAsAbstentionRatherThanFailingTheDecision()
    {
        // A non-null but blank ModelName still fails IsAbstain's null check, so it must be caught
        // separately - otherwise it reaches ModelNameCanonicalizer.Canonicalize, which throws on
        // whitespace and would hard-fail the whole decision.
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "   ", confidence: 1.0),
            new FakeVoter(VoterNames.LogReg, "kimi-k2.5", confidence: 1.0),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
    }

    [Fact]
    public async Task DecideAsync_AllParticipatingVotersHaveNonPositiveWeight_FallsBackToDefaultModel()
    {
        // A zero (or negative) weight still lets its voter through IsVoterEnabled, but contributing
        // nothing while still counting toward participatingVoters would let an all-zero-weight
        // configuration deterministically "win" a candidate via the tie-break with no effective ensemble
        // weight behind it. It must instead degrade the same way a fully-abstained vote does.
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0) };
        var policy = new OrchestratorRoutingPolicy(
            voters,
            Options.Create(new RoutingOptions { DefaultModel = "kimi-k2.5", DimBestVoterWeight = 0d }),
            NullLogger<OrchestratorRoutingPolicy>.Instance);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal(RouterConstants.FallbackReason, decision.Rationale);
        Assert.Equal(0, decision.Confidence);
    }

    [Fact]
    public async Task DecideAsync_CandidateModelNameContainsSlashNotMatchingItsOwnProvider_NotConflatedWithADifferentCandidate()
    {
        // Canonicalizing with provider=null strips ANY leading "segment/", so two distinct candidates -
        // one whose ModelName legitimately contains a slash unrelated to its own Provider, and one with no
        // slash at all - could collapse onto the same comparison key and let a vote for the bare name match
        // whichever candidate happened to be listed first. Canonicalizing with the matched candidate's own
        // Provider must keep them distinct.
        var llamaViaOpenRouter = new RoutingCandidate("meta-llama/llama-3.1", "openrouter", IsFree: false);
        var bareLlama = new RoutingCandidate("llama-3.1", "some-other-provider", IsFree: false);
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "llama-3.1", confidence: 1.0) };
        var policy = CreatePolicy(voters, defaultModel: "llama-3.1");
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [llamaViaOpenRouter, bareLlama]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("llama-3.1", decision.SelectedModel);
        Assert.False(decision.CandidateScores.ContainsKey("meta-llama/llama-3.1"));
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
