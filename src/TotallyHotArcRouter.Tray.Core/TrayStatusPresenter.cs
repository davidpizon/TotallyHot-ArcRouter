namespace TotallyHot.ArcRouter.Tray;

/// <summary>
/// The Windows service states <see cref="TrayStatusPresenter"/> distinguishes - a decoupled mirror of
/// <c>System.ServiceProcess.ServiceControllerStatus</c>'s values so this library (and its tests) never
/// need a reference to <c>System.ServiceProcess.ServiceController</c>, which is meaningless outside the
/// WinForms shell that actually queries the service. The shell maps a real
/// <c>ServiceControllerStatus</c> onto this enum before calling <see cref="TrayStatusPresenter"/>.
/// </summary>
public enum RouterServiceStatus
{
    /// <summary>The service is stopped.</summary>
    Stopped,

    /// <summary>The service is starting.</summary>
    StartPending,

    /// <summary>The service is stopping.</summary>
    StopPending,

    /// <summary>The service is paused.</summary>
    Paused,

    /// <summary>The service is running.</summary>
    Running
}

/// <summary>
/// Formats the router's combined Windows-service and routing-gate-connection state into the short menu
/// caption and the longer balloon-notification text the tray shows - a straight port of
/// <c>TrayWindowManager.BuildRouterStatusLabel</c>/<c>ShowRouterUnavailableBalloon</c>'s classification
/// logic out of that file's raw Win32 P/Invoke shell, so it can be unit-tested without a live Windows
/// service or a native window. Pure and stateless: every case the tray can be in is a value of
/// (<see cref="RouterServiceStatus"/>?, <c>RouterConnectionState</c>), decided once here rather than
/// re-derived at each of the tray's two call sites (the menu caption, the balloon).
/// </summary>
public static class TrayStatusPresenter
{
    /// <summary>
    /// Builds the short status caption shown at the top of the tray's context menu when the router is not
    /// usable - e.g. <c>"Router service: stopped"</c>, <c>"Router: request rejected"</c>.
    /// </summary>
    /// <param name="serviceStatus">
    /// The Windows service's current status, or <see langword="null"/> when it could not be queried (the
    /// service isn't installed, or the query itself failed) - never conflated with "stopped".
    /// </param>
    /// <param name="connectionState">The routing-gate poll's last outcome.</param>
    public static string BuildStatusLabel(RouterServiceStatus? serviceStatus, RouterConnectionState connectionState)
    {
        return (serviceStatus, connectionState) switch
        {
            (RouterServiceStatus.Stopped, _) => "Router service: stopped",
            (RouterServiceStatus.StartPending, _) => "Router service: starting",
            (RouterServiceStatus.StopPending, _) => "Router service: stopping",
            (RouterServiceStatus.Paused, _) => "Router service: paused",
            (RouterServiceStatus.Running, RouterConnectionState.Rejected) => "Router: request rejected",
            (RouterServiceStatus.Running, _) => "Router: not responding yet",
            (null, RouterConnectionState.Rejected) => "Router: request rejected",
            _ => "Router: not responding"
        };
    }

    /// <summary>
    /// Builds the longer balloon-notification body shown once when the router transitions from usable to
    /// unusable (<c>RoutingGateMonitor.BecameUnusable</c>) - the same classification as
    /// <see cref="BuildStatusLabel"/>, but phrased as a complete sentence for a notification the user is
    /// not already looking at a menu to interpret.
    /// </summary>
    /// <param name="serviceStatus">
    /// The Windows service's current status, or <see langword="null"/> when it could not be queried.
    /// </param>
    /// <param name="connectionState">The routing-gate poll's last outcome.</param>
    public static string BuildBalloonMessage(RouterServiceStatus? serviceStatus, RouterConnectionState connectionState)
    {
        return (serviceStatus, connectionState) switch
        {
            (RouterServiceStatus.Stopped, _) => "The Windows service is stopped.",
            (RouterServiceStatus.StartPending, _) => "The Windows service is still starting.",
            (RouterServiceStatus.StopPending, _) => "The Windows service is shutting down.",
            (RouterServiceStatus.Paused, _) => "The Windows service is paused.",
            (RouterServiceStatus.Running, RouterConnectionState.Rejected) =>
                "The Windows service is running but rejected this app's request. Its management token may not match; restarting the service regenerates it.",
            (RouterServiceStatus.Running, RouterConnectionState.Unreachable) =>
                "The Windows service is running but is not accepting connections on its management port yet.",
            (RouterServiceStatus.Running, _) => "The router is temporarily unavailable.",
            (null, RouterConnectionState.Rejected) =>
                "The router rejected this app's request. Its management token may not match.",
            _ => "The router is not responding."
        };
    }
}
