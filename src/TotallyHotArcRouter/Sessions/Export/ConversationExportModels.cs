namespace TotallyHot.ArcRouter.Sessions.Export;

/// <summary>Why <see cref="ConversationExportWriter"/> refused to start an export.</summary>
public enum ConversationExportFailure
{
    /// <summary>Another export is already running; the writer allows one at a time.</summary>
    InProgress,

    /// <summary>The destination is not an absolute, unused path outside the data directory.</summary>
    InvalidDestination,

    /// <summary>The destination volume cannot hold the export plus the free-space reserve.</summary>
    InsufficientDiskSpace,
}

/// <summary>
/// Raised when an export is refused before any file is written. A caller maps <see cref="Reason"/> to its
/// own status (for instance a failed precondition for <see cref="ConversationExportFailure.InProgress"/>).
/// </summary>
public sealed class ConversationExportException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationExportException"/> class.
    /// </summary>
    /// <param name="reason">Why the export was refused.</param>
    /// <param name="message">A message that names the rule without echoing user content.</param>
    public ConversationExportException(ConversationExportFailure reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Gets why the export was refused.</summary>
    public ConversationExportFailure Reason { get; }
}

/// <summary>What an export has written so far.</summary>
/// <param name="TurnsWritten">How many turns are in the zip so far.</param>
/// <param name="BytesWritten">The stored plaintext bytes of the bodies written so far, before the zip compresses them.</param>
public sealed record ConversationExportProgress(int TurnsWritten, long BytesWritten);

/// <summary>What a finished export wrote.</summary>
/// <param name="Path">The zip's path.</param>
/// <param name="Conversations">How many sessions contributed at least one turn.</param>
/// <param name="Turns">How many turns are in the zip.</param>
/// <param name="MissingBodies">How many bodies are recorded as missing rather than written.</param>
/// <param name="CorruptTurns">How many turns failed verification and were left out.</param>
/// <param name="IncompleteSessions">How many sessions disappeared (deleted by retention or Clear) before all their turns were written.</param>
/// <param name="BodyBytes">The stored plaintext bytes of every body written, before the zip compresses them.</param>
public sealed record ConversationExportResult(
    string Path,
    int Conversations,
    int Turns,
    int MissingBodies,
    int CorruptTurns,
    int IncompleteSessions,
    long BodyBytes);
