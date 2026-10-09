namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Raised when a body capture cannot complete in bounded memory or disk (ADR-0019, "Bounded capture"):
/// the obscurer's look-back window overflowed, or free disk space fell below the reserve. The capture is
/// abandoned and the body recorded as missing, because a body is complete or absent, never a prefix.
/// </summary>
public sealed class SessionCaptureAbandonedException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionCaptureAbandonedException"/> class.
    /// </summary>
    /// <param name="message">Why the capture was abandoned; never contains body text.</param>
    public SessionCaptureAbandonedException(string message) : base(message)
    {
    }
}
