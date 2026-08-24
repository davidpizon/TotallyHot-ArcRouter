using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>Unit tests for <see cref="RegretReplayEngine"/>, <see cref="RegretReplayResult"/>, and <see cref="AlwaysModelBaseline"/>.</summary>
public class RegretReplayEngineTests
{
    // Canonical weights: r = 1*s + (-0.1)*cost.
    private static readonly RewardWeights Weights = RewardWeights.Canonical;

    [Fact]
    public void Replay_AlwaysOpus_MatchesHandComputedMetrics()
    {
        // Task 1: opus scores 0.8 @ $0.02 (r=0.798), sonnet scores 1.0 @ $0.01 (r=0.999) -> oracle is sonnet.
        // Task 2: opus scores 0.6 @ $0.02 (r=0.598), sonnet scores 0.4 @ $0.01 (r=0.399) -> oracle is opus.
        RegretTaskOutcome[] tasks =
        [
            new("t1", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(Score: 0.8, CostUsd: 0.02, TotalTokens: 1000),
                ["claude-sonnet-4-5"] = new(Score: 1.0, CostUsd: 0.01, TotalTokens: 800),
            }),
            new("t2", "bug_fixing", new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(Score: 0.6, CostUsd: 0.02, TotalTokens: 1200),
                ["claude-sonnet-4-5"] = new(Score: 0.4, CostUsd: 0.01, TotalTokens: 900),
            }),
        ];

        var result = RegretReplayEngine.Replay(tasks, new AlwaysModelBaseline("claude-opus-4-6"), Weights);

        // r*_1 = 0.999, r_1(opus) = 0.798 -> regret 0.201
        // r*_2 = 0.598, r_2(opus) = 0.598 -> regret 0.0
        Assert.Equal(0.201, result.CumulativeRegret, precision: 6);
        Assert.Equal(0.7, result.AvgPerf, precision: 6); // (0.8 + 0.6) / 2
        Assert.Equal(2200, result.TotalTokens);
        Assert.Equal(0.04, result.TotalCostUsd, precision: 6);
        Assert.Equal(0.7 * 100 / 0.04, result.PerfPerDollar!.Value, precision: 3);
        Assert.Equal(2, result.ScoredTaskCount);
        Assert.Equal(0, result.SkippedTaskCount);
        Assert.Equal("always_claude-opus-4-6", result.RouterName);
    }

    [Fact]
    public void Replay_TaskMissingTheFixedModelsCell_IsSkippedNotZeroed()
    {
        RegretTaskOutcome[] tasks =
        [
            new("t1", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(Score: 0.8, CostUsd: 0.02, TotalTokens: 1000),
            }),
            new("t2", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                // Opus was never scored on this task - Always-Opus cannot route it.
                ["glm-5"] = new(Score: 0.5, CostUsd: 0.01, TotalTokens: 500),
            }),
        ];

        var result = RegretReplayEngine.Replay(tasks, new AlwaysModelBaseline("claude-opus-4-6"), Weights);

        Assert.Equal(1, result.ScoredTaskCount);
        Assert.Equal(1, result.SkippedTaskCount);
        Assert.Equal(0.8, result.AvgPerf, precision: 6);
    }

    [Fact]
    public void Replay_NoLeakage_BaselineNeverSeesOutcomeCells()
    {
        var spy = new LeakDetectingBaseline();
        RegretTaskOutcome[] tasks =
        [
            new("t1", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(Score: 0.9, CostUsd: 0.02, TotalTokens: 100),
            }),
        ];

        RegretReplayEngine.Replay(tasks, spy, Weights);

        Assert.True(spy.ObservedOnlyContextSignals);
    }

    [Fact]
    public void PerfPerDollar_ZeroTotalCost_IsNullNotInfinite()
    {
        RegretTaskOutcome[] tasks =
        [
            new("t1", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["free-model"] = new(Score: 1.0, CostUsd: 0.0, TotalTokens: 100),
            }),
        ];

        var result = RegretReplayEngine.Replay(tasks, new AlwaysModelBaseline("free-model"), Weights);

        Assert.Null(result.PerfPerDollar);
    }

    [Fact]
    public void Record_SelectedModelNotInOutcome_Throws()
    {
        var result = new RegretReplayResult { RouterName = "test" };
        var outcome = new RegretTaskOutcome("t1", "code_generation", new Dictionary<string, RegretOutcomeCell>
        {
            ["claude-opus-4-6"] = new(Score: 0.5, CostUsd: 0.01, TotalTokens: 100),
        });

        Assert.Throws<ArgumentException>(() => result.Record(outcome, "not-a-candidate", Weights));
    }

    // Confirms IRegretBaselineRouter.Route only ever receives dimension + candidate ids, never the
    // cells RegretReplayEngine holds internally - the harness's core "no leakage" property.
    private sealed class LeakDetectingBaseline : IRegretBaselineRouter
    {
        public bool ObservedOnlyContextSignals { get; private set; }

        public string Name => "leak_detector";

        public string? Route(RegretReplayContext context)
        {
            ObservedOnlyContextSignals = context.TaskId == "t1"
                && context.Dimension == "code_generation"
                && context.CandidateModelIds is ["claude-opus-4-6"];
            return context.CandidateModelIds[0];
        }
    }
}
