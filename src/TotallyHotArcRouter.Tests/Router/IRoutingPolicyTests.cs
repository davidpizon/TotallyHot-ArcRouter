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
        var context = new RoutingContext("live:code_generation", IsUtility: false, [new RoutingCandidate("picked-model", "openai", IsFree: false)]);

        var decision = await policy.DecideOutcomeAsync(context, signals: null, TestContext.Current.CancellationToken);

        Assert.Equal("picked-model", decision.SelectedModel);
        Assert.False(decision.IsExploratory);
        Assert.Equal(1.0, decision.Propensity, precision: 6);
        Assert.Equal(0, decision.Confidence);
    }

    [Fact]
    public async Task DecideOutcomeAsync_DefaultImplementation_ForwardsSignalsToSelectModelAsync()
    {
        var policy = new RecordingStubPolicy("picked-model");
        var context = new RoutingContext("live:code_generation", IsUtility: false, [new RoutingCandidate("picked-model", "openai", IsFree: false)]);
        var signals = new RoutingSignals("task text", [1f, 2f]);

        IRoutingPolicy asInterface = policy;
        await asInterface.DecideOutcomeAsync(context, signals, TestContext.Current.CancellationToken);

        Assert.Same(signals, policy.LastSignals);
    }

    /// <summary>A minimal <see cref="IRoutingPolicy"/> with no overrides, so it exercises both interface defaults.</summary>
    private sealed class StubPolicy(string modelName) : IRoutingPolicy
    {
        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(modelName);
    }

    /// <summary>Records the last <see cref="RoutingSignals"/> it was called with, to prove the default forwards them rather than discarding them.</summary>
    private sealed class RecordingStubPolicy(string modelName) : IRoutingPolicy
    {
        public RoutingSignals? LastSignals { get; private set; }

        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(modelName);

        public Task<string> SelectModelAsync(RoutingContext context, RoutingSignals? signals, CancellationToken cancellationToken = default)
        {
            LastSignals = signals;
            return Task.FromResult(modelName);
        }
    }
}
