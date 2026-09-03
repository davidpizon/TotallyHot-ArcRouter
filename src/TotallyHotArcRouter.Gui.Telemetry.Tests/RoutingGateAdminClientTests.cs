using AwesomeAssertions;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="RoutingGateAdminClient"/> - the wire-to-view mapping and error translation behind
/// the tray's "Enable Routing"/"Disable Routing" toggle.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, mirroring
/// <c>RoutingModeAdminClientTests</c>.
/// </remarks>
public class RoutingGateAdminClientTests
{
    [Fact]
    public async Task GetAsync_ReturnsTheGatesCurrentState()
    {
        var stub = new StubClient { Response = new Contract.RoutingGateResponse { Enabled = false } };
        using var client = new RoutingGateAdminClient(stub);

        var enabled = await client.GetAsync(TestContext.Current.CancellationToken);

        enabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_ReturnsTheConfirmedPostMutationState()
    {
        var stub = new StubClient { Response = new Contract.RoutingGateResponse { Enabled = true } };
        using var client = new RoutingGateAdminClient(stub);

        var enabled = await client.SetAsync(true, cancellationToken: TestContext.Current.CancellationToken);

        enabled.Should().BeTrue();
        stub.LastSetRequest.Should().NotBeNull();
        stub.LastSetRequest!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient
            { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        using var client = new RoutingGateAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RoutingGateAdminException>(() =>
            client.GetAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not reach the router: the router is not reachable.");
        ex.InnerException.Should().BeOfType<RpcException>();
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient
            { Failure = new RpcException(new Status(statusCode: StatusCode.Internal, detail: "boom")) };
        using var client = new RoutingGateAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RoutingGateAdminException>(() =>
            client.SetAsync(false, cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not update the routing gate: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_rejected_read_names_the_read_not_the_update()
    {
        var stub = new StubClient
            { Failure = new RpcException(new Status(statusCode: StatusCode.Internal, detail: "boom")) };
        using var client = new RoutingGateAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RoutingGateAdminException>(() =>
            client.GetAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the routing gate: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new RoutingGateAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new RoutingGateAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void The_default_address_overload_targets_the_proxys_grpc_port()
    {
        using var client = new RoutingGateAdminClient();

        client.Should().NotBeNull();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RoutingGateAdminClient((Contract.RoutingGateAdminService.RoutingGateAdminServiceClient)null!));
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overload: the generated
    /// convenience overloads delegate to it, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.RoutingGateAdminService.RoutingGateAdminServiceClient
    {
        public Contract.RoutingGateResponse Response { get; init; } = new();

        public RpcException? Failure { get; init; }

        public Contract.SetRoutingGateRequest? LastSetRequest { get; private set; }

        public override AsyncUnaryCall<Contract.RoutingGateResponse> GetRoutingGateAsync(
            Contract.GetRoutingGateRequest request,
            CallOptions options)
        {
            return Respond();
        }

        public override AsyncUnaryCall<Contract.RoutingGateResponse> SetRoutingGateAsync(
            Contract.SetRoutingGateRequest request,
            CallOptions options)
        {
            LastSetRequest = request;
            return Respond();
        }

        private AsyncUnaryCall<Contract.RoutingGateResponse> Respond()
        {
            return new AsyncUnaryCall<Contract.RoutingGateResponse>(
                responseAsync: Failure is null
                    ? Task.FromResult(Response)
                    : Task.FromException<Contract.RoutingGateResponse>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}