using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>Unit tests for <see cref="DimensionBestBaseline"/>.</summary>
public class DimensionBestBaselineTests
{
    private static readonly RewardWeights Weights = RewardWeights.Canonical;

    [Fact]
    public void Replay_FrozenPriorDisagreesWithTruePerTaskWinner_RegretReflectsIt()
    {
        // Frozen probing-set prior: opus averages higher than sonnet on code_generation overall.
        var matrix = DimensionModelScoreMatrix.FromRows(
        [
            new("p1", "code_generation", "claude-opus-4-6", 0.9),
            new("p2", "code_generation", "claude-opus-4-6", 0.9),
            new("p3", "code_generation", "claude-sonnet-4-5", 0.5),
        ]);
        var baseline = new DimensionBestBaseline(matrix);

        // But on this specific task, sonnet actually wins - the frozen prior is wrong here.
        RegretTaskOutcome[] tasks =
        [
            new("t1", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(Score: 0.2, CostUsd: 0.0, TotalTokens: 100),
                ["claude-sonnet-4-5"] = new(Score: 0.95, CostUsd: 0.0, TotalTokens: 100),
            }),
        ];

        var result = RegretReplayEngine.Replay(tasks, baseline, Weights);

        // DimensionBest picks opus (frozen prior), oracle is sonnet -> nonzero regret.
        Assert.True(result.CumulativeRegret > 0d);
        Assert.Equal(0.2, result.AvgPerf, precision: 6);
    }

    [Fact]
    public void Replay_FrozenPriorAgreesWithTruePerTaskWinner_RegretIsZero()
    {
        var matrix = DimensionModelScoreMatrix.FromRows([new("p1", "code_generation", "claude-opus-4-6", 0.9)]);
        var baseline = new DimensionBestBaseline(matrix);

        RegretTaskOutcome[] tasks =
        [
            new("t1", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(Score: 1.0, CostUsd: 0.0, TotalTokens: 100),
                ["claude-sonnet-4-5"] = new(Score: 0.1, CostUsd: 0.0, TotalTokens: 100),
            }),
        ];

        var result = RegretReplayEngine.Replay(tasks, baseline, Weights);

        Assert.Equal(0d, result.CumulativeRegret, precision: 9);
    }

    [Fact]
    public void Route_NoCandidateHasAPriorAverage_ReturnsNull()
    {
        var matrix = DimensionModelScoreMatrix.FromRows([]);
        var baseline = new DimensionBestBaseline(matrix);
        var context = new RegretReplayContext("t1", "code_generation", ["claude-opus-4-6"]);

        Assert.Null(baseline.Route(context));
    }

    [Fact]
    public void Route_TiedAverages_BreaksTieByOrdinalModelName()
    {
        var matrix = DimensionModelScoreMatrix.FromRows(
        [
            new("p1", "code_generation", "claude-opus-4-6", 0.5),
            new("p1", "code_generation", "claude-sonnet-4-5", 0.5),
        ]);
        var baseline = new DimensionBestBaseline(matrix);
        var context = new RegretReplayContext("t1", "code_generation", ["claude-sonnet-4-5", "claude-opus-4-6"]);

        Assert.Equal("claude-opus-4-6", baseline.Route(context));
    }
}
