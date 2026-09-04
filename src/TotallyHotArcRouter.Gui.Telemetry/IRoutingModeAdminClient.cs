namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The routing-mode read operation the Governance → Routing Mode panel needs. An interface so
/// <c>RoutingModeStore</c> can be unit-tested against a fake without a live proxy or a gRPC channel.
/// </summary>
public interface IRoutingModeAdminClient
{
    /// <summary>Reads the router's current routing configuration.</summary>
    /// <exception cref="RoutingModeAdminException">The call failed or the router is unreachable.</exception>
    Task<RoutingMode> GetAsync(CancellationToken cancellationToken = default);
}