using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="RoutingGateStore"/>: the background poll loop keeping <see cref="RoutingGateStore.IsReachable"/>/
/// <see cref="RoutingGateStore.IsEnabled"/> fresh for the tray context menu, the one-time
/// <see cref="RoutingGateStore.BecameUnreachable"/> notification on a down-transition, and
/// <see cref="RoutingGateStore.EnableAsync"/>/<see cref="RoutingGateStore.DisableAsync"/> applying immediately
/// rather than waiting for the next poll tick.
/// </summary>
public sealed class RoutingGateStoreTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task PollLoop_SuccessfulPoll_SetsReachableAndEnabled()
    {
        var client = new FakeRoutingGateAdminClient { EnabledResult = false };
        await using var store = new RoutingGateStore(client, pollInterval: FastPoll);

        await WaitUntilAsync(() => store.IsReachable, WaitTimeout);

        store.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task PollLoop_FailedPoll_SetsUnreachable_AndRaisesBecameUnreachableExactlyOnce()
    {
        var client = new FakeRoutingGateAdminClient();
        await using var store = new RoutingGateStore(client, pollInterval: FastPoll);
        await WaitUntilAsync(() => store.IsReachable, WaitTimeout);

        var becameUnreachableCount = 0;
        store.BecameUnreachable += () => Interlocked.Increment(ref becameUnreachableCount);
        client.GetFailure = new RoutingGateAdminException("router is gone", isUnavailable: true);

        await WaitUntilAsync(() => !store.IsReachable, WaitTimeout);
        // Give several more poll ticks a chance to run, to prove BecameUnreachable doesn't fire again
        // while the router stays down.
        await Task.Delay(FastPoll * 10, TestContext.Current.CancellationToken);

        becameUnreachableCount.Should().Be(1);
    }

    [Fact]
    public async Task EnableAsync_SetsEnabledAndReachable_AndReturnsTheConfirmedState()
    {
        // Matches the poll's own answer so a concurrent poll tick can't race this assertion to a different
        // value - only RoutingGateStore.SetAsync's own update is asserted here, not poll timing.
        var client = new FakeRoutingGateAdminClient { EnabledResult = true };
        await using var store = new RoutingGateStore(client, pollInterval: FastPoll);

        var confirmed = await store.EnableAsync(TestContext.Current.CancellationToken);

        confirmed.Should().BeTrue();
        store.IsEnabled.Should().BeTrue();
        store.IsReachable.Should().BeTrue();
        client.LastSetValue.Should().BeTrue();
    }

    [Fact]
    public async Task DisableAsync_SetsDisabled_AndReturnsTheConfirmedState()
    {
        var client = new FakeRoutingGateAdminClient { EnabledResult = false };
        await using var store = new RoutingGateStore(client, pollInterval: FastPoll);

        var confirmed = await store.DisableAsync(TestContext.Current.CancellationToken);

        confirmed.Should().BeFalse();
        store.IsEnabled.Should().BeFalse();
        client.LastSetValue.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakeRoutingGateAdminClient : IRoutingGateAdminClient
    {
        public bool EnabledResult { get; set; } = true;

        public Exception? GetFailure { get; set; }

        public bool? LastSetValue { get; private set; }

        public Task<bool> GetAsync(CancellationToken cancellationToken = default) =>
            GetFailure is not null ? Task.FromException<bool>(GetFailure) : Task.FromResult(EnabledResult);

        public Task<bool> SetAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            LastSetValue = enabled;
            EnabledResult = enabled;
            return Task.FromResult(enabled);
        }
    }
}
