using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray.Tests;

/// <summary>
/// Covers <see cref="UpdateApplyMonitor"/>: a pending install is launched once, and an empty claim
/// does not launch anything.
/// </summary>
public sealed class UpdateApplyMonitorTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task PendingInstall_IsAppliedOnce_AndRaisesLaunched()
    {
        var client = new FakeUpdateClient
        {
            Claim = new ClaimedUpdateApply(
                Claimed: true,
                LatestVersion: "2.0.0",
                AssetDownloadUrl: "https://example.test/a.msi",
                AssetSha256: "abc")
        };
        var applier = new CountingApplier();
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new UpdateApplyMonitor(
            client: client,
            applier: applier,
            pollInterval: FastPoll);
        monitor.InstallLaunched += () => launched.TrySetResult();

        await launched.Task.WaitAsync(timeout: TimeSpan.FromSeconds(2),
            cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(delay: FastPoll * 4, cancellationToken: TestContext.Current.CancellationToken);

        applier.Calls.Should().Be(1);
        client.ClaimCalls.Should().Be(1);
    }

    [Fact]
    public async Task EmptyClaim_DoesNotApply()
    {
        var client = new FakeUpdateClient
        {
            Claim = new ClaimedUpdateApply(Claimed: false, LatestVersion: string.Empty, null, null)
        };
        var applier = new CountingApplier();
        await using var monitor = new UpdateApplyMonitor(
            client: client,
            applier: applier,
            pollInterval: FastPoll);

        await Task.Delay(delay: FastPoll * 4, cancellationToken: TestContext.Current.CancellationToken);

        applier.Calls.Should().Be(0);
        client.ClaimCalls.Should().BeGreaterThan(0);
    }

    private sealed class FakeUpdateClient : IUpdateAdminClient
    {
        public int ClaimCalls { get; private set; }

        public required ClaimedUpdateApply Claim { get; init; }

        public Task<UpdateStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<UpdateStatusInfo> CheckNowAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NotifyApplyStartingInfo> NotifyApplyStartingAsync(string version,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RequestApplyInfo> RequestApplyAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ClaimedUpdateApply> ClaimPendingApplyAsync(CancellationToken cancellationToken = default)
        {
            ClaimCalls++;
            return Task.FromResult(Claim);
        }
    }

    private sealed class CountingApplier : IMsiUpdateApplier
    {
        public int Calls { get; private set; }

        public Task<MsiApplyResult> ApplyAsync(string assetDownloadUrl, string assetSha256, string latestVersion,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(MsiApplyResult.Launched("started"));
        }
    }
}
