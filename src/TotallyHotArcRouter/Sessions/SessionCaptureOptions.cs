namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Tunables for the session capture writer, bound from the <c>SessionCapture</c> configuration section. Whether
/// capture runs at all is not here: that is only the Transcription Capture toggle (ADR-0019).
/// </summary>
public sealed class SessionCaptureOptions
{
    /// <summary>Gets the configuration section name.</summary>
    public const string SectionName = "SessionCapture";

    /// <summary>
    /// Gets how many captured turns may wait for the writer. When the queue is full the producer waits rather
    /// than dropping a turn, which holds a finished request's bookkeeping but never the client's response.
    /// </summary>
    public int QueueCapacity { get; init; } = 256;

    /// <summary>Gets how long shutdown waits for queued turns to be written before abandoning the rest.</summary>
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the free disk space that must remain after a captured body's second copy (the commit re-seals the
    /// spool into the session file), below which the capture is abandoned and the body is recorded as missing.
    /// </summary>
    public long MinFreeDiskBytes { get; init; } = SessionBodySpool.DefaultMinFreeBytes;
}
