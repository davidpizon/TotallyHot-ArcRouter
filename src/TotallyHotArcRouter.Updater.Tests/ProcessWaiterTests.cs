using AwesomeAssertions;
using TotallyHot.ArcRouter.Updater;

namespace TotallyHot.ArcRouter.Updater.Tests;

public class ProcessWaiterTests
{
    [Fact]
    public async Task WaitForExitAsync_NoSuchProcess_ReturnsTrueImmediately()
    {
        var waiter = new ProcessWaiter();

        // Process ids are 32-bit; this one is vanishingly unlikely to be a live process on the test
        // machine, exercising the "already exited or never existed" trivial-success path without
        // spawning anything.
        var result = await waiter.WaitForExitAsync(int.MaxValue - 1, TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForExitAsync_StillRunningProcess_ReturnsFalseAfterTimeout()
    {
        var waiter = new ProcessWaiter();

        // The current test process is definitely still running for the duration of this call.
        var result = await waiter.WaitForExitAsync(Environment.ProcessId, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        result.Should().BeFalse();
    }
}
