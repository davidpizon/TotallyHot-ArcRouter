namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The routing-gate read/write operations the tray's "Enable Routing"/"Disable Routing" toggle needs. An
/// interface so <c>RoutingGateStore</c> can be unit-tested against a fake without a live proxy or a gRPC
/// channel.
/// </summary>
public interface IRoutingGateAdminClient
{
    /// <summary>Reads whether the router currently accepts LLM-forwarding requests.</summary>
    /// <exception cref="RoutingGateAdminException">The call failed or the router is unreachable.</exception>
    Task<bool> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Enables or disables routing, returning the confirmed post-mutation state.</summary>
    /// <exception cref="RoutingGateAdminException">The call failed or the router is unreachable.</exception>
    Task<bool> SetAsync(bool enabled, CancellationToken cancellationToken = default);
}
