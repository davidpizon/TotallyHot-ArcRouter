using TotallyHot.ArcRouter.Router.TextGeneration;

namespace TotallyHot.ArcRouter.Tests.Router.TextGeneration;

/// <summary>
/// Covers <see cref="OnnxGenAiShutdown"/>'s "only if used, at most once" contract against an injected shutdown
/// action - never <see cref="OnnxGenAiShutdown.Process"/>, whose real shutdown is irreversible and would break
/// every later test in this process that touches GenAI.
/// </summary>
public sealed class OnnxGenAiShutdownTests
{
    [Fact]
    public void ShutdownIfUsed_NeverMarked_DoesNotShutDown()
    {
        var calls = 0;
        var shutdown = new OnnxGenAiShutdown(() => calls++);

        Assert.False(shutdown.ShutdownIfUsed());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ShutdownIfUsed_AfterMarkUsed_ShutsDownOnce()
    {
        var calls = 0;
        var shutdown = new OnnxGenAiShutdown(() => calls++);

        shutdown.MarkUsed();
        shutdown.MarkUsed();

        Assert.True(shutdown.ShutdownIfUsed());
        Assert.False(shutdown.ShutdownIfUsed());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ShutdownIfUsed_CalledConcurrently_ShutsDownExactlyOnce()
    {
        var calls = 0;
        var shutdown = new OnnxGenAiShutdown(() => Interlocked.Increment(ref calls));
        shutdown.MarkUsed();

        var performed = 0;
        Parallel.For(fromInclusive: 0, toExclusive: 32, body: _ =>
        {
            if (shutdown.ShutdownIfUsed()) Interlocked.Increment(ref performed);
        });

        Assert.Equal(1, performed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Constructor_NullShutdown_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OnnxGenAiShutdown(null!));
    }
}
