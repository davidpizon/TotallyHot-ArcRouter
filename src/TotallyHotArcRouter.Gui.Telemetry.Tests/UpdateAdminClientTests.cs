using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="UpdateAdminClient"/> - the wire-to-view mapping and error translation behind the
/// System Settings window's "Software Update" section, mirroring <c>LlmRouterModelAdminClientTests</c>.
/// </summary>
public class UpdateAdminClientTests
{
    [Fact]
    public async Task GetStatusAsync_MapsEveryField()
    {
        var checkedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var stub = new StubClient
        {
            StatusResponse = new Contract.UpdateStatusResponse
            {
                CurrentVersion = "1.0.0",
                LatestVersion = "2.0.0",
                UpdateAvailable = true,
                CheckedAtUtc = Timestamp.FromDateTimeOffset(checkedAt),
                UnavailableReason = Contract.UpdateUnavailableReason.None,
                AssetDownloadUrl = "https://example.test/a.msi",
                AssetSha256 = "abc123",
            },
        };
        using var client = new UpdateAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.CurrentVersion.Should().Be("1.0.0");
        status.LatestVersion.Should().Be("2.0.0");
        status.UpdateAvailable.Should().BeTrue();
        status.CheckedAtUtc.Should().Be(checkedAt);
        status.UnavailableReason.Should().Be(UpdateUnavailableReasonInfo.None);
        status.AssetDownloadUrl.Should().Be("https://example.test/a.msi");
        status.AssetSha256.Should().Be("abc123");
    }

    [Fact]
    public async Task GetStatusAsync_UnsetCheckedAtUtc_MapsToNull()
    {
        var stub = new StubClient { StatusResponse = new Contract.UpdateStatusResponse() };
        using var client = new UpdateAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.CheckedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_NoAssetFieldsSet_MapsToNull()
    {
        var stub = new StubClient { StatusResponse = new Contract.UpdateStatusResponse() };
        using var client = new UpdateAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.AssetDownloadUrl.Should().BeNull();
        status.AssetSha256.Should().BeNull();
    }

    [Theory]
    [InlineData(Contract.UpdateUnavailableReason.NoReleasesPublished, UpdateUnavailableReasonInfo.NoReleasesPublished)]
    [InlineData(Contract.UpdateUnavailableReason.MalformedTag, UpdateUnavailableReasonInfo.MalformedTag)]
    [InlineData(Contract.UpdateUnavailableReason.AssetOrChecksumMissing, UpdateUnavailableReasonInfo.AssetOrChecksumMissing)]
    [InlineData(Contract.UpdateUnavailableReason.NetworkOrApiFailure, UpdateUnavailableReasonInfo.NetworkOrApiFailure)]
    [InlineData(Contract.UpdateUnavailableReason.Unspecified, UpdateUnavailableReasonInfo.None)]
    public async Task GetStatusAsync_MapsEveryUnavailableReason(Contract.UpdateUnavailableReason wire, UpdateUnavailableReasonInfo expected)
    {
        var stub = new StubClient
        {
            StatusResponse = new Contract.UpdateStatusResponse
            {
                UnavailableReason = wire,
                UnavailableDetail = "some detail",
            },
        };
        using var client = new UpdateAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.UnavailableReason.Should().Be(expected);
        status.UnavailableDetail.Should().Be("some detail");
    }

    [Fact]
    public async Task CheckNowAsync_ReturnsMappedStatus()
    {
        var stub = new StubClient
        {
            CheckNowResponse = new Contract.UpdateStatusResponse { CurrentVersion = "1.0.0", LatestVersion = "1.0.0" },
        };
        using var client = new UpdateAdminClient(stub);

        var status = await client.CheckNowAsync(TestContext.Current.CancellationToken);

        status.LatestVersion.Should().Be("1.0.0");
    }

    [Fact]
    public async Task NotifyApplyStartingAsync_ReturnsMappedOutcome()
    {
        var stub = new StubClient
        {
            NotifyResponse = new Contract.NotifyApplyStartingResponse { Acknowledged = true },
        };
        using var client = new UpdateAdminClient(stub);

        var outcome = await client.NotifyApplyStartingAsync("2.0.0", TestContext.Current.CancellationToken);

        outcome.Acknowledged.Should().BeTrue();
    }

    [Fact]
    public async Task GetStatusAsync_RouterUnavailable_ThrowsFlaggedException()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "down")) };
        using var client = new UpdateAdminClient(stub);

        var act = async () => await client.GetStatusAsync(TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<UpdateAdminException>();
        ex.Which.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task NotifyApplyStartingAsync_Rejected_ThrowsUnflaggedException()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.FailedPrecondition, "no update")) };
        using var client = new UpdateAdminClient(stub);

        var act = async () => await client.NotifyApplyStartingAsync("2.0.0", TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<UpdateAdminException>();
        ex.Which.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public async Task CheckNowAsync_Rejected_ThrowsUnflaggedException()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Internal, "boom")) };
        using var client = new UpdateAdminClient(stub);

        var act = async () => await client.CheckNowAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UpdateAdminException>();
    }

    [Fact]
    public void Constructor_NullGeneratedClient_Throws()
    {
        var act = () => new UpdateAdminClient((Contract.UpdateAdminService.UpdateAdminServiceClient)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class StubClient : Contract.UpdateAdminService.UpdateAdminServiceClient
    {
        public Contract.UpdateStatusResponse StatusResponse { get; init; } = new();
        public Contract.UpdateStatusResponse CheckNowResponse { get; init; } = new();
        public Contract.NotifyApplyStartingResponse NotifyResponse { get; init; } = new();
        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.UpdateStatusResponse> GetUpdateStatusAsync(
            Contract.GetUpdateStatusRequest request, CallOptions options) =>
            Call(StatusResponse);

        public override AsyncUnaryCall<Contract.UpdateStatusResponse> CheckForUpdatesNowAsync(
            Contract.CheckForUpdatesNowRequest request, CallOptions options) =>
            Call(CheckNowResponse);

        public override AsyncUnaryCall<Contract.NotifyApplyStartingResponse> NotifyApplyStartingAsync(
            Contract.NotifyApplyStartingRequest request, CallOptions options) =>
            Call(NotifyResponse);

        private AsyncUnaryCall<T> Call<T>(T response) =>
            new(
                Failure is null ? Task.FromResult(response) : Task.FromException<T>(Failure),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
    }
}
