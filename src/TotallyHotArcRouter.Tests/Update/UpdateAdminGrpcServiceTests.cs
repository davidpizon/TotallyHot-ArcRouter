using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Update;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Update;

/// <summary>
/// Covers <see cref="UpdateAdminGrpcService"/>'s three RPCs against fakes - no real Windows service, no
/// real installer process ever spawned (that all lives in the GUI now).
/// </summary>
public sealed class UpdateAdminGrpcServiceTests
{
    private sealed class FakeReleaseCheckClient : IReleaseCheckClient
    {
        public ReleaseCheckResult Result { get; set; } = ReleaseCheckResult.Resolved("1.0.0", "1.0.0", false, null, null);

        public Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private static ServerCallContext CreateContext() =>
        TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: TestContext.Current.CancellationToken,
            peer: "test-peer",
            authContext: null!,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });

    [Fact]
    public async Task GetUpdateStatus_BeforeAnyCheck_ReportsUnspecifiedWithNoTimestamp()
    {
        var service = new UpdateAdminGrpcService(new UpdateStateStore(), new FakeReleaseCheckClient(), NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.GetUpdateStatus(new Contract.GetUpdateStatusRequest(), CreateContext());

        Assert.False(response.UpdateAvailable);
        Assert.Null(response.CheckedAtUtc);
        Assert.Equal(Contract.UpdateUnavailableReason.Unspecified, response.UnavailableReason);
    }

    [Fact]
    public async Task GetUpdateStatus_AfterStateRecorded_ReturnsSnapshot()
    {
        var stateStore = new UpdateStateStore();
        stateStore.Record(ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, "https://example.test/a.msi", "abc"));
        var service = new UpdateAdminGrpcService(stateStore, new FakeReleaseCheckClient(), NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.GetUpdateStatus(new Contract.GetUpdateStatusRequest(), CreateContext());

        Assert.True(response.UpdateAvailable);
        Assert.Equal("2.0.0", response.LatestVersion);
        Assert.NotNull(response.CheckedAtUtc);
        Assert.Equal("https://example.test/a.msi", response.AssetDownloadUrl);
        Assert.Equal("abc", response.AssetSha256);
    }

    [Fact]
    public async Task GetUpdateStatus_NoUpdateAvailable_LeavesAssetFieldsUnset()
    {
        var stateStore = new UpdateStateStore();
        stateStore.Record(ReleaseCheckResult.Resolved("1.0.0", "1.0.0", false, null, null));
        var service = new UpdateAdminGrpcService(stateStore, new FakeReleaseCheckClient(), NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.GetUpdateStatus(new Contract.GetUpdateStatusRequest(), CreateContext());

        Assert.False(response.HasAssetDownloadUrl);
        Assert.False(response.HasAssetSha256);
    }

    [Fact]
    public async Task CheckForUpdatesNow_CallsClientAndRecordsIntoStateStore()
    {
        var releaseClient = new FakeReleaseCheckClient
        {
            Result = ReleaseCheckResult.Resolved("1.0.0", "3.0.0", true, "https://example.test/a.msi", "abc"),
        };
        var stateStore = new UpdateStateStore();
        var service = new UpdateAdminGrpcService(stateStore, releaseClient, NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.CheckForUpdatesNow(new Contract.CheckForUpdatesNowRequest(), CreateContext());

        Assert.True(response.UpdateAvailable);
        Assert.Equal("3.0.0", response.LatestVersion);
        Assert.True(stateStore.Current.Result!.IsUpdateAvailable);
    }

    [Fact]
    public async Task NotifyApplyStarting_AlwaysAcknowledges()
    {
        var service = new UpdateAdminGrpcService(new UpdateStateStore(), new FakeReleaseCheckClient(), NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.NotifyApplyStarting(
            new Contract.NotifyApplyStartingRequest { Version = "2.0.0" },
            CreateContext());

        Assert.True(response.Acknowledged);
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var stateStore = new UpdateStateStore();
        var releaseClient = new FakeReleaseCheckClient();
        var logger = NullLogger<UpdateAdminGrpcService>.Instance;

        Assert.Throws<ArgumentNullException>(() => new UpdateAdminGrpcService(null!, releaseClient, logger));
        Assert.Throws<ArgumentNullException>(() => new UpdateAdminGrpcService(stateStore, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new UpdateAdminGrpcService(stateStore, releaseClient, null!));
    }
}
