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
            new CodeRouterBenchResultRow(TaskId: "p1", Dimension: "code_generation", Model: "claude-opus-4-6", 0.9),
            new CodeRouterBenchResultRow(TaskId: "p2", Dimension: "code_generation", Model: "claude-opus-4-6", 0.9),
            new CodeRouterBenchResultRow(TaskId: "p3", Dimension: "code_generation", Model: "claude-sonnet-4-5", 0.5)
        ]);
        var baseline = new DimensionBestBaseline(matrix);

        // But on this specific task, sonnet actually wins - the frozen prior is wrong here.
        RegretTaskOutcome[] tasks =
        [
            new(TaskId: "t1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(0.2, 0.0, 100),
                ["claude-sonnet-4-5"] = new(0.95, 0.0, 100)
            })
        ];

        var result = RegretReplayEngine.Replay(tasks: tasks, router: baseline, weights: Weights);

        // DimensionBest picks opus (frozen prior), oracle is sonnet -> nonzero regret.
        Assert.True(result.CumulativeRegret > 0d);
        Assert.Equal(0.2, actual: result.AvgPerf, 6);
    }

    [Fact]
    public void Replay_FrozenPriorAgreesWithTruePerTaskWinner_RegretIsZero()
    {
        var matrix = DimensionModelScoreMatrix.FromRows([
            new CodeRouterBenchResultRow(TaskId: "p1", Dimension: "code_generation", Model: "claude-opus-4-6", 0.9)
        ]);
        var baseline = new DimensionBestBaseline(matrix);

        RegretTaskOutcome[] tasks =
        [
            new(TaskId: "t1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["claude-opus-4-6"] = new(1.0, 0.0, 100),
                ["claude-sonnet-4-5"] = new(0.1, 0.0, 100)
            })
        ];

        var result = RegretReplayEngine.Replay(tasks: tasks, router: baseline, weights: Weights);

        Assert.Equal(0d, actual: result.CumulativeRegret, 9);
    }

    [Fact]
    public void Route_NoCandidateHasAPriorAverage_ReturnsNull()
    {
        var matrix = DimensionModelScoreMatrix.FromRows([]);
        var baseline = new DimensionBestBaseline(matrix);
        var context = new RegretReplayContext(TaskId: "t1", Dimension: "code_generation",
            CandidateModelIds: ["claude-opus-4-6"]);

        Assert.Null(baseline.Route(context));
    }

    [Fact]
    public void Route_TiedAverages_BreaksTieByOrdinalModelName()
    {
        var matrix = DimensionModelScoreMatrix.FromRows(
        [
            new CodeRouterBenchResultRow(TaskId: "p1", Dimension: "code_generation", Model: "claude-opus-4-6", 0.5),
            new CodeRouterBenchResultRow(TaskId: "p1", Dimension: "code_generation", Model: "claude-sonnet-4-5", 0.5)
        ]);
        var baseline = new DimensionBestBaseline(matrix);
        var context = new RegretReplayContext(TaskId: "t1", Dimension: "code_generation",
            CandidateModelIds: ["claude-sonnet-4-5", "claude-opus-4-6"]);

        Assert.Equal(expected: "claude-opus-4-6", actual: baseline.Route(context));
    }
}