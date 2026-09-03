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
    private static ServerCallContext CreateContext()
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: TestContext.Current.CancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    [Fact]
    public async Task GetUpdateStatus_BeforeAnyCheck_ReportsUnspecifiedWithNoTimestamp()
    {
        var service = new UpdateAdminGrpcService(stateStore: new UpdateStateStore(),
            releaseCheckClient: new FakeReleaseCheckClient(), logger: NullLogger<UpdateAdminGrpcService>.Instance);

        var response =
            await service.GetUpdateStatus(request: new Contract.GetUpdateStatusRequest(), context: CreateContext());

        Assert.False(response.UpdateAvailable);
        Assert.Null(response.CheckedAtUtc);
        Assert.Equal(expected: Contract.UpdateUnavailableReason.Unspecified, actual: response.UnavailableReason);
    }

    [Fact]
    public async Task GetUpdateStatus_AfterStateRecorded_ReturnsSnapshot()
    {
        var stateStore = new UpdateStateStore();
        stateStore.Record(ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "2.0.0", true,
            assetDownloadUrl: "https://example.test/a.msi", assetSha256: "abc"));
        var service = new UpdateAdminGrpcService(stateStore: stateStore,
            releaseCheckClient: new FakeReleaseCheckClient(), logger: NullLogger<UpdateAdminGrpcService>.Instance);

        var response =
            await service.GetUpdateStatus(request: new Contract.GetUpdateStatusRequest(), context: CreateContext());

        Assert.True(response.UpdateAvailable);
        Assert.Equal(expected: "2.0.0", actual: response.LatestVersion);
        Assert.NotNull(response.CheckedAtUtc);
        Assert.Equal(expected: "https://example.test/a.msi", actual: response.AssetDownloadUrl);
        Assert.Equal(expected: "abc", actual: response.AssetSha256);
    }

    [Fact]
    public async Task GetUpdateStatus_NoUpdateAvailable_LeavesAssetFieldsUnset()
    {
        var stateStore = new UpdateStateStore();
        stateStore.Record(ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "1.0.0", false, null,
            null));
        var service = new UpdateAdminGrpcService(stateStore: stateStore,
            releaseCheckClient: new FakeReleaseCheckClient(), logger: NullLogger<UpdateAdminGrpcService>.Instance);

        var response =
            await service.GetUpdateStatus(request: new Contract.GetUpdateStatusRequest(), context: CreateContext());

        Assert.False(response.HasAssetDownloadUrl);
        Assert.False(response.HasAssetSha256);
    }

    [Fact]
    public async Task CheckForUpdatesNow_CallsClientAndRecordsIntoStateStore()
    {
        var releaseClient = new FakeReleaseCheckClient
        {
            Result = ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "3.0.0", true,
                assetDownloadUrl: "https://example.test/a.msi", assetSha256: "abc")
        };
        var stateStore = new UpdateStateStore();
        var service = new UpdateAdminGrpcService(stateStore: stateStore, releaseCheckClient: releaseClient,
            logger: NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.CheckForUpdatesNow(request: new Contract.CheckForUpdatesNowRequest(),
            context: CreateContext());

        Assert.True(response.UpdateAvailable);
        Assert.Equal(expected: "3.0.0", actual: response.LatestVersion);
        Assert.True(stateStore.Current.Result!.IsUpdateAvailable);
    }

    [Fact]
    public async Task NotifyApplyStarting_AlwaysAcknowledges()
    {
        var service = new UpdateAdminGrpcService(stateStore: new UpdateStateStore(),
            releaseCheckClient: new FakeReleaseCheckClient(), logger: NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.NotifyApplyStarting(
            request: new Contract.NotifyApplyStartingRequest { Version = "2.0.0" },
            context: CreateContext());

        Assert.True(response.Acknowledged);
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var stateStore = new UpdateStateStore();
        var releaseClient = new FakeReleaseCheckClient();
        var logger = NullLogger<UpdateAdminGrpcService>.Instance;

        Assert.Throws<ArgumentNullException>(() =>
            new UpdateAdminGrpcService(stateStore: null!, releaseCheckClient: releaseClient, logger: logger));
        Assert.Throws<ArgumentNullException>(() =>
            new UpdateAdminGrpcService(stateStore: stateStore, releaseCheckClient: null!, logger: logger));
        Assert.Throws<ArgumentNullException>(() =>
            new UpdateAdminGrpcService(stateStore: stateStore, releaseCheckClient: releaseClient, logger: null!));
    }

    private sealed class FakeReleaseCheckClient : IReleaseCheckClient
    {
        public ReleaseCheckResult Result { get; set; } =
            ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "1.0.0", false, null, null);

        public Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result);
        }
    }
}