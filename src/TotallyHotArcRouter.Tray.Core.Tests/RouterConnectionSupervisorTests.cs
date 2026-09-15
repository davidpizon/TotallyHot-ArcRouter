using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray.Tests;

/// <summary>
/// Covers <see cref="RouterConnectionSupervisor"/>: connecting on first tick, retrying a failed connect,
/// swapping in a fresh monitor when the current one stops being usable (the router-restart case a session
/// cookie can't survive on its own), and never leaving <see cref="RouterConnectionSupervisor.Monitor"/>
/// observed to regress to <see langword="null"/> once it has succeeded once.
/// </summary>
public sealed class RouterConnectionSupervisorTests
{
    private static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Supervisor_FirstTick_ConnectsAndExposesAMonitor()
    {
        var connector = new FakeConnector();
        await using var supervisor = new RouterConnectionSupervisor(connector, "https://localhost:5004",
            retryInterval: FastRetry, monitorFactory: FakeMonitor);

        await WaitUntilAsync(() => supervisor.Monitor is not null, WaitTimeout);

        connector.ConnectCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Supervisor_ConnectFails_RetriesUntilItSucceeds()
    {
        var connector = new FakeConnector { FailNextConnects = 2 };
        await using var supervisor = new RouterConnectionSupervisor(connector, "https://localhost:5004",
            retryInterval: FastRetry, monitorFactory: FakeMonitor);

        await WaitUntilAsync(() => supervisor.Monitor is not null, WaitTimeout);

        connector.ConnectCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Supervisor_ReconnectFailsAfterTheMonitorGoesUnusable_NeverExposesANullMonitorRegression()
    {
        // Succeeds once, then the underlying client starts failing (simulating a router restart) while
        // every reconnect attempt also fails (the router still isn't back) - proves the stale-but-only
        // monitor is never torn down just because a reconnect attempt failed.
        var connector = new FakeConnector();
        await using var supervisor = new RouterConnectionSupervisor(connector, "https://localhost:5004",
            retryInterval: FastRetry, monitorFactory: FakeMonitor);
        await WaitUntilAsync(() => supervisor.Monitor is not null, WaitTimeout);
        var firstMonitor = supervisor.Monitor;

        ((FakeRoutingGateAdminClient)connector.LastClient!).GetFailure = new GrpcAdminException("bad session");
        await WaitUntilAsync(() => !firstMonitor!.IsUsable, WaitTimeout);
        connector.FailNextConnects = int.MaxValue;
        await Task.Delay(FastRetry * 10, TestContext.Current.CancellationToken);

        supervisor.Monitor.Should().BeSameAs(firstMonitor, "a failed reconnect attempt must not discard the only monitor there is");
    }

    [Fact]
    public async Task Supervisor_ReconnectsWhenTheCurrentMonitorStopsBeingUsable_AndRaisesReconnected()
    {
        var connector = new FakeConnector();
        await using var supervisor = new RouterConnectionSupervisor(connector, "https://localhost:5004",
            retryInterval: FastRetry, monitorFactory: FakeMonitor);
        await WaitUntilAsync(() => supervisor.Monitor is not null, WaitTimeout);
        var firstMonitor = supervisor.Monitor;

        var reconnectedCount = 0;
        supervisor.Reconnected += () => Interlocked.Increment(ref reconnectedCount);

        // Simulate the router restarting: the session cookie behind the current monitor's client is now
        // rejected on every call, so IsUsable goes false and the supervisor should reconnect.
        ((FakeRoutingGateAdminClient)connector.LastClient!).GetFailure = new GrpcAdminException("bad session");
        await WaitUntilAsync(() => !firstMonitor!.IsUsable, WaitTimeout);

        await WaitUntilAsync(() => supervisor.Monitor is not null && !ReferenceEquals(supervisor.Monitor, firstMonitor),
            WaitTimeout);

        reconnectedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    private static RoutingGateMonitor FakeMonitor(IRouterChannelProvider provider)
    {
        var recordingProvider = (FakeConnector.RecordingProvider)provider;
        return new RoutingGateMonitor(client: recordingProvider.Client, pollInterval: FastRetry);
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

    private sealed class FakeConnector : ISessionRouterConnector
    {
        public int ConnectCount { get; private set; }
        public int FailNextConnects { get; set; }
        public IRoutingGateAdminClient? LastClient { get; private set; }

        public Task<IRouterChannelProvider> ConnectAsync(string serverAddress,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            if (FailNextConnects > 0)
            {
                FailNextConnects--;
                return Task.FromException<IRouterChannelProvider>(new HttpRequestException("router unreachable"));
            }

            var client = new FakeRoutingGateAdminClient();
            LastClient = client;
            return Task.FromResult<IRouterChannelProvider>(new RecordingProvider(client, serverAddress));
        }

        public sealed class RecordingProvider(FakeRoutingGateAdminClient client, string serverAddress)
            : IRouterChannelProvider
        {
            public FakeRoutingGateAdminClient Client { get; } = client;
            public Grpc.Core.CallInvoker CallInvoker => throw new NotSupportedException("Use the monitor factory seam instead.");
            public string ServerAddress { get; } = serverAddress;
        }
    }

    private sealed class FakeRoutingGateAdminClient : IRoutingGateAdminClient
    {
        public bool EnabledResult { get; set; } = true;
        public Exception? GetFailure { get; set; }

        public Task<bool> GetAsync(CancellationToken cancellationToken = default)
        {
            return GetFailure is not null ? Task.FromException<bool>(GetFailure) : Task.FromResult(EnabledResult);
        }

        public Task<bool> SetAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            EnabledResult = enabled;
            return Task.FromResult(enabled);
        }
    }
}
