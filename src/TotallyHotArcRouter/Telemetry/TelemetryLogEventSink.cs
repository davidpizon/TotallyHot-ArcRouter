using Serilog.Core;
using Serilog.Events;
using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// A Serilog <see cref="ILogEventSink"/> that forwards every log event to connected dashboards as a
/// <see cref="LogLineEvent"/> via <see cref="ITelemetryPublisher"/> (backed by
/// <see cref="TelemetryGrpcService"/>'s stream), powering the GUI's Console tab. Wired into the
/// pipeline in <c>Program.cs</c> alongside (not replacing) the existing Console sink. Conversation-body
/// excerpts (#184 phase 3) are dropped here so the Console tab never shows opt-in body text; ADR-0020
/// will later re-admit them for sessions with a content grant.
/// </summary>
public sealed class TelemetryLogEventSink : ILogEventSink
{
    private readonly ITelemetryPublisher _publisher;

    /// <param name="publisher">Receives every emitted log line as a <see cref="LogLineEvent"/>.</param>
    public TelemetryLogEventSink(ITelemetryPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Properties.ContainsKey(ConversationBodyLogging.PropertyName)) return;

        var line = new LogLineEvent(
            TimestampUtc: logEvent.Timestamp.ToUniversalTime(),
            Level: NormalizeLevel(logEvent.Level),
            Message: logEvent.RenderMessage());

        // Emit is a synchronous callback invoked directly on the calling thread by every Log.*() call
        // in the app - it must never block on network I/O. PublishLogLineAsync is itself
        // fault-isolated (never throws, see ITelemetryPublisher's contract), so this is a safe
        // fire-and-forget.
        _ = _publisher.PublishLogLineAsync(line);
    }

    /// <summary>
    /// Maps Serilog's <see cref="LogEventLevel"/> to the short form the GUI's color-coding expects
    /// (see docs/gui/console-tab-plan.md). <see cref="LogEventLevel.Verbose"/> has no dedicated color
    /// in the spec, so it folds into DEBUG.
    /// </summary>
    public static string NormalizeLevel(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => "DEBUG",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARN",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "FATAL",
            _ => "INFO"
        };
    }
}