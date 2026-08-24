using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>Unit tests for <see cref="LinUcbBaseline"/>.</summary>
public class LinUcbBaselineTests
{
    private static readonly RewardWeights Weights = RewardWeights.Canonical;

    [Fact]
    public void Replay_OneArmStrictlyBetter_ConvergesToPickingIt()
    {
        var baseline = new LinUcbBaseline(alpha: 1d, lambda: 1d);
        var tasks = Enumerable.Range(0, 200)
            .Select(i => new RegretTaskOutcome($"t{i}", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["good-model"] = new(Score: 0.9, CostUsd: 0.0, TotalTokens: 100),
                ["bad-model"] = new(Score: 0.1, CostUsd: 0.0, TotalTokens: 100),
            }))
            .ToArray();

        var result = RegretReplayEngine.Replay(tasks, baseline, Weights);

        // Early rounds explore both arms (equal prior); by the end the accumulated posterior should have
        // pulled good-model on the clear majority of tasks, driving AvgPerf well above the bad arm's score.
        Assert.True(result.AvgPerf > 0.7, $"AvgPerf was {result.AvgPerf}, expected convergence toward good-model's 0.9");
    }

    [Fact]
    public void WarmStart_SeedsPosteriorSoScoredStreamStartsExploiting()
    {
        RegretTaskOutcome[] probing =
        [
            new("p1", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["good-model"] = new(Score: 0.9, CostUsd: 0.0, TotalTokens: 100),
                ["bad-model"] = new(Score: 0.1, CostUsd: 0.0, TotalTokens: 100),
            }),
            new("p2", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["good-model"] = new(Score: 0.9, CostUsd: 0.0, TotalTokens: 100),
                ["bad-model"] = new(Score: 0.1, CostUsd: 0.0, TotalTokens: 100),
            }),
        ];

        var baseline = new LinUcbBaseline(alpha: 1d, lambda: 1d);
        baseline.WarmStart(probing, Weights);

        var context = new RegretReplayContext("t1", "code_generation", ["good-model", "bad-model"]);

        Assert.Equal("good-model", baseline.Route(context));
    }

    [Fact]
    public void Route_UnseenDimension_FallsBackToPriorAndTieBreaksOrdinal()
    {
        var baseline = new LinUcbBaseline();
        var context = new RegretReplayContext("t1", "algorithm", ["zeta-model", "alpha-model"]);

        Assert.Equal("alpha-model", baseline.Route(context));
    }
}
