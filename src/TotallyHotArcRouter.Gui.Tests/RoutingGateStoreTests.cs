using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="RoutingGateStore"/>: the background poll loop keeping <see cref="RoutingGateStore.IsReachable"/>/
/// <see cref="RoutingGateStore.IsEnabled"/> fresh for the tray context menu, the one-time
/// <see cref="RoutingGateStore.BecameUnusable"/> notification on a down-transition, and
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
        await using var store = new RoutingGateStore(client: client, pollInterval: FastPoll);

        await WaitUntilAsync(condition: () => store.IsReachable, timeout: WaitTimeout);

        store.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task PollLoop_UnavailableFailure_SetsUnreachable_AndRaisesBecameUnusableExactlyOnce()
    {
        var client = new FakeRoutingGateAdminClient();
        await using var store = new RoutingGateStore(client: client, pollInterval: FastPoll);
        await WaitUntilAsync(condition: () => store.IsReachable, timeout: WaitTimeout);

        var becameUnusableCount = 0;
        store.BecameUnusable += () => Interlocked.Increment(ref becameUnusableCount);
        client.GetFailure = new RoutingGateAdminException(message: "router is gone", isUnavailable: true);

        await WaitUntilAsync(condition: () => !store.IsReachable, timeout: WaitTimeout);
        // Give several more poll ticks a chance to run, to prove BecameUnusable doesn't fire again
        // while the router stays down.
        await Task.Delay(delay: FastPoll * 10, cancellationToken: TestContext.Current.CancellationToken);

        becameUnusableCount.Should().Be(1);
    }

    [Fact]
    public async Task PollLoop_UnavailableFailure_ReportsUnreachable()
    {
        var client = new FakeRoutingGateAdminClient
        {
            GetFailure = new RoutingGateAdminException(message: "router is gone", isUnavailable: true)
        };
        await using var store = new RoutingGateStore(client: client, pollInterval: FastPoll);

        await WaitUntilAsync(condition: () => store.ConnectionState == RouterConnectionState.Unreachable,
            timeout: WaitTimeout);

        store.IsReachable.Should().BeFalse();
        store.IsUsable.Should().BeFalse();
    }

    // The regression this whole distinction exists for: the router answered and refused the call (a
    // mismatched management token in the field), which the store used to record as "unreachable" - so the
    // tray told the user their perfectly healthy Windows service was stopped.
    [Fact]
    public async Task PollLoop_RejectedFailure_ReportsRejectedAndStaysReachable()
    {
        var client = new FakeRoutingGateAdminClient
        {
            GetFailure = new RoutingGateAdminException("Could not update the routing gate: bad token")
        };
        await using var store = new RoutingGateStore(client: client, pollInterval: FastPoll);

        await WaitUntilAsync(condition: () => store.ConnectionState == RouterConnectionState.Rejected,
            timeout: WaitTimeout);

        store.IsReachable.Should().BeTrue("a router that answers with an error is still reachable");
        store.IsUsable.Should().BeFalse("the routing toggle still has nothing it can act on");
        store.LastFailureMessage.Should().Be("Could not update the routing gate: bad token");
    }

    [Fact]
    public async Task PollLoop_RecoveringAfterAFailure_ClearsTheFailureMessage()
    {
        var client = new FakeRoutingGateAdminClient
        {
            GetFailure = new RoutingGateAdminException("bad token")
        };
        await using var store = new RoutingGateStore(client: client, pollInterval: FastPoll);
        await WaitUntilAsync(condition: () => store.ConnectionState == RouterConnectionState.Rejected,
            timeout: WaitTimeout);

        client.GetFailure = null;

        await WaitUntilAsync(condition: () => store.IsUsable, timeout: WaitTimeout);
        store.LastFailureMessage.Should().BeNull();
    }

    [Fact]
    public async Task PollLoop_RejectedFailure_RaisesBecameUnusableExactlyOnce()
    {
        var client = new FakeRoutingGateAdminClient();
        await using var store = new RoutingGateStore(client: client, pollInterval: FastPoll);
        await WaitUntilAsync(condition: () => store.IsUsable, timeout: WaitTimeout);

        var becameUnusableCount = 0;
        store.BecameUnusable += () => Interlocked.Increment(ref becameUnusableCount);
        client.GetFailure = new RoutingGateAdminException("bad token");

        await WaitUntilAsync(condition: () => store.ConnectionState == RouterConnectionState.Rejected,
            timeout: WaitTimeout);
        await Task.Delay(delay: FastPoll * 10, cancellationToken: TestContext.Current.CancellationToken);

        becameUnusableCount.Should().Be(1);
    }

    [Fact]
    public async Task EnableAsync_SetsEnabledAndReachable_AndReturnsTheConfirmedState()
    {
        // Matches the poll's own answer so a concurrent poll tick can't race this assertion to a different
        // value - only RoutingGateStore.SetAsync's own update is asserted here, not poll timing.
        var client = new FakeRoutingGateAdminClient { EnabledResult = true };
        await using var store = new RoutingGateStore(client: client, pollInterval: FastPoll);

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
        await using var store = new RoutingGateStore(client: client, pollInterval: FastPoll);

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
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");

            await Task.Delay(10, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakeRoutingGateAdminClient : IRoutingGateAdminClient
    {
        public bool EnabledResult { get; set; } = true;

        public Exception? GetFailure { get; set; }

        public bool? LastSetValue { get; private set; }

        public Task<bool> GetAsync(CancellationToken cancellationToken = default)
        {
            return GetFailure is not null ? Task.FromException<bool>(GetFailure) : Task.FromResult(EnabledResult);
        }

        public Task<bool> SetAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            LastSetValue = enabled;
            EnabledResult = enabled;
            return Task.FromResult(enabled);
        }
    }
}