using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Unit tests for <see cref="RegretReplayEngine"/>, <see cref="RegretReplayResult"/>, and
/// <see cref="AlwaysModelBaseline"/>.
/// </summary>
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
            new(TaskId: "t1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(0.8, 0.02, 1000),
                ["claude-sonnet-4-5"] = new(1.0, 0.01, 800)
            }),
            new(TaskId: "t2", Dimension: "bug_fixing", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(0.6, 0.02, 1200),
                ["claude-sonnet-4-5"] = new(0.4, 0.01, 900)
            })
        ];

        var result = RegretReplayEngine.Replay(tasks: tasks, router: new AlwaysModelBaseline("claude-opus-4-6"),
            weights: Weights);

        // r*_1 = 0.999, r_1(opus) = 0.798 -> regret 0.201
        // r*_2 = 0.598, r_2(opus) = 0.598 -> regret 0.0
        Assert.Equal(0.201, actual: result.CumulativeRegret, 6);
        Assert.Equal(0.7, actual: result.AvgPerf, 6); // (0.8 + 0.6) / 2
        Assert.Equal(2200, actual: result.TotalTokens);
        Assert.Equal(0.04, actual: result.TotalCostUsd, 6);
        Assert.Equal(expected: 0.7 * 100 / 0.04, actual: result.PerfPerDollar!.Value, 3);
        Assert.Equal(2, actual: result.ScoredTaskCount);
        Assert.Equal(0, actual: result.SkippedTaskCount);
        Assert.Equal(expected: "always_claude-opus-4-6", actual: result.RouterName);
    }

    [Fact]
    public void Replay_TaskMissingTheFixedModelsCell_IsSkippedNotZeroed()
    {
        RegretTaskOutcome[] tasks =
        [
            new(TaskId: "t1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(0.8, 0.02, 1000)
            }),
            new(TaskId: "t2", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                // Opus was never scored on this task - Always-Opus cannot route it.
                ["glm-5"] = new(0.5, 0.01, 500)
            })
        ];

        var result = RegretReplayEngine.Replay(tasks: tasks, router: new AlwaysModelBaseline("claude-opus-4-6"),
            weights: Weights);

        Assert.Equal(1, actual: result.ScoredTaskCount);
        Assert.Equal(1, actual: result.SkippedTaskCount);
        Assert.Equal(0.8, actual: result.AvgPerf, 6);
    }

    [Fact]
    public void Replay_NoLeakage_BaselineNeverSeesOutcomeCells()
    {
        var spy = new LeakDetectingBaseline();
        RegretTaskOutcome[] tasks =
        [
            new(TaskId: "t1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(0.9, 0.02, 100)
            })
        ];

        RegretReplayEngine.Replay(tasks: tasks, router: spy, weights: Weights);

        Assert.True(spy.ObservedOnlyContextSignals);
    }

    [Fact]
    public void PerfPerDollar_ZeroTotalCost_IsNullNotInfinite()
    {
        RegretTaskOutcome[] tasks =
        [
            new(TaskId: "t1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["free-model"] = new(1.0, 0.0, 100)
            })
        ];

        var result = RegretReplayEngine.Replay(tasks: tasks, router: new AlwaysModelBaseline("free-model"),
            weights: Weights);

        Assert.Null(result.PerfPerDollar);
    }

    [Fact]
    public void Replay_OnlineBaseline_ReceivesOnlyItsOwnSelectedCellsReward()
    {
        var spy = new UpdateSpyBaseline();
        RegretTaskOutcome[] tasks =
        [
            new(TaskId: "t1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(0.8, 0.02, 1000),
                ["claude-sonnet-4-5"] = new(1.0, 0.01, 800)
            })
        ];

        RegretReplayEngine.Replay(tasks: tasks, router: spy, weights: Weights);

        var (context, selectedModelId, reward) = Assert.Single(spy.Updates);
        Assert.Equal(expected: "t1", actual: context.TaskId);
        Assert.Equal(expected: "claude-opus-4-6", actual: selectedModelId);
        Assert.Equal(expected: Weights.Reward(new RegretOutcomeCell(0.8, 0.02, 1000)), actual: reward, 9);
    }

    [Fact]
    public void Record_SelectedModelNotInOutcome_Throws()
    {
        var result = new RegretReplayResult { RouterName = "test" };
        var outcome = new RegretTaskOutcome(TaskId: "t1", Dimension: "code_generation",
            Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(0.5, 0.01, 100)
            });

        Assert.Throws<ArgumentException>(() =>
            result.Record(outcome: outcome, selectedModelId: "not-a-candidate", weights: Weights));
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

    // Records every Update call so a test can assert online baselines are fed exactly the selected
    // model's own reward, never another candidate's.
    private sealed class UpdateSpyBaseline : IOnlineRegretBaselineRouter
    {
        public List<(RegretReplayContext Context, string ModelId, double Reward)> Updates { get; } = [];

        public string Name => "update_spy";

        public string? Route(RegretReplayContext context)
        {
            return context.CandidateModelIds
                .OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal)
                .First();
        }

        public void Update(RegretReplayContext context, string selectedModelId, double reward)
        {
            Updates.Add((context, selectedModelId, reward));
        }
    }
}