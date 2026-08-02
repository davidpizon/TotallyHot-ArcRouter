namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// One execution-grounded sandbox signal, broadcast to connected dashboards as an optional live tile
/// alongside <see cref="RoutingTelemetryEvent"/>. Derived from a scored sandbox result; it is a
/// <b>heuristic</b> quality proxy for live traffic (no ground-truth tests), not a paper-grade metric.
/// </summary>
/// <param name="CorrelationId">Ties this signal to its <see cref="RoutingTelemetryEvent.CorrelationId"/>.</param>
/// <param name="SessionId">The routed request's session id.</param>
/// <param name="Dimension">The inferred task dimension (already live-namespaced by the observer).</param>
/// <param name="Model">The model the signal is attributed to.</param>
/// <param name="Language">The evaluated snippet's language.</param>
/// <param name="Tier">The isolation tier that produced the signal.</param>
/// <param name="SyntaxValid">Whether the structural check passed.</param>
/// <param name="Executed">Whether the snippet was actually executed.</param>
/// <param name="ExitCode">The exit code, or <see langword="null"/> when not executed.</param>
/// <param name="TimedOut">Whether the run hit the wall-clock ceiling.</param>
/// <param name="UnifiedScore">The unified score u_i in [0,1] fed into router memory.</param>
/// <param name="WallClockMs">Wall-clock duration in milliseconds.</param>
/// <param name="PeakMemoryBytes">Peak resident memory in bytes, or 0 when not measured.</param>
/// <param name="TimestampUtc">When the signal was produced.</param>
public sealed record SandboxSignalEvent(
    string CorrelationId,
    string SessionId,
    string Dimension,
    string Model,
    string Language,
    string Tier,
    bool SyntaxValid,
    bool Executed,
    int? ExitCode,
    bool TimedOut,
    double UnifiedScore,
    long WallClockMs,
    long PeakMemoryBytes,
    DateTimeOffset TimestampUtc);

