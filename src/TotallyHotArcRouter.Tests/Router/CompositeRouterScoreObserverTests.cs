using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="CompositeRouterScoreObserver"/> - docs/router/live-feedback-learning-plan.md Phase
/// 2c: both registered observers must receive every result, and one throwing must not stop the other.
/// </summary>
public class CompositeRouterScoreObserverTests
{
    [Fact]
    public async Task ObserveAsync_BothObserversReceiveTheResult()
    {
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        var composite = new CompositeRouterScoreObserver([first, second], NullLogger<CompositeRouterScoreObserver>.Instance);

        var result = new SandboxResult { Model = "m", RequestCorrelationId = "c" };

        await composite.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Same(result, Assert.Single(first.Observed));
        Assert.Same(result, Assert.Single(second.Observed));
    }

    [Fact]
    public async Task ObserveAsync_FirstObserverThrows_SecondStillObserves()
    {
        var throwing = new ThrowingObserver();
        var second = new RecordingObserver();
        var composite = new CompositeRouterScoreObserver([throwing, second], NullLogger<CompositeRouterScoreObserver>.Instance);

        var result = new SandboxResult { Model = "m", RequestCorrelationId = "c" };

        await composite.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Same(result, Assert.Single(second.Observed));
    }

    [Fact]
    public async Task ObserveAsync_NullResult_Throws()
    {
        var composite = new CompositeRouterScoreObserver([], NullLogger<CompositeRouterScoreObserver>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => composite.ObserveAsync(null!, TestContext.Current.CancellationToken));
    }

    private sealed class RecordingObserver : IRouterScoreObserver
    {
        public List<SandboxResult> Observed { get; } = [];

        public Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default)
        {
            Observed.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IRouterScoreObserver
    {
        public Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
