using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// gRPC service backing the GUI system tray's "Enable Routing"/"Disable Routing" toggle: reads and mutates
/// whether <see cref="Proxy.ProxyMiddleware"/> currently accepts LLM-forwarding requests. Mapped by
/// <see cref="Proxy.ProxyServer"/> onto the same loopback TLS endpoint as <c>TelemetryService</c> and the
/// other admin services, unconditionally like <see cref="RoutingModeAdminGrpcService"/> and
/// <c>UpdateAdminGrpcService</c> - whether the proxy accepts requests is core operational state, not an
/// optional add-on that can be absent.
/// </summary>
public sealed class RoutingGateAdminGrpcService : Contract.RoutingGateAdminService.RoutingGateAdminServiceBase
{
    private readonly IRoutingGate _gate;

    /// <summary>Initializes a new instance of the <see cref="RoutingGateAdminGrpcService"/> class.</summary>
    public RoutingGateAdminGrpcService(IRoutingGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    /// <inheritdoc/>
    public override Task<Contract.RoutingGateResponse> GetRoutingGate(
        Contract.GetRoutingGateRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new Contract.RoutingGateResponse { Enabled = _gate.IsEnabled });
    }

    /// <inheritdoc/>
    public override Task<Contract.RoutingGateResponse> SetRoutingGate(
        Contract.SetRoutingGateRequest request,
        ServerCallContext context)
    {
        _gate.SetEnabled(request.Enabled);
        return Task.FromResult(new Contract.RoutingGateResponse { Enabled = _gate.IsEnabled });
    }
}