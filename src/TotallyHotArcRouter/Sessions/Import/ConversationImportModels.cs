namespace TotallyHot.ArcRouter.Sessions.Import;

/// <summary>How the timestamps of a session new to the history are stored (#165 plan, §2 "Import options").</summary>
public enum ImportTimestampMode
{
    /// <summary>Keep each turn's original <c>created_at_utc</c>. This is the default.</summary>
    Original,

    /// <summary>
    /// Adopt the moment of import while keeping the original order and spacing: every timestamp of a session new
    /// to the history shifts by one offset, so the newest imported turn lands at the import time. The original
    /// time is kept in the turn's metadata as <c>original_created_at_utc</c>.
    /// </summary>
    ImportTime,
}

/// <summary>What to do with a turn that is present on both sides with different contents (#165 plan, §2 "Conflicts").</summary>
public enum ImportConflictMode
{
    /// <summary>Leave the local turn and count it as a conflict kept. This is the default.</summary>
    Skip,

    /// <summary>Store the zip's version and retire the local one, which stays on disk but leaves every read.</summary>
    Overwrite,

    /// <summary>Store the zip's version beside the local one under a new local id, recording the zip's id as its source.</summary>
    KeepBoth,
}

/// <summary>The choices an import runs with. The same ones are offered wherever import is exposed.</summary>
/// <param name="Timestamps">How the timestamps of new sessions are stored.</param>
/// <param name="OnConflict">What a conflicting turn does when <paramref name="KeepLocalCopy"/> is off.</param>
/// <param name="KeepLocalCopy">
/// Whether a conflicting turn keeps the local copy. On by default (David, 2026-09-30); with it on,
/// <paramref name="OnConflict"/> is not consulted and the effective mode is <see cref="ImportConflictMode.Skip"/>.
/// </param>
public sealed record ConversationImportOptions(
    ImportTimestampMode Timestamps = ImportTimestampMode.Original,
    ImportConflictMode OnConflict = ImportConflictMode.Skip,
    bool KeepLocalCopy = true)
{
    /// <summary>Gets the conflict mode that actually applies: <see cref="ImportConflictMode.Skip"/> while the local copy is kept.</summary>
    public ImportConflictMode EffectiveConflictMode => KeepLocalCopy ? ImportConflictMode.Skip : OnConflict;
}

/// <summary>Why <see cref="ConversationImporter"/> refused to import a zip.</summary>
public enum ConversationImportFailure
{
    /// <summary>Another import is already running; the importer allows one at a time.</summary>
    InProgress,

    /// <summary>The file does not exist or is not a readable zip.</summary>
    ArchiveNotFound,

    /// <summary>The zip breaks the format: a bad entry name, a missing or malformed manifest, or a hash that does not match.</summary>
    InvalidArchive,

    /// <summary>The manifest's <c>schema_version</c> is not one this router reads.</summary>
    UnsupportedVersion,

    /// <summary>The volume cannot hold the bodies that would be stored plus the free-space reserve.</summary>
    InsufficientDiskSpace,

    /// <summary>The source to stage is not a zip file the router may read: relative, missing, a directory, inside the data directory, or not a zip.</summary>
    InvalidSource,

    /// <summary>Too many staged copies are waiting to be imported or discarded.</summary>
    TooManyStaged,

    /// <summary>No staged copy has the given id: it was imported, discarded, or lapsed.</summary>
    NotStaged,
}

/// <summary>
/// Raised when an import is refused before it stores anything, or when the archive itself is unusable. A caller
/// maps <see cref="Reason"/> to its own status.
/// </summary>
public sealed class ConversationImportException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="ConversationImportException"/> class.</summary>
    /// <param name="reason">Why the import was refused.</param>
    /// <param name="message">A message that names the rule without echoing archive content.</param>
    public ConversationImportException(ConversationImportFailure reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Gets why the import was refused.</summary>
    public ConversationImportFailure Reason { get; }
}

/// <summary>What an import has done so far.</summary>
/// <param name="TurnsProcessed">Turns examined, whether stored, skipped or rejected.</param>
/// <param name="TurnsStored">Turns written to session files.</param>
public sealed record ConversationImportProgress(int TurnsProcessed, int TurnsStored);

/// <summary>What a finished import did.</summary>
/// <param name="ImportId">The id the import was recorded under, which the imports list and its delete use.</param>
/// <param name="SessionsAdded">Sessions the import created.</param>
/// <param name="SessionsSkipped">Sessions skipped whole because their checksum matched, without reading any body.</param>
/// <param name="TurnsAdded">Turns written to session files.</param>
/// <param name="TurnsAlreadyPresent">Turns that were already in the history with the same content.</param>
/// <param name="TurnsConflictKept">Turns present with different content where the local copy was kept.</param>
/// <param name="TurnsRejected">Turns that failed validation (a bad line, a missing or tampered body) and were left out.</param>
/// <param name="TurnsToGrade">Stored turns that arrived without a score, which the quality rescan will grade like any other unscored turn.</param>
/// <param name="BodyBytes">The plaintext bytes of the exchange bodies that were read and stored.</param>
/// <param name="TurnsDueForRetention">
/// How many turns the next retention pass would delete, counted when the import finished and zero when the caller
/// gave no Sample Size. Under <see cref="ImportTimestampMode.Original"/> an old session can fall outside the newest
/// Sample Size turns and be removed; <see cref="ImportTimestampMode.ImportTime"/> avoids that by making the import
/// the newest history. The count is about the whole store, not only the imported sessions.
/// </param>
public sealed record ConversationImportResult(
    Guid ImportId,
    int SessionsAdded,
    int SessionsSkipped,
    int TurnsAdded,
    int TurnsAlreadyPresent,
    int TurnsConflictKept,
    int TurnsRejected,
    int TurnsToGrade,
    long BodyBytes,
    int TurnsDueForRetention = 0);

/// <summary>
/// A turn an import has just stored, handed to an <see cref="IImportedTurnSink"/> so the learning jobs can see it.
/// </summary>
/// <param name="ArchiveSessionId">The session the turn was stored in.</param>
/// <param name="ArchiveTurnId">The turn's local id.</param>
/// <param name="ClientSessionId">The client's session id, kept as metadata only.</param>
/// <param name="TurnSequence">The turn's position in its session.</param>
/// <param name="CreatedAtUtc">The timestamp it was stored with (shifted under <see cref="ImportTimestampMode.ImportTime"/>).</param>
/// <param name="Metadata">The metadata snapshot, as the zip carried it with the import's own fields added.</param>
/// <param name="NewestUserMessage">The newest user message extracted from the request, or <see langword="null"/> when none could be.</param>
/// <param name="ResponseText">The reply text extracted from the response, or <see langword="null"/> when none could be.</param>
public sealed record ImportedTurn(
    Guid ArchiveSessionId,
    Guid ArchiveTurnId,
    string ClientSessionId,
    int TurnSequence,
    DateTimeOffset CreatedAtUtc,
    System.Text.Json.JsonElement Metadata,
    string? NewestUserMessage,
    string? ResponseText);

/// <summary>
/// Receives each turn an import stores, so a transcript row can be written for it. The importer knows nothing
/// of the transcript database; this seam keeps it that way and lets a test observe what would be written.
/// </summary>
public interface IImportedTurnSink
{
    /// <summary>Called once for each turn after its session file and index row have committed.</summary>
    /// <param name="turn">The stored turn.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>A task that completes when the sink is done with the turn.</returns>
    Task TurnStoredAsync(ImportedTurn turn, CancellationToken cancellationToken);
}
