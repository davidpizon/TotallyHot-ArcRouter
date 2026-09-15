using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray.Tests;

/// <summary>
/// Tests for <see cref="RoutingGateMonitor"/>: the background poll loop keeping <see cref="RoutingGateMonitor.IsReachable"/>/
/// <see cref="RoutingGateMonitor.IsEnabled"/> fresh for the tray context menu, the one-time
/// <see cref="RoutingGateMonitor.BecameUnusable"/> notification on a down-transition, and
/// <see cref="RoutingGateMonitor.EnableAsync"/>/<see cref="RoutingGateMonitor.DisableAsync"/> applying
/// immediately rather than waiting for the next poll tick. Mirrors
/// <c>TotallyHot.ArcRouter.Gui.Tests.RoutingGateStoreTests</c>' coverage shape - see
/// <see cref="RoutingGateMonitor"/>'s remarks for why this is a tray-owned port rather than a shared type.
/// </summary>
public sealed class RoutingGateMonitorTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task PollLoop_SuccessfulPoll_SetsReachableAndEnabled()
    {
        var client = new FakeRoutingGateAdminClient { EnabledResult = false };
        await using var monitor = new RoutingGateMonitor(client: client, pollInterval: FastPoll);

        await WaitUntilAsync(condition: () => monitor.IsReachable, timeout: WaitTimeout);

        monitor.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task PollLoop_UnavailableFailure_SetsUnreachable_AndRaisesBecameUnusableExactlyOnce()
    {
        var client = new FakeRoutingGateAdminClient();
        await using var monitor = new RoutingGateMonitor(client: client, pollInterval: FastPoll);
        await WaitUntilAsync(condition: () => monitor.IsReachable, timeout: WaitTimeout);

        var becameUnusableCount = 0;
        monitor.BecameUnusable += () => Interlocked.Increment(ref becameUnusableCount);
        client.GetFailure = new GrpcAdminException(message: "router is gone", isUnavailable: true);

        await WaitUntilAsync(condition: () => !monitor.IsReachable, timeout: WaitTimeout);
        await Task.Delay(delay: FastPoll * 10, cancellationToken: TestContext.Current.CancellationToken);

        becameUnusableCount.Should().Be(1);
    }

    [Fact]
    public async Task PollLoop_UnavailableFailure_ReportsUnreachable()
    {
        var client = new FakeRoutingGateAdminClient
        {
            GetFailure = new GrpcAdminException(message: "router is gone", isUnavailable: true)
        };
        await using var monitor = new RoutingGateMonitor(client: client, pollInterval: FastPoll);

        await WaitUntilAsync(condition: () => monitor.ConnectionState == RouterConnectionState.Unreachable,
            timeout: WaitTimeout);

        monitor.IsReachable.Should().BeFalse();
        monitor.IsUsable.Should().BeFalse();
    }

    [Fact]
    public async Task PollLoop_RejectedFailure_ReportsRejectedAndStaysReachable()
    {
        var client = new FakeRoutingGateAdminClient
        {
            GetFailure = new GrpcAdminException("Could not update the routing gate: bad token")
        };
        await using var monitor = new RoutingGateMonitor(client: client, pollInterval: FastPoll);

        await WaitUntilAsync(condition: () => monitor.ConnectionState == RouterConnectionState.Rejected,
            timeout: WaitTimeout);

        monitor.IsReachable.Should().BeTrue("a router that answers with an error is still reachable");
        monitor.IsUsable.Should().BeFalse("the routing toggle still has nothing it can act on");
        monitor.LastFailureMessage.Should().Be("Could not update the routing gate: bad token");
    }

    [Fact]
    public async Task PollLoop_RecoveringAfterAFailure_ClearsTheFailureMessage()
    {
        var client = new FakeRoutingGateAdminClient
        {
            GetFailure = new GrpcAdminException("bad token")
        };
        await using var monitor = new RoutingGateMonitor(client: client, pollInterval: FastPoll);
        await WaitUntilAsync(condition: () => monitor.ConnectionState == RouterConnectionState.Rejected,
            timeout: WaitTimeout);

        client.GetFailure = null;

        await WaitUntilAsync(condition: () => monitor.IsUsable, timeout: WaitTimeout);
        monitor.LastFailureMessage.Should().BeNull();
    }

    [Fact]
    public async Task EnableAsync_SetsEnabledAndReachable_AndReturnsTheConfirmedState()
    {
        var client = new FakeRoutingGateAdminClient { EnabledResult = true };
        await using var monitor = new RoutingGateMonitor(client: client, pollInterval: FastPoll);

        var confirmed = await monitor.EnableAsync(TestContext.Current.CancellationToken);

        confirmed.Should().BeTrue();
        monitor.IsEnabled.Should().BeTrue();
        monitor.IsReachable.Should().BeTrue();
        client.LastSetValue.Should().BeTrue();
    }

    [Fact]
    public async Task DisableAsync_SetsDisabled_AndReturnsTheConfirmedState()
    {
        var client = new FakeRoutingGateAdminClient { EnabledResult = false };
        await using var monitor = new RoutingGateMonitor(client: client, pollInterval: FastPoll);

        var confirmed = await monitor.DisableAsync(TestContext.Current.CancellationToken);

        confirmed.Should().BeFalse();
        monitor.IsEnabled.Should().BeFalse();
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
