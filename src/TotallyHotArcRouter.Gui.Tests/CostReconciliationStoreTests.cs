using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="CostReconciliationStore"/>: the load/run round trips, the unreachable-load state,
/// a rejected run rethrowing while still recording the failure, and <see cref="CostReconciliationStore.IsRunning"/>
/// (docs/router/agent-cost-tracking.md §5.8).
/// </summary>
public sealed class CostReconciliationStoreTests
{
    private static readonly IReadOnlyList<ProviderReconciliationStatus> SampleProviders =
    [
        new ProviderReconciliationStatus("openai", new DateOnly(2026, 1, 15), 10.50m, 9.75m,
            new DateTimeOffset(2026, 1, 16, 3, 0, 0, offset: TimeSpan.Zero))
    ];

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        var act = () => new CostReconciliationStore((ICostReconciliationAdminClient)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task LoadAsync_Success_PopulatesProviders()
    {
        var client = new FakeCostReconciliationAdminClient { StatusResult = SampleProviders };
        var store = new CostReconciliationStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeTrue();
        store.Providers.Should().BeEquivalentTo(SampleProviders);
    }

    [Fact]
    public async Task LoadAsync_ClientThrows_SetsUnreachableWithoutThrowing()
    {
        var client = new FakeCostReconciliationAdminClient
        { StatusFailure = new GrpcAdminException(message: "router is gone", isUnavailable: true) };
        var store = new CostReconciliationStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeFalse();
        store.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task RunNowAsync_Success_PublishesResultAndClearsIsRunning()
    {
        var client = new FakeCostReconciliationAdminClient { RunNowResult = SampleProviders };
        var store = new CostReconciliationStore(client);
        var runningDuringCall = false;
        client.OnRunNow = () => runningDuringCall = store.IsRunning;

        await store.RunNowAsync(TestContext.Current.CancellationToken);

        runningDuringCall.Should().BeTrue();
        store.IsRunning.Should().BeFalse();
        store.IsReachable.Should().BeTrue();
        store.Providers.Should().BeEquivalentTo(SampleProviders);
    }

    [Fact]
    public async Task RunNowAsync_Rejected_RethrowsAndClearsIsRunning()
    {
        var client = new FakeCostReconciliationAdminClient
        { RunNowFailure = new GrpcAdminException(message: "reconciler blew up", isUnavailable: false) };
        var store = new CostReconciliationStore(client);

        var act = () => store.RunNowAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GrpcAdminException>();
        store.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Dispose_OverCallerSuppliedClient_DoesNotDisposeTheClient()
    {
        var client = new FakeCostReconciliationAdminClient();
        var store = new CostReconciliationStore(client);

        store.Dispose();

        client.Disposed.Should().BeFalse();
    }

    private sealed class FakeCostReconciliationAdminClient : ICostReconciliationAdminClient, IDisposable
    {
        public IReadOnlyList<ProviderReconciliationStatus> StatusResult { get; set; } = [];

        public Exception? StatusFailure { get; set; }

        public IReadOnlyList<ProviderReconciliationStatus> RunNowResult { get; set; } = [];

        public Exception? RunNowFailure { get; set; }

        public Action? OnRunNow { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }

        public Task<IReadOnlyList<ProviderReconciliationStatus>> GetStatusAsync(
            CancellationToken cancellationToken = default)
        {
            return StatusFailure is not null
                ? Task.FromException<IReadOnlyList<ProviderReconciliationStatus>>(StatusFailure)
                : Task.FromResult(StatusResult);
        }

        public Task<IReadOnlyList<ProviderReconciliationStatus>> RunNowAsync(
            CancellationToken cancellationToken = default)
        {
            OnRunNow?.Invoke();
            return RunNowFailure is not null
                ? Task.FromException<IReadOnlyList<ProviderReconciliationStatus>>(RunNowFailure)
                : Task.FromResult(RunNowResult);
        }
    }
}
