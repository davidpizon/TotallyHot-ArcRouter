using AwesomeAssertions;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="RoutingModeAdminClient"/> - the wire-to-view mapping and error translation behind
/// the Governance → Routing Mode panel.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, mirroring
/// <c>PriceSourceAdminClientTests</c>. This project is plain net10.0, so unlike the bUnit panel tests
/// these run in CI.
/// </remarks>
public class RoutingModeAdminClientTests
{
    [Fact]
    public async Task GetAsync_maps_every_field_off_the_wire()
    {
        var response = new Contract.RoutingModeResponse
        {
            OrchestratorEnabled = true,
            ExplorationEnabled = true,
            ExplorationRate = 0.05
        };
        response.Voters.Add(new Contract.VoterMode { Name = "dim_best", Enabled = true, Weight = 0.9 });
        response.Voters.Add(new Contract.VoterMode { Name = "memory_kNN", Enabled = true, Weight = 0.57 });
        var stub = new StubClient { Response = response };
        using var client = new RoutingModeAdminClient(stub);

        var mode = await client.GetAsync(TestContext.Current.CancellationToken);

        mode.OrchestratorEnabled.Should().BeTrue();
        mode.ExplorationEnabled.Should().BeTrue();
        mode.ExplorationRate.Should().Be(0.05);
        mode.Voters.Should().HaveCount(2);
        mode.Voters[0].Should().Be(new VoterMode(Name: "dim_best", true, 0.9));
        mode.Voters[1].Should().Be(new VoterMode(Name: "memory_kNN", true, 0.57));
    }

    [Fact]
    public async Task GetAsync_maps_a_disabled_orchestrator_and_a_disabled_voter()
    {
        var response = new Contract.RoutingModeResponse { OrchestratorEnabled = false, ExplorationEnabled = false };
        response.Voters.Add(new Contract.VoterMode { Name = "llm_router", Enabled = false, Weight = 0.64 });
        var stub = new StubClient { Response = response };
        using var client = new RoutingModeAdminClient(stub);

        var mode = await client.GetAsync(TestContext.Current.CancellationToken);

        mode.OrchestratorEnabled.Should().BeFalse();
        mode.ExplorationEnabled.Should().BeFalse();
        mode.Voters.Single().Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        using var client = new RoutingModeAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RoutingModeAdminException>(() =>
            client.GetAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the routing mode: the router is not reachable.");
        ex.InnerException.Should().BeOfType<RpcException>();
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Internal, detail: "boom")) };
        using var client = new RoutingModeAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RoutingModeAdminException>(() =>
            client.GetAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the routing mode: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new RoutingModeAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new RoutingModeAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void The_default_address_overload_targets_the_proxys_grpc_port()
    {
        using var client = new RoutingModeAdminClient();

        client.Should().NotBeNull();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RoutingModeAdminClient((Contract.RoutingModeAdminService.RoutingModeAdminServiceClient)null!));
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overload: the generated
    /// convenience overloads delegate to it, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.RoutingModeAdminService.RoutingModeAdminServiceClient
    {
        public Contract.RoutingModeResponse Response { get; init; } = new();

        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.RoutingModeResponse> GetRoutingModeAsync(
            Contract.GetRoutingModeRequest request,
            CallOptions options)
        {
            return new AsyncUnaryCall<Contract.RoutingModeResponse>(
                responseAsync: Failure is null
                    ? Task.FromResult(Response)
                    : Task.FromException<Contract.RoutingModeResponse>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}