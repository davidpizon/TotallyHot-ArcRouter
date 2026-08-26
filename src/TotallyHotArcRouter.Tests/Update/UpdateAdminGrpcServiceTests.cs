using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Update;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Update;

/// <summary>
/// Covers <see cref="UpdateAdminGrpcService"/>'s three RPCs against fakes - no real Windows service, no
/// real <c>Updater.exe</c> process ever spawned.
/// </summary>
public sealed class UpdateAdminGrpcServiceTests
{
    private sealed class FakeReleaseCheckClient : IReleaseCheckClient
    {
        public ReleaseCheckResult Result { get; set; } = ReleaseCheckResult.Resolved("1.0.0", "1.0.0", false, null, null);

        public Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class FakeUpdateApplier : IUpdateApplier
    {
        public ApplyUpdateResult Result { get; set; } = ApplyUpdateResult.Handoff("ok");
        public ReleaseCheckResult? LastApplied { get; private set; }

        public Task<ApplyUpdateResult> ApplyAsync(ReleaseCheckResult update, CancellationToken cancellationToken = default)
        {
            LastApplied = update;
            return Task.FromResult(Result);
        }
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
        var service = new UpdateAdminGrpcService(new UpdateStateStore(), new FakeReleaseCheckClient(), new FakeUpdateApplier(), NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.GetUpdateStatus(new Contract.GetUpdateStatusRequest(), CreateContext());

        Assert.False(response.UpdateAvailable);
        Assert.Null(response.CheckedAtUtc);
        Assert.Equal(Contract.UpdateUnavailableReason.Unspecified, response.UnavailableReason);
    }

    [Fact]
    public async Task GetUpdateStatus_AfterStateRecorded_ReturnsSnapshot()
    {
        var stateStore = new UpdateStateStore();
        stateStore.Record(ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, "https://example.test/a.zip", "abc"));
        var service = new UpdateAdminGrpcService(stateStore, new FakeReleaseCheckClient(), new FakeUpdateApplier(), NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.GetUpdateStatus(new Contract.GetUpdateStatusRequest(), CreateContext());

        Assert.True(response.UpdateAvailable);
        Assert.Equal("2.0.0", response.LatestVersion);
        Assert.NotNull(response.CheckedAtUtc);
    }

    [Fact]
    public async Task CheckForUpdatesNow_CallsClientAndRecordsIntoStateStore()
    {
        var releaseClient = new FakeReleaseCheckClient
        {
            Result = ReleaseCheckResult.Resolved("1.0.0", "3.0.0", true, "https://example.test/a.zip", "abc"),
        };
        var stateStore = new UpdateStateStore();
        var service = new UpdateAdminGrpcService(stateStore, releaseClient, new FakeUpdateApplier(), NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.CheckForUpdatesNow(new Contract.CheckForUpdatesNowRequest(), CreateContext());

        Assert.True(response.UpdateAvailable);
        Assert.Equal("3.0.0", response.LatestVersion);
        Assert.True(stateStore.Current.Result!.IsUpdateAvailable);
    }

    [Fact]
    public async Task ApplyUpdate_NoUpdateKnownAvailable_ThrowsFailedPrecondition()
    {
        var service = new UpdateAdminGrpcService(new UpdateStateStore(), new FakeReleaseCheckClient(), new FakeUpdateApplier(), NullLogger<UpdateAdminGrpcService>.Instance);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.ApplyUpdate(new Contract.ApplyUpdateRequest(), CreateContext()));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task ApplyUpdate_UpdateKnownAvailable_CallsApplierAndReturnsOutcome()
    {
        var stateStore = new UpdateStateStore();
        var available = ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, "https://example.test/a.zip", "abc");
        stateStore.Record(available);
        var applier = new FakeUpdateApplier { Result = ApplyUpdateResult.Handoff("handed off") };
        var service = new UpdateAdminGrpcService(stateStore, new FakeReleaseCheckClient(), applier, NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.ApplyUpdate(new Contract.ApplyUpdateRequest(), CreateContext());

        Assert.True(response.Succeeded);
        Assert.Equal("handed off", response.Message);
        Assert.Same(available, applier.LastApplied);
    }

    [Fact]
    public async Task ApplyUpdate_DownloadOrChecksumFailure_ReturnsFailureWithoutThrowing()
    {
        var stateStore = new UpdateStateStore();
        stateStore.Record(ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, "https://example.test/a.zip", "abc"));
        var applier = new FakeUpdateApplier { Result = ApplyUpdateResult.Failure("checksum mismatch") };
        var service = new UpdateAdminGrpcService(stateStore, new FakeReleaseCheckClient(), applier, NullLogger<UpdateAdminGrpcService>.Instance);

        var response = await service.ApplyUpdate(new Contract.ApplyUpdateRequest(), CreateContext());

        Assert.False(response.Succeeded);
        Assert.Equal("checksum mismatch", response.Message);
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var stateStore = new UpdateStateStore();
        var releaseClient = new FakeReleaseCheckClient();
        var applier = new FakeUpdateApplier();
        var logger = NullLogger<UpdateAdminGrpcService>.Instance;

        Assert.Throws<ArgumentNullException>(() => new UpdateAdminGrpcService(null!, releaseClient, applier, logger));
        Assert.Throws<ArgumentNullException>(() => new UpdateAdminGrpcService(stateStore, null!, applier, logger));
        Assert.Throws<ArgumentNullException>(() => new UpdateAdminGrpcService(stateStore, releaseClient, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new UpdateAdminGrpcService(stateStore, releaseClient, applier, null!));
    }
}
