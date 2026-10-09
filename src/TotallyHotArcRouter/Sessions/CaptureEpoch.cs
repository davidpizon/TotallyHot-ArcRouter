namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// A counter that the Clear action advances so a turn that began before it is never stored after it. A turn's
/// request carries the whole conversation so far, so a turn still being relayed (or queued for the writer) when
/// Clear runs would otherwise write the very history Clear just wiped back to disk. Each capture records the
/// value when its request arrives; the capture and the writer both drop a turn whose value is no longer current.
/// </summary>
public sealed class CaptureEpoch
{
    private long _current;

    /// <summary>Gets the current value; a turn is storable only while the value it began under is still this.</summary>
    public long Current => Volatile.Read(ref _current);

    /// <summary>Starts a new epoch, which invalidates every turn that began under an earlier one.</summary>
    /// <returns>The new value.</returns>
    public long Advance() => Interlocked.Increment(ref _current);
}
