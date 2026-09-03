namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Why the model that served a request (<see cref="RoutingTelemetryEvent.RoutedModel"/>) differs from
/// the client's literal <c>model</c> string (<see cref="RoutingTelemetryEvent.RequestedModel"/>) - see
/// <c>docs/router/orchestrator-live-path-plan.md</c> §M2.2. Each value names a distinct cause a dashboard
/// must not merge into one generic "substituted" flag.
/// </summary>
public enum RoutingSubstitutionReason
{
    /// <summary>The routed model is exactly what the client named. No substitution occurred.</summary>
    None,

    /// <summary>The client asked for <c>"auto"</c> (any casing), delegating the choice to the router.</summary>
    AutoSelect,

    /// <summary>The client's named model is not in the configured <c>ModelList</c>.</summary>
    UnresolvedName,

    /// <summary>
    /// The client's named model resolved but is administratively stopped (operator Stop, or dropped by
    /// its provider's last endpoint scan).
    /// </summary>
    ModelStopped,

    /// <summary>
    /// The client's named model resolved and is enabled, but its circuit (or its whole provider's) is
    /// currently open.
    /// </summary>
    CircuitOpen,

    /// <summary>
    /// The primary candidate was attempted and failed at the transport layer (outage, timeout, 5xx); a
    /// later candidate in the fallback chain served the request instead.
    /// </summary>
    Failover
}