using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Update;

namespace TotallyHot.ArcRouter.Tests.Update;

/// <summary>
/// Covers <see cref="UpdateCheckHostedService"/>'s polling behavior against a fake
/// <see cref="IReleaseCheckClient"/> and <see cref="IUpdateStateStore"/> - no real HTTP calls and no
/// multi-hour timer: <see cref="UpdateCheckHostedService.RunOneCheckAsync"/> exercises one cycle
/// directly, and the "disabled" and "short-interval-runs-multiple-times" cases use a shrunk interval
/// bounded well under this repo's 5-second test cap.
/// </summary>
public sealed class UpdateCheckHostedServiceTests
{
    private static UpdateCheckHostedService CreateService(
        FakeReleaseCheckClient client,
        IUpdateStateStore stateStore,
        UpdateOptions? options = null)
    {
        return new UpdateCheckHostedService(releaseCheckClient: client, stateStore: stateStore,
            options: Options.Create(options ?? new UpdateOptions()),
            logger: NullLogger<UpdateCheckHostedService>.Instance);
    }

    [Fact]
    public async Task RunOneCheckAsync_RecordsResultIntoStateStore()
    {
        var client = new FakeReleaseCheckClient
        {
            ResultFactory = () => ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "2.0.0", true,
                assetDownloadUrl: "https://example.test/a.zip", assetSha256: "abc123")
        };
        var stateStore = new UpdateStateStore();
        var service = CreateService(client: client, stateStore: stateStore);

        await service.RunOneCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: client.CallCount);
        Assert.NotNull(stateStore.Current.Result);
        Assert.True(stateStore.Current.Result!.IsUpdateAvailable);
        Assert.NotNull(stateStore.Current.CheckedAtUtc);
    }

    [Fact]
    public async Task RunOneCheckAsync_ClientThrows_LogsAndDoesNotPropagate()
    {
        var client = new FakeReleaseCheckClient { ResultFactory = () => throw new InvalidOperationException("boom") };
        var stateStore = new UpdateStateStore();
        var service = CreateService(client: client, stateStore: stateStore);

        await service.RunOneCheckAsync(TestContext.Current.CancellationToken);

        // No result recorded (the exception surfaced before Record was called) - and, critically, nothing
        // propagated out of RunOneCheckAsync for a caller (the real ExecuteAsync loop) to crash on.
        Assert.Null(stateStore.Current.Result);
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_NeverCallsClient()
    {
        var client = new FakeReleaseCheckClient();
        var stateStore = new UpdateStateStore();
        var service = CreateService(client: client, stateStore: stateStore,
            options: new UpdateOptions { Enabled = false });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StartAsync(cts.Token);
        await Task.Delay(50, cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: client.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_RunsInitialCheckShortlyAfterStartup_NotOnlyAfterFirstInterval()
    {
        var client = new FakeReleaseCheckClient();
        var stateStore = new UpdateStateStore();
        var service = new UpdateCheckHostedService(
            releaseCheckClient: client,
            stateStore: stateStore,
            options: Options.Create(new UpdateOptions { Enabled = true, PollInterval = TimeSpan.FromHours(6) }),
            logger: NullLogger<UpdateCheckHostedService>.Instance)
        {
            InitialDelayOverride = TimeSpan.FromMilliseconds(10)
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            // The poll interval is 6 hours, so the only way a call could have landed within this bounded
            // wait is the initial-delay path, not the timer tick.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (client.CallCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            await cts.CancelAsync();
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, actual: client.CallCount);
    }

    private sealed class FakeReleaseCheckClient : IReleaseCheckClient
    {
        public int CallCount { get; private set; }

        public Func<ReleaseCheckResult> ResultFactory { get; init; } = () =>
            ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "1.0.0", false, null, null);

        public Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ResultFactory());
        }
    }
}