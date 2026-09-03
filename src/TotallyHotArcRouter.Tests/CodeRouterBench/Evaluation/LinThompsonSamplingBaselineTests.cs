using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>Unit tests for <see cref="LinThompsonSamplingBaseline"/>.</summary>
public class LinThompsonSamplingBaselineTests
{
    private static readonly RewardWeights Weights = RewardWeights.Canonical;

    [Fact]
    public void Replay_OneArmStrictlyBetter_ConvergesToPickingIt()
    {
        var baseline = new LinThompsonSamplingBaseline();
        var tasks = Enumerable.Range(0, 200)
            .Select(i => new RegretTaskOutcome(TaskId: $"t{i}", Dimension: "code_generation",
                Cells: new Dictionary<string, RegretOutcomeCell>
                {
                    ["good-model"] = new(0.9, 0.0, 100),
                    ["bad-model"] = new(0.1, 0.0, 100)
                }))
            .ToArray();

        var result = RegretReplayEngine.Replay(tasks: tasks, router: baseline, weights: Weights);

        Assert.True(condition: result.AvgPerf > 0.7,
            userMessage: $"AvgPerf was {result.AvgPerf}, expected convergence toward good-model's 0.9");
    }

    [Fact]
    public void Replay_SameSeed_IsDeterministicAcrossRuns()
    {
        RegretTaskOutcome[] tasks =
        [
            .. Enumerable.Range(0, 50).Select(i => new RegretTaskOutcome(TaskId: $"t{i}", Dimension: "code_generation",
                Cells: new Dictionary<string, RegretOutcomeCell>
                {
                    ["model-a"] = new(0.6, 0.0, 100),
                    ["model-b"] = new(0.5, 0.0, 100)
                }))
        ];

        var first = RegretReplayEngine.Replay(tasks: tasks, router: new LinThompsonSamplingBaseline(seed: 42),
            weights: Weights);
        var second = RegretReplayEngine.Replay(tasks: tasks, router: new LinThompsonSamplingBaseline(seed: 42),
            weights: Weights);

        Assert.Equal(expected: first.CumulativeRegret, actual: second.CumulativeRegret, 12);
        Assert.Equal(expected: first.AvgPerf, actual: second.AvgPerf, 12);
    }

    [Fact]
    public void WarmStart_SeededTwice_ProducesIdenticalPostWarmStartRoute()
    {
        RegretTaskOutcome[] probing =
        [
            new(TaskId: "p1", Dimension: "code_generation", Cells: new Dictionary<string, RegretOutcomeCell>
            {
                ["model-a"] = new(0.9, 0.0, 100),
                ["model-b"] = new(0.1, 0.0, 100)
            })
        ];

        var first = new LinThompsonSamplingBaseline(seed: 42);
        first.WarmStart(probingTasks: probing, weights: Weights);
        var second = new LinThompsonSamplingBaseline(seed: 42);
        second.WarmStart(probingTasks: probing, weights: Weights);

        var context = new RegretReplayContext(TaskId: "t1", Dimension: "code_generation",
            CandidateModelIds: ["model-a", "model-b"]);
        Assert.Equal(expected: first.Route(context), actual: second.Route(context));
    }
}