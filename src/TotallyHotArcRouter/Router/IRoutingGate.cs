namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Runtime kill switch for whether the proxy accepts LLM-forwarding requests. Checked by
/// <see cref="Proxy.ProxyMiddleware"/> before routing a request, and toggled from the GUI system tray via
/// <see cref="RoutingGateAdminGrpcService"/>. Independent of whether the process itself is running -
/// disabling routing keeps the Windows Service up and every admin/management surface (REST
/// <c>/admin/*</c>, the gRPC admin services, <c>/v1/models</c>) fully usable; it only stops new LLM traffic
/// from being forwarded.
/// </summary>
public interface IRoutingGate
{
    /// <summary>Gets whether the proxy currently accepts LLM-forwarding requests.</summary>
    bool IsEnabled { get; }

    /// <summary>Enables or disables routing, persisting the choice so it survives a service restart.</summary>
    /// <param name="enabled"><see langword="true"/> to accept LLM-forwarding requests, <see langword="false"/> to reject them.</param>
    void SetEnabled(bool enabled);
}