using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>Unit tests for <see cref="LinUcbBaseline"/>.</summary>
public class LinUcbBaselineTests
{
    private static readonly RewardWeights Weights = RewardWeights.Canonical;

    [Fact]
    public void Replay_OneArmStrictlyBetter_ConvergesToPickingIt()
    {
        var baseline = new LinUcbBaseline();
        var tasks = Enumerable.Range(0, 200)
            .Select(i => new RegretTaskOutcome(TaskId: $"t{i}", Dimension: "code_generation",
                Cells: new Dictionary<string, RegretOutcomeCell>
                {
                    ["good-model"] = new(0.9, 0.0, 100),
                    ["bad-model"] = new(0.1, 0.0, 100)
                }))
            .ToArray();

        var result = RegretReplayEngine.Replay(tasks: tasks, router: baseline, weights: Weights);

        // Early rounds explore both arms (equal prior); by the end the accumulated posterior should have
        // pulled good-model on the clear majority of tasks, driving AvgPerf well above the bad arm's score.
        Assert.True(condition: result.AvgPerf > 0.7,
            userMessage: $"AvgPerf was {result.AvgPerf}, expected convergence toward good-model's 0.9");
    }

    [Fact]
    public void WarmStart_SeedsPosteriorSoScoredStreamStartsExploiting()
    {
        RegretTaskOutcome[] probing =
        [
            new(TaskId: "p1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["good-model"] = new(0.9, 0.0, 100),
                ["bad-model"] = new(0.1, 0.0, 100)
            }),
            new(TaskId: "p2", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["good-model"] = new(0.9, 0.0, 100),
                ["bad-model"] = new(0.1, 0.0, 100)
            })
        ];

        var baseline = new LinUcbBaseline();
        baseline.WarmStart(probingTasks: probing, weights: Weights);

        var context = new RegretReplayContext(TaskId: "t1", Dimension: "code_generation",
            CandidateModelIds: ["good-model", "bad-model"]);

        Assert.Equal(expected: "good-model", actual: baseline.Route(context));
    }

    [Fact]
    public void Route_UnseenDimension_FallsBackToPriorAndTieBreaksOrdinal()
    {
        var baseline = new LinUcbBaseline();
        var context = new RegretReplayContext(TaskId: "t1", Dimension: "algorithm",
            CandidateModelIds: ["zeta-model", "alpha-model"]);

        Assert.Equal(expected: "alpha-model", actual: baseline.Route(context));
    }
}