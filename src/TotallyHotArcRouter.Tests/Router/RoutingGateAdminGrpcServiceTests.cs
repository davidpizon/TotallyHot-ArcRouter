using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.Router;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="RoutingGateAdminGrpcService"/>: it must report the gate's current state and, on a
/// mutation, both apply it through <see cref="IRoutingGate.SetEnabled"/> and echo back the confirmed
/// post-mutation state.
/// </summary>
public sealed class RoutingGateAdminGrpcServiceTests
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
    public async Task GetRoutingGate_ReportsTheGatesCurrentState()
    {
        var gate = new FakeRoutingGate(isEnabled: false);
        var service = new RoutingGateAdminGrpcService(gate);

        var response =
            await service.GetRoutingGate(request: new Contract.GetRoutingGateRequest(), context: CreateContext());

        response.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetRoutingGate_AppliesTheMutation_AndEchoesTheConfirmedState()
    {
        var gate = new FakeRoutingGate(isEnabled: true);
        var service = new RoutingGateAdminGrpcService(gate);

        var response = await service.SetRoutingGate(request: new Contract.SetRoutingGateRequest { Enabled = false },
            context: CreateContext());

        gate.IsEnabled.Should().BeFalse();
        response.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ThrowsOnNullGate()
    {
        var act = () => new RoutingGateAdminGrpcService(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class FakeRoutingGate(bool isEnabled) : IRoutingGate
    {
        public bool IsEnabled { get; private set; } = isEnabled;

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
        }
    }
}