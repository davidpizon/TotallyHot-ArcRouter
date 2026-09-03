using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>Covers <see cref="FrozenOptionsMonitor{TOptions}"/>'s fixed-value contract.</summary>
public class FrozenOptionsMonitorTests
{
    [Fact]
    public void CurrentValue_ReturnsTheConstructedValue()
    {
        var monitor = new FrozenOptionsMonitor<Options>(new Options { Value = 42 });

        Assert.Equal(42, actual: monitor.CurrentValue.Value);
        Assert.Equal(42, actual: monitor.Get(name: null).Value);
        Assert.Equal(42, actual: monitor.Get("named").Value);
    }

    [Fact]
    public void OnChange_ReturnsADisposableThatNeverInvokesTheListener()
    {
        var monitor = new FrozenOptionsMonitor<Options>(new Options { Value = 1 });
        var invoked = false;

        using var subscription = monitor.OnChange((_, _) => invoked = true);

        Assert.False(invoked);
    }

    [Fact]
    public void Constructor_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FrozenOptionsMonitor<Options>(null!));
    }

    private sealed class Options
    {
        public int Value { get; init; }
    }
}