using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray.Tests;

/// <summary>
/// Covers <see cref="TrayUpdateCoordinator"/>: caching the last check, refusing to apply with no verified
/// update, notifying the router best-effort before applying, and exiting the process only after a
/// successful launch - mirroring <c>TotallyHot.ArcRouter.Gui.Tests.UpdateStoreTests</c>' coverage shape for
/// the equivalent browser-GUI view-model.
/// </summary>
public sealed class TrayUpdateCoordinatorTests
{
    [Fact]
    public async Task CheckNowAsync_StoresTheResultAsStatus()
    {
        var client = new FakeUpdateAdminClient { StatusToReturn = Available() };
        var coordinator = new TrayUpdateCoordinator(client, new FakeMsiUpdateApplier());

        var result = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        result.Should().Be(client.StatusToReturn);
        coordinator.Status.Should().Be(client.StatusToReturn);
    }

    [Fact]
    public async Task ApplyAsync_WithNoStatusYetChecked_Throws()
    {
        var coordinator = new TrayUpdateCoordinator(new FakeUpdateAdminClient(), new FakeMsiUpdateApplier());

        var act = () => coordinator.ApplyAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ApplyAsync_WithNoUpdateAvailable_Throws()
    {
        var client = new FakeUpdateAdminClient { StatusToReturn = NotAvailable() };
        var coordinator = new TrayUpdateCoordinator(client, new FakeMsiUpdateApplier());
        await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        var act = () => coordinator.ApplyAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ApplyAsync_NotifiesTheRouterFirst_ThenApplies()
    {
        var client = new FakeUpdateAdminClient { StatusToReturn = Available() };
        var applier = new FakeMsiUpdateApplier { ResultToReturn = MsiApplyResult.Launched("launched") };
        var coordinator = new TrayUpdateCoordinator(client, applier, exitApplication: () => { });
        await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        var result = await coordinator.ApplyAsync(TestContext.Current.CancellationToken);

        client.NotifiedVersion.Should().Be("2.0.0");
        applier.LastAssetDownloadUrl.Should().Be("https://example.com/router.msi");
        applier.LastAssetSha256.Should().Be("abc123");
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_RouterNotifyFails_StillApplies()
    {
        var client = new FakeUpdateAdminClient
        {
            StatusToReturn = Available(),
            NotifyFailure = new GrpcAdminException("router is gone", isUnavailable: true)
        };
        var applier = new FakeMsiUpdateApplier { ResultToReturn = MsiApplyResult.Launched("launched") };
        var coordinator = new TrayUpdateCoordinator(client, applier, exitApplication: () => { });
        await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        var result = await coordinator.ApplyAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        applier.LastAssetDownloadUrl.Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyAsync_SuccessfulLaunch_InvokesExitCallback()
    {
        var client = new FakeUpdateAdminClient { StatusToReturn = Available() };
        var applier = new FakeMsiUpdateApplier { ResultToReturn = MsiApplyResult.Launched("launched") };
        var exited = false;
        var coordinator = new TrayUpdateCoordinator(client, applier, exitApplication: () => exited = true);
        await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        await coordinator.ApplyAsync(TestContext.Current.CancellationToken);

        exited.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_FailedLaunch_DoesNotInvokeExitCallback()
    {
        var client = new FakeUpdateAdminClient { StatusToReturn = Available() };
        var applier = new FakeMsiUpdateApplier { ResultToReturn = MsiApplyResult.Failure("checksum mismatch") };
        var exited = false;
        var coordinator = new TrayUpdateCoordinator(client, applier, exitApplication: () => exited = true);
        await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        var result = await coordinator.ApplyAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        exited.Should().BeFalse();
    }

    private static UpdateStatusInfo Available()
    {
        return new UpdateStatusInfo(
            CurrentVersion: "1.0.0",
            LatestVersion: "2.0.0",
            UpdateAvailable: true,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            UnavailableReason: UpdateUnavailableReasonInfo.None,
            UnavailableDetail: null,
            AssetDownloadUrl: "https://example.com/router.msi",
            AssetSha256: "abc123");
    }

    private static UpdateStatusInfo NotAvailable()
    {
        return new UpdateStatusInfo(
            CurrentVersion: "1.0.0",
            LatestVersion: "1.0.0",
            UpdateAvailable: false,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            UnavailableReason: UpdateUnavailableReasonInfo.None,
            UnavailableDetail: null);
    }

    private sealed class FakeUpdateAdminClient : IUpdateAdminClient
    {
        public string? NotifiedVersion { get; private set; }
        public Exception? NotifyFailure { get; set; }
        public UpdateStatusInfo StatusToReturn { get; set; } = NotAvailable();

        public Task<UpdateStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StatusToReturn);
        }

        public Task<UpdateStatusInfo> CheckNowAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StatusToReturn);
        }

        public Task<NotifyApplyStartingInfo> NotifyApplyStartingAsync(string version,
            CancellationToken cancellationToken = default)
        {
            NotifiedVersion = version;
            return NotifyFailure is not null
                ? Task.FromException<NotifyApplyStartingInfo>(NotifyFailure)
                : Task.FromResult(new NotifyApplyStartingInfo(true));
        }
    }

    private sealed class FakeMsiUpdateApplier : IMsiUpdateApplier
    {
        public string? LastAssetDownloadUrl { get; private set; }
        public string? LastAssetSha256 { get; private set; }
        public MsiApplyResult ResultToReturn { get; set; } = MsiApplyResult.Failure("not configured");

        public Task<MsiApplyResult> ApplyAsync(string assetDownloadUrl, string assetSha256, string latestVersion,
            CancellationToken cancellationToken = default)
        {
            LastAssetDownloadUrl = assetDownloadUrl;
            LastAssetSha256 = assetSha256;
            return Task.FromResult(ResultToReturn);
        }
    }
}
