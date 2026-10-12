namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>What to do with an imported turn that is present on both sides with different contents.</summary>
public enum ConversationImportConflictChoice
{
    /// <summary>Leave the local turn alone.</summary>
    Skip,

    /// <summary>Store the zip's version and retire the local one.</summary>
    Overwrite,

    /// <summary>Store the zip's version beside the local one.</summary>
    KeepBoth,
}

/// <summary>How a conversation import runs (#165 plan, section 2). The defaults are the router's defaults.</summary>
/// <param name="ImportTime">
/// Whether a session new to the history takes the moment of import (keeping the original order and spacing)
/// instead of its original timestamps.
/// </param>
/// <param name="OnConflict">What a conflicting turn does when <paramref name="KeepLocalCopy"/> is off.</param>
/// <param name="KeepLocalCopy">
/// Whether a conflicting turn keeps the local copy. On by default; with it on <paramref name="OnConflict"/> is not
/// consulted.
/// </param>
public sealed record ConversationImportOptionsInfo(
    bool ImportTime = false,
    ConversationImportConflictChoice OnConflict = ConversationImportConflictChoice.Skip,
    bool KeepLocalCopy = true);

/// <summary>
/// A copy of a zip the router staged for import. The passkey approval for the import is bound to
/// <paramref name="Sha256"/>, which the router computed from its own copy.
/// </summary>
/// <param name="StagedId">Names the copy in the import call.</param>
/// <param name="Sha256">The copy's SHA-256 as 64 lowercase hex digits (<see cref="PasskeyOperations.ImportParameters"/>).</param>
/// <param name="SizeBytes">The copy's size.</param>
/// <param name="ExpiresAtUtc">When the router discards the copy if it has not been imported.</param>
public sealed record StagedImportInfo(string StagedId, string Sha256, long SizeBytes, DateTimeOffset ExpiresAtUtc);

/// <summary>How far a running import has got.</summary>
/// <param name="TurnsProcessed">Turns examined, whether stored, skipped or rejected.</param>
/// <param name="TurnsStored">Turns written to session files.</param>
public sealed record ConversationImportProgressInfo(int TurnsProcessed, int TurnsStored);

/// <summary>What a finished import did.</summary>
/// <param name="ImportId">The id the import is listed and deleted under.</param>
/// <param name="SessionsAdded">Sessions the import created.</param>
/// <param name="SessionsSkipped">Sessions skipped whole because their checksum matched.</param>
/// <param name="TurnsAdded">Turns written to session files.</param>
/// <param name="TurnsAlreadyPresent">Turns that were already in the history with the same content.</param>
/// <param name="TurnsConflictKept">Turns present with different content where the local copy was kept.</param>
/// <param name="TurnsRejected">Turns that failed validation and were left out.</param>
/// <param name="TurnsToGrade">Stored turns that arrived without a score, which the quality rescan will grade.</param>
/// <param name="BodyBytes">The plaintext bytes of the bodies that were read and stored.</param>
/// <param name="TurnsDueForRetention">Turns the next retention pass would delete.</param>
public sealed record ConversationImportResultInfo(
    string ImportId,
    int SessionsAdded,
    int SessionsSkipped,
    int TurnsAdded,
    int TurnsAlreadyPresent,
    int TurnsConflictKept,
    int TurnsRejected,
    int TurnsToGrade,
    long BodyBytes,
    int TurnsDueForRetention);

/// <summary>One message on the import stream: zero or more progress reports, then one result as the final message.</summary>
/// <param name="Progress">A progress report, or <see langword="null"/> for the result message.</param>
/// <param name="Result">The finished import, set only on the final message.</param>
public sealed record ConversationImportEvent(
    ConversationImportProgressInfo? Progress, ConversationImportResultInfo? Result);

/// <summary>One past import, as the imports list shows it.</summary>
/// <param name="ImportId">The import's id.</param>
/// <param name="ImportedAtUtc">When it started.</param>
/// <param name="SourceSha256">SHA-256 of the zip that was imported.</param>
/// <param name="SessionsAdded">Sessions it created.</param>
/// <param name="TurnsAdded">Turns it stored.</param>
/// <param name="TurnsSkipped">Turns that were already present.</param>
/// <param name="TurnsRejected">Turns that failed validation.</param>
public sealed record ConversationImportRowInfo(
    string ImportId,
    DateTimeOffset ImportedAtUtc,
    string SourceSha256,
    int SessionsAdded,
    int TurnsAdded,
    int TurnsSkipped,
    int TurnsRejected);

/// <summary>What deleting an import removed.</summary>
/// <param name="SessionsDeleted">Sessions the import created that were deleted whole.</param>
/// <param name="TurnsRetired">Turns retired instead, because their session holds turns from elsewhere too.</param>
public sealed record ConversationImportDeleteInfo(int SessionsDeleted, int TurnsRetired);

/// <summary>
/// An import a command-line caller has asked the operator to approve (ADR-0020). What the approval view shows is
/// what the router binds the passkey ceremony to: the digest of the staged copy.
/// </summary>
/// <param name="StagedId">The staged copy the import will read.</param>
/// <param name="SourcePath">Where the zip came from on the router machine.</param>
/// <param name="SizeBytes">The staged copy's size.</param>
/// <param name="Sha256">The staged copy's SHA-256, which the approval is bound to.</param>
/// <param name="Options">How the import will run.</param>
public sealed record PendingImportInfo(
    string StagedId, string SourcePath, long SizeBytes, string Sha256, ConversationImportOptionsInfo Options);
