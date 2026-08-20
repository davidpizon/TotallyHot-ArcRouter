using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Router.TextGeneration;
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

    /// <summary>
    /// docs/router/live-feedback-learning-plan.md Phase 2a: the <see cref="RoutingSignals"/> overload
    /// must actually reach the voters via <see cref="VotingContext"/>, not just accept and drop the
    /// parameter.
    /// </summary>
    [Fact]
    public async Task SelectModelAsync_WithSignals_ForwardsTaskTextAndEmbeddingToVoters()
    {
        var recordingVoter = new RecordingVoter(VoterNames.DimBest, "kimi-k2.5");
        var policy = CreatePolicy([recordingVoter]);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);
        var embedding = new float[] { 1f, 2f, 3f };
        var signals = new RoutingSignals("refactor this function", embedding);

        var selected = await policy.SelectModelAsync(context, signals, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", selected);
        Assert.NotNull(recordingVoter.LastContext);
        Assert.Equal("refactor this function", recordingVoter.LastContext!.TaskText);
        Assert.Same(embedding, recordingVoter.LastContext.TaskEmbedding);
    }

    [Fact]
    public async Task SelectModelAsync_NoSignals_ForwardsNullTaskTextAndEmbedding()
    {
        var recordingVoter = new RecordingVoter(VoterNames.DimBest, "kimi-k2.5");
        var policy = CreatePolicy([recordingVoter]);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        await policy.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(recordingVoter.LastContext);
        Assert.Null(recordingVoter.LastContext!.TaskText);
        Assert.Null(recordingVoter.LastContext.TaskEmbedding);
    }

    [Fact]
    public async Task DecideAsync_LlmRouterAbstains_DegradesToThreeVoterVote()
    {
        // llm_router abstains here because no task text is supplied (taskText: null below) - this is
        // the exact "voter has nothing to go on" degrade path PLAN.md Phase L requires: the ensemble
        // still resolves cleanly with three voters. NeverCalledTextGenerationClient asserts the voter
        // never even reaches the generation client for a text-less context.
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.MemoryKnn, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.LogReg, "glm-5", confidence: 1.0),
            new LlmRouterVoter(new NeverCalledTextGenerationClient(), NullLogger<LlmRouterVoter>.Instance),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Glm, Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.False(decision.CandidateScores.ContainsKey("minimax-m2.7"));
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T3's exit bar: an ensemble integration
    /// test confirms all five voters appear in a single decision's breakdown, once <c>cluster_best</c>
    /// joins <c>dim_best</c>/<c>memory_kNN</c>/<c>logreg</c>/<c>llm_router</c>.
    /// </summary>
    [Fact]
    public async Task DecideAsync_AllFiveVotersCastAVote_EachAppearsInTheBreakdown()
    {
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.MemoryKnn, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.LogReg, "glm-5", confidence: 1.0),
            new FakeVoter(VoterNames.LlmRouter, "minimax-m2.7", confidence: 1.0),
            new FakeVoter(VoterNames.ClusterBest, "kimi-k2.5", confidence: 1.0),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [MiniMax, Glm, Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        foreach (var voterName in new[] { VoterNames.DimBest, VoterNames.MemoryKnn, VoterNames.LogReg, VoterNames.LlmRouter, VoterNames.ClusterBest })
        {
            Assert.True(
                decision.CandidateScores.ContainsKey($"voter:{voterName}:kimi-k2.5") ||
                decision.CandidateScores.ContainsKey($"voter:{voterName}:glm-5") ||
                decision.CandidateScores.ContainsKey($"voter:{voterName}:minimax-m2.7"),
                $"Expected a breakdown entry for voter '{voterName}'.");
        }
    }

    [Fact]
    public async Task DecideAsync_EveryVoterAbstains_FallsBackToDefaultModel()
    {
        var voters = new IRoutingVoter[] { new LlmRouterVoter(new NeverCalledTextGenerationClient(), NullLogger<LlmRouterVoter>.Instance) };
        var policy = CreatePolicy(voters, defaultModel: "kimi-k2.5");
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Equal(0, decision.Confidence);
        Assert.Equal(RouterConstants.FallbackReason, decision.Rationale);
        Assert.False(decision.IsExploratory);
    }

    /// <summary>
    /// docs/router/orchestrator-live-path-plan.md M1.2: with a single eligible candidate, an
    /// exploration roll of rate 1.0 is deterministic in outcome (there is only one candidate to land on)
    /// while still exercising the roll itself, so this asserts the mechanism fires and is flagged without
    /// depending on RNG seeding.
    /// </summary>
    [Fact]
    public async Task DecideAsync_ExplorationRollFires_SelectsRandomCandidateAndFlagsExploratory()
    {
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0) };
        var policy = CreatePolicy(voters, enableExploration: true, explorationRate: 1.0);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.True(decision.IsExploratory);
    }

    [Fact]
    public async Task DecideAsync_ExplorationDisabled_NeverFlagsExploratory()
    {
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0) };
        var policy = CreatePolicy(voters); // exploration disabled by default via CreatePolicy

        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.False(decision.IsExploratory);
    }

    /// <summary>
    /// docs/router/orchestrator-live-path-plan.md M1.2: exploration must never fire on the all-abstain
    /// fallback path - <see cref="RoutingDecision.CreateFallback"/> is already a degraded outcome and
    /// must not be compounded with a random pick, even with a rate of 1.0.
    /// </summary>
    [Fact]
    public async Task DecideAsync_EveryVoterAbstains_ExplorationRateOne_FallbackIsNeverExploratory()
    {
        var voters = new IRoutingVoter[] { new LlmRouterVoter(new NeverCalledTextGenerationClient(), NullLogger<LlmRouterVoter>.Instance) };
        var policy = CreatePolicy(voters, defaultModel: "kimi-k2.5", enableExploration: true, explorationRate: 1.0);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.False(decision.IsExploratory);
        Assert.Equal(RouterConstants.FallbackReason, decision.Rationale);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: with exploration enabled and K
    /// eligible candidates, the greedy-arm propensity is <c>(1 - eps) + eps / K</c>.
    /// </summary>
    [Fact]
    public async Task DecideAsync_ExplorationEnabled_GreedyPick_PropensityIsOneMinusEpsPlusEpsOverK()
    {
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0) };
        // explorationRate 0 keeps the roll deterministic (never exploratory) while EnableExploration
        // stays true, so the propensity formula's eps term is exercised without RNG flakiness.
        var policy = CreatePolicy(voters, enableExploration: true, explorationRate: 0.0);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [MiniMax, Glm, Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.False(decision.IsExploratory);
        Assert.Equal(1.0, decision.Propensity, precision: 6);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: an exploratory pick's propensity is
    /// <c>eps / K</c>.
    /// </summary>
    [Fact]
    public async Task DecideAsync_ExplorationRollFires_PropensityIsEpsOverK()
    {
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0) };
        var policy = CreatePolicy(voters, enableExploration: true, explorationRate: 1.0);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [MiniMax, Glm, Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.True(decision.IsExploratory);
        Assert.Equal(1.0 / 3.0, decision.Propensity, precision: 6);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: with exploration disabled entirely
    /// (not just a zero rate), eps folds to 0 and every decision reports certain selection.
    /// </summary>
    [Fact]
    public async Task DecideAsync_ExplorationDisabled_PropensityIsAlwaysOne()
    {
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0) };
        var policy = CreatePolicy(voters); // exploration disabled by default via CreatePolicy
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [MiniMax, Glm, Kimi]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.False(decision.IsExploratory);
        Assert.Equal(1.0, decision.Propensity, precision: 6);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: <see cref="OrchestratorRoutingPolicy.DecideOutcomeAsync"/>
    /// delegates directly to <see cref="OrchestratorRoutingPolicy.DecideAsync"/>, so it reports the same
    /// real provenance rather than the interface default's always-certain wrap.
    /// </summary>
    [Fact]
    public async Task DecideOutcomeAsync_DelegatesToDecideAsync_PreservingRealProvenance()
    {
        var voters = new IRoutingVoter[] { new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0) };
        var policy = CreatePolicy(voters, enableExploration: true, explorationRate: 1.0);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi]);
        var signals = new RoutingSignals("some task", [1f, 2f, 3f]);

        IRoutingPolicy asInterface = policy;
        var decision = await asInterface.DecideOutcomeAsync(context, signals, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.True(decision.IsExploratory);
        Assert.Equal(1.0, decision.Propensity, precision: 6);
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
                // Exploration defaults to enabled at 5% (RoutingOptions' own defaults) - disabled here so
                // this tie-break assertion can't flake on an exploration roll (docs/router/
                // orchestrator-live-path-plan.md M1.2).
                EnableExploration = false,
                ExplorationRate = 0,
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
    public async Task DecideAsync_PerVoterBreakdownKeyCollidesWithARealCandidatesAggregateKey_AggregateScoreWins()
    {
        // A per-voter breakdown key is "voter:{voterName}:{modelName}" - here that's exactly
        // "voter:dim_best:kimi-k2.5" from the first voter's pick. A second, real candidate is coincidentally
        // named the same thing. Its own aggregate score (from the second voter) must survive in
        // CandidateScores rather than being overwritten by the unrelated per-voter breakdown entry.
        var collidingCandidate = new RoutingCandidate("voter:dim_best:kimi-k2.5", "openai", IsFree: false);
        var voters = new IRoutingVoter[]
        {
            new FakeVoter(VoterNames.DimBest, "kimi-k2.5", confidence: 1.0),
            new FakeVoter(VoterNames.MemoryKnn, "voter:dim_best:kimi-k2.5", confidence: 1.0),
        };
        var policy = CreatePolicy(voters);
        var context = new RoutingContext("live:bug_fixing", IsUtility: false, [Kimi, collidingCandidate]);

        var decision = await policy.DecideAsync(context, taskEmbedding: null, taskText: null, TestContext.Current.CancellationToken);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Equal(0.9, decision.CandidateScores["kimi-k2.5"], precision: 6);
        Assert.Equal(0.57, decision.CandidateScores["voter:dim_best:kimi-k2.5"], precision: 6);
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
        bool enableLogReg = true,
        bool enableExploration = false,
        double explorationRate = 0d) =>
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
                // Every other test in this file relies on deterministic argmax behavior, so exploration
                // is off by default here - only the exploration-focused tests below opt in explicitly.
                EnableExploration = enableExploration,
                ExplorationRate = explorationRate,
            }),
            NullLogger<OrchestratorRoutingPolicy>.Instance);

    private sealed class FakeVoter(string name, string modelName, double confidence) : IRoutingVoter
    {
        public string Name { get; } = name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VoterVote(Name, modelName, confidence));
    }

    private sealed class RecordingVoter(string name, string modelName) : IRoutingVoter
    {
        public string Name { get; } = name;

        public VotingContext? LastContext { get; private set; }

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(new VoterVote(Name, modelName, Confidence: 1.0));
        }
    }

    private sealed class ThrowingVoter(string name) : IRoutingVoter
    {
        public string Name { get; } = name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated voter failure.");
    }

    /// <summary>
    /// A <see cref="ITextGenerationClient"/> that fails the test if ever invoked - used to construct a
    /// real <see cref="LlmRouterVoter"/> in tests that expect it to abstain before reaching generation
    /// (e.g. no task text supplied), asserting that early-abstain path actually short-circuits.
    /// </summary>
    private sealed class NeverCalledTextGenerationClient : ITextGenerationClient
    {
        public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("LlmRouterVoter should have abstained before calling the generation client.");
    }
}
