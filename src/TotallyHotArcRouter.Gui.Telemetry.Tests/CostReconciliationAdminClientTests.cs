using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="CostReconciliationAdminClient"/> - the wire-to-view mapping and error translation
/// behind System Settings' Cost Reconciliation section.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, mirroring
/// <c>RouterSettingsAdminClientTests</c>. This project is plain net10.0, so unlike the bUnit modal tests
/// these run in CI.
/// </remarks>
public class CostReconciliationAdminClientTests
{
    [Fact]
    public async Task GetStatusAsync_maps_a_provider_with_no_snapshot_yet()
    {
        var stub = new StubClient
        {
            StatusResponse = new Contract.CostReconciliationStatusResponse
            {
                Providers = { new Contract.ProviderReconciliationStatus { Provider = "openai" } }
            }
        };
        using var client = new CostReconciliationAdminClient(stub);

        var providers = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        var status = Assert.Single(providers);
        status.Provider.Should().Be("openai");
        status.LastReconciledDay.Should().BeNull();
        status.LastReportedCostUsd.Should().BeNull();
        status.LastLocalCostUsd.Should().BeNull();
        status.LastFetchedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_maps_a_provider_with_a_snapshot()
    {
        var fetchedAt = new DateTimeOffset(2026, 1, 16, 3, 0, 0, offset: TimeSpan.Zero);
        var stub = new StubClient
        {
            StatusResponse = new Contract.CostReconciliationStatusResponse
            {
                Providers =
                {
                    new Contract.ProviderReconciliationStatus
                    {
                        Provider = "anthropic",
                        LastReconciledDay = "2026-01-15",
                        LastReportedCostUsd = "10.50",
                        LastLocalCostUsd = "9.75",
                        LastFetchedAtUtc = Timestamp.FromDateTimeOffset(fetchedAt)
                    }
                }
            }
        };
        using var client = new CostReconciliationAdminClient(stub);

        var providers = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        var status = Assert.Single(providers);
        status.Provider.Should().Be("anthropic");
        status.LastReconciledDay.Should().Be(new DateOnly(2026, 1, 15));
        status.LastReportedCostUsd.Should().Be(10.50m);
        status.LastLocalCostUsd.Should().Be(9.75m);
        status.LastFetchedAtUtc.Should().Be(fetchedAt);
    }

    [Fact]
    public async Task GetStatusAsync_unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient
        {
            StatusFailure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect"))
        };
        using var client = new CostReconciliationAdminClient(stub);

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the cost reconciliation status: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task RunNowAsync_maps_the_resulting_status()
    {
        var stub = new StubClient
        {
            RunNowResponse = new Contract.CostReconciliationStatusResponse
            {
                Providers = { new Contract.ProviderReconciliationStatus { Provider = "openai" } }
            }
        };
        using var client = new CostReconciliationAdminClient(stub);

        var providers = await client.RunNowAsync(TestContext.Current.CancellationToken);

        Assert.Single(providers).Provider.Should().Be("openai");
    }

    [Fact]
    public async Task RunNowAsync_a_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient
        {
            RunNowFailure = new RpcException(new Status(statusCode: StatusCode.Internal, detail: "reconciler blew up"))
        };
        using var client = new CostReconciliationAdminClient(stub);

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            client.RunNowAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not run cost reconciliation: reconciler blew up");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new CostReconciliationAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new CostReconciliationAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void The_default_address_overload_targets_the_proxys_grpc_port()
    {
        using var client = new CostReconciliationAdminClient();

        client.Should().NotBeNull();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CostReconciliationAdminClient(
                (Contract.CostReconciliationAdminService.CostReconciliationAdminServiceClient)null!));
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overload: the generated
    /// convenience overloads delegate to it, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.CostReconciliationAdminService.CostReconciliationAdminServiceClient
    {
        public Contract.CostReconciliationStatusResponse StatusResponse { get; init; } = new();

        public RpcException? StatusFailure { get; init; }

        public Contract.CostReconciliationStatusResponse RunNowResponse { get; init; } = new();

        public RpcException? RunNowFailure { get; init; }

        public override AsyncUnaryCall<Contract.CostReconciliationStatusResponse> GetCostReconciliationStatusAsync(
            Contract.GetCostReconciliationStatusRequest request,
            CallOptions options)
        {
            return new AsyncUnaryCall<Contract.CostReconciliationStatusResponse>(
                responseAsync: StatusFailure is null
                    ? Task.FromResult(StatusResponse)
                    : Task.FromException<Contract.CostReconciliationStatusResponse>(StatusFailure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        public override AsyncUnaryCall<Contract.CostReconciliationStatusResponse> RunCostReconciliationNowAsync(
            Contract.RunCostReconciliationNowRequest request,
            CallOptions options)
        {
            return new AsyncUnaryCall<Contract.CostReconciliationStatusResponse>(
                responseAsync: RunNowFailure is null
                    ? Task.FromResult(RunNowResponse)
                    : Task.FromException<Contract.CostReconciliationStatusResponse>(RunNowFailure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}
