using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// One finished request waiting on the single background writer (ADR-0018). The request thread fills it
/// after <c>CompleteAsync</c> and does not wait for SQLite. Spool references, not a second copy of the body,
/// ride along when session capture is on (ADR-0019).
/// </summary>
internal sealed class ServedRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServedRequest"/> class.
    /// </summary>
    /// <param name="work">Telemetry facts already extracted on the request thread. Null only for a capture-only enqueue.</param>
    /// <param name="capture">The finished session turn, or null when capture is off or the epoch was already stale.</param>
    /// <param name="onDropped">Marks the transcript row terminal. One-shot; safe from the writer thread.</param>
    /// <param name="startEpoch">The capture epoch the turn began under. Zero when there is no capture.</param>
    public ServedRequest(QueuedTelemetryWork? work, SessionCaptureItem? capture, Action? onDropped, long startEpoch)
    {
        Work = work;
        Capture = capture;
        OnDropped = onDropped;
        StartEpoch = startEpoch;
    }

    /// <summary>Gets the telemetry facts, or null when this item only stores a session turn.</summary>
    public QueuedTelemetryWork? Work { get; }

    /// <summary>Gets the finished session turn, or null when there is nothing to append.</summary>
    public SessionCaptureItem? Capture { get; }

    /// <summary>Gets the one-shot drop callback, or null when no transcript row can be waiting.</summary>
    public Action? OnDropped { get; }

    /// <summary>Gets the epoch the turn began under.</summary>
    public long StartEpoch { get; }

    /// <summary>
    /// Gets or sets the in-flight gauge hold taken before enqueue returned. The writer releases it when the
    /// item is written, dropped, or abandoned. A double dispose does not drive the gauge negative.
    /// </summary>
    public IDisposable? GaugeHold { get; set; }
}
