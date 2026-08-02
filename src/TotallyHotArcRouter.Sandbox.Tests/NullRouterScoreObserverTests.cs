using TotallyHot.ArcRouter.Sandbox.Execution;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the no-op default <see cref="IRouterScoreObserver"/>.</summary>
public class NullRouterScoreObserverTests
{
    [Fact]
    public async Task ObserveAsync_CompletesWithoutThrowing()
    {
        var observer = new NullRouterScoreObserver();

        await observer.ObserveAsync(new SandboxResult(), TestContext.Current.CancellationToken);
    }
}

