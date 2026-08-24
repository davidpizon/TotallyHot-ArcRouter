using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>Unit tests for <see cref="LinThompsonSamplingBaseline"/>.</summary>
public class LinThompsonSamplingBaselineTests
{
    private static readonly RewardWeights Weights = RewardWeights.Canonical;

    [Fact]
    public void Replay_OneArmStrictlyBetter_ConvergesToPickingIt()
    {
        var baseline = new LinThompsonSamplingBaseline(v: 0.5, lambda: 1d, seed: 42);
        var tasks = Enumerable.Range(0, 200)
            .Select(i => new RegretTaskOutcome($"t{i}", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["good-model"] = new(Score: 0.9, CostUsd: 0.0, TotalTokens: 100),
                ["bad-model"] = new(Score: 0.1, CostUsd: 0.0, TotalTokens: 100),
            }))
            .ToArray();

        var result = RegretReplayEngine.Replay(tasks, baseline, Weights);

        Assert.True(result.AvgPerf > 0.7, $"AvgPerf was {result.AvgPerf}, expected convergence toward good-model's 0.9");
    }

    [Fact]
    public void Replay_SameSeed_IsDeterministicAcrossRuns()
    {
        RegretTaskOutcome[] tasks =
        [
            .. Enumerable.Range(0, 50).Select(i => new RegretTaskOutcome($"t{i}", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["model-a"] = new(Score: 0.6, CostUsd: 0.0, TotalTokens: 100),
                ["model-b"] = new(Score: 0.5, CostUsd: 0.0, TotalTokens: 100),
            })),
        ];

        var first = RegretReplayEngine.Replay(tasks, new LinThompsonSamplingBaseline(seed: 42), Weights);
        var second = RegretReplayEngine.Replay(tasks, new LinThompsonSamplingBaseline(seed: 42), Weights);

        Assert.Equal(first.CumulativeRegret, second.CumulativeRegret, precision: 12);
        Assert.Equal(first.AvgPerf, second.AvgPerf, precision: 12);
    }

    [Fact]
    public void WarmStart_SeededTwice_ProducesIdenticalPostWarmStartRoute()
    {
        RegretTaskOutcome[] probing =
        [
            new("p1", "code_generation", new Dictionary<string, RegretOutcomeCell>
            {
                ["model-a"] = new(Score: 0.9, CostUsd: 0.0, TotalTokens: 100),
                ["model-b"] = new(Score: 0.1, CostUsd: 0.0, TotalTokens: 100),
            }),
        ];

        var first = new LinThompsonSamplingBaseline(seed: 42);
        first.WarmStart(probing, Weights);
        var second = new LinThompsonSamplingBaseline(seed: 42);
        second.WarmStart(probing, Weights);

        var context = new RegretReplayContext("t1", "code_generation", ["model-a", "model-b"]);
        Assert.Equal(first.Route(context), second.Route(context));
    }
}
