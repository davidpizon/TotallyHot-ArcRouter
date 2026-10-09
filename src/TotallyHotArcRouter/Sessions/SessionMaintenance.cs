using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// The delete operations on session storage that the rest of the router needs (the Transcription Capture
/// "Clear" action and the retention pass), behind one type that does nothing when nothing was ever captured.
/// Opening <see cref="SessionStore"/> creates the index tables and runs recovery, so a router that has never
/// captured a turn must not open it merely to find it empty; the sessions folder, which exists only once a
/// capture has started, is what says there is something to delete.
/// </summary>
/// <param name="store">The lazily opened store.</param>
/// <param name="database">Names where the session folder lives, beside <c>transcripts.db</c>.</param>
public sealed class SessionMaintenance(Lazy<SessionStore> store, TranscriptDatabase database)
{
    private static readonly SessionDeletionResult NothingToDelete =
        new(0, WalTruncated: true, MasterKeyRotated: false);

    private readonly string _folder = SessionStore.FolderBeside(database.DatabasePath);

    /// <summary>
    /// Deletes every session, so the Clear action leaves no conversation text on disk. The master key rotates
    /// afterwards, which is what makes the deletion final.
    /// </summary>
    /// <returns>What was deleted; zero sessions and a final result when nothing was ever captured.</returns>
    public SessionDeletionResult DeleteAll() =>
        Directory.Exists(_folder) ? store.Value.DeleteAllSessions() : NothingToDelete;

    /// <summary>
    /// Keeps the newest <paramref name="maxTurns"/> turns by deleting whole oldest sessions (ADR-0019,
    /// "Retention"). The newest session is never deleted.
    /// </summary>
    /// <param name="maxTurns">The most turns to keep (the Sample Size setting).</param>
    /// <returns>What was deleted; zero sessions when the store is within the limit or was never opened.</returns>
    public SessionDeletionResult EnforceRetention(int maxTurns) =>
        Directory.Exists(_folder) ? store.Value.EnforceRetention(maxTurns) : NothingToDelete;
}
