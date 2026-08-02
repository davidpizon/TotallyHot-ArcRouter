namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// One Serilog log event, broadcast to connected dashboards via <see cref="TelemetryBroadcaster"/> for
/// the GUI's Console tab live log stream. <see cref="Level"/> is normalized to the short form the
/// GUI's color-coding expects (DEBUG/INFO/WARN/ERROR/FATAL) rather than Serilog's own level names -
/// see <see cref="TelemetryLogEventSink.NormalizeLevel"/>.
/// </summary>
/// <param name="TimestampUtc">When the log event was emitted.</param>
/// <param name="Level">Normalized level: DEBUG, INFO, WARN, ERROR, or FATAL.</param>
/// <param name="Message">The rendered log message, with Serilog's structured properties substituted in.</param>
public sealed record LogLineEvent(DateTimeOffset TimestampUtc, string Level, string Message);

