using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="NullRegretHarnessRunner"/>, the fallback used when <c>ProxyServer</c> is constructed
/// without a real <see cref="IRegretHarnessRunner"/> configured - it must always decline rather than throw,
/// since the admin gRPC service that calls it is mapped unconditionally.
/// </summary>
public class NullRegretHarnessRunnerTests
{
    [Fact]
    public void LastResult_IsAlwaysNull()
    {
        var runner = new NullRegretHarnessRunner();

        Assert.Null(runner.LastResult);
    }

    [Fact]
    public async Task RunAsync_ReturnsADeclinedResult_WithoutThrowing()
    {
        var runner = new NullRegretHarnessRunner();

        var result = await runner.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: RegretHarnessRunResultKind.Declined, actual: result.Kind);
        Assert.NotNull(result.Message);
        Assert.Empty(result.Splits);
        Assert.Null(runner.LastResult);
    }
}
