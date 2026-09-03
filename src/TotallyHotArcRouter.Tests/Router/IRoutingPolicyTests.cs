using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="IRoutingPolicy.DecideOutcomeAsync"/>'s default implementation
/// (docs/router/self-organizing-classification-plan.md Phase T1c): a policy with no override gets a
/// correct-enough decision (the selected model, non-exploratory, propensity 1.0) wrapped around whatever
/// <see cref="IRoutingPolicy.SelectModelAsync(RoutingContext, RoutingSignals?, CancellationToken)"/> returns.
/// </summary>
public class IRoutingPolicyTests
{
    [Fact]
    public async Task DecideOutcomeAsync_DefaultImplementation_WrapsSelectModelAsyncResult()
    {
        IRoutingPolicy policy = new StubPolicy("picked-model");
        var context = new RoutingContext(Dimension: "live:code_generation", false,
            Candidates: [new RoutingCandidate(ModelName: "picked-model", Provider: "openai", false)]);

        var decision = await policy.DecideOutcomeAsync(context: context, null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "picked-model", actual: decision.SelectedModel);
        Assert.False(decision.IsExploratory);
        Assert.Equal(1.0, actual: decision.Propensity, 6);
        Assert.Equal(0, actual: decision.Confidence);
    }

    [Fact]
    public async Task DecideOutcomeAsync_DefaultImplementation_ForwardsSignalsToSelectModelAsync()
    {
        var policy = new RecordingStubPolicy("picked-model");
        var context = new RoutingContext(Dimension: "live:code_generation", false,
            Candidates: [new RoutingCandidate(ModelName: "picked-model", Provider: "openai", false)]);
        var signals = new RoutingSignals(TaskText: "task text", TaskEmbedding: [1f, 2f]);

        IRoutingPolicy asInterface = policy;
        await asInterface.DecideOutcomeAsync(context: context, signals: signals,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected: signals, actual: policy.LastSignals);
    }

    /// <summary>A minimal <see cref="IRoutingPolicy"/> with no overrides, so it exercises both interface defaults.</summary>
    private sealed class StubPolicy(string modelName) : IRoutingPolicy
    {
        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(modelName);
        }
    }

    /// <summary>
    /// Records the last <see cref="RoutingSignals"/> it was called with, to prove the default forwards them rather
    /// than discarding them.
    /// </summary>
    private sealed class RecordingStubPolicy(string modelName) : IRoutingPolicy
    {
        public RoutingSignals? LastSignals { get; private set; }

        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(modelName);
        }

        public Task<string> SelectModelAsync(RoutingContext context, RoutingSignals? signals,
            CancellationToken cancellationToken = default)
        {
            LastSignals = signals;
            return Task.FromResult(modelName);
        }
    }
}