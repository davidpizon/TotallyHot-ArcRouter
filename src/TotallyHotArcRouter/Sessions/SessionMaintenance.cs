using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>What <see cref="SessionMaintenance.DeleteAllAsync"/> did.</summary>
/// <param name="Deletion">What was removed from the store.</param>
/// <param name="QueueDrained">
/// Whether the capture writer had nothing left to write when the deletion ran. When it did not, a turn may still
/// land after the wipe, so the Clear is not final and should be retried.
/// </param>
public sealed record SessionClearResult(SessionDeletionResult Deletion, bool QueueDrained)
{
    /// <summary>
    /// Gets a value indicating whether Clear is complete: the writer was drained, the log was truncated, and the
    /// master key was rotated whenever a session was removed.
    /// </summary>
    public bool IsFinal =>
        QueueDrained && Deletion.WalTruncated && (Deletion.DeletedSessions == 0 || Deletion.MasterKeyRotated);
}

/// <summary>
/// The delete operations on session storage that the rest of the router needs (the Transcription Capture
/// "Clear" action and the retention pass), behind one type that does nothing when nothing was ever captured.
/// Opening <see cref="SessionStore"/> creates the index tables and runs recovery, so a router that has never
/// captured a turn must not open it merely to find it empty; the sessions folder, which exists only once a
/// capture has started, is what says there is something to delete.
/// </summary>
/// <param name="store">The lazily opened store.</param>
/// <param name="database">Names where the session folder lives, beside <c>transcripts.db</c>.</param>
/// <param name="writer">
/// The capture writer, which <see cref="DeleteAllAsync"/> waits on so no queued turn is mid-write while sessions
/// are deleted. Optional: without one nothing is awaited.
/// </param>
/// <param name="drainTimeout">The longest <see cref="DeleteAllAsync"/> waits for the writer; five seconds when omitted.</param>
/// <param name="epoch">
/// The Clear epoch, advanced by <see cref="DeleteAllAsync"/> so turns that began earlier are dropped instead of
/// stored after it. Optional: without one nothing is invalidated.
/// </param>
/// <param name="transcripts">
/// Deletes <c>request_transcripts</c> rows for a removed session. Optional so a store built by hand in a test
/// only removes session files.
/// </param>
/// <param name="memory">Deletes the learned embeddings those rows pointed at. Optional when no memory store exists.</param>
public sealed class SessionMaintenance(
    Lazy<SessionStore> store,
    TranscriptDatabase database,
    SessionCaptureWriter? writer = null,
    TimeSpan? drainTimeout = null,
    CaptureEpoch? epoch = null,
    ITranscriptStore? transcripts = null,
    IMemoryEntryStore? memory = null)
{
    private static readonly SessionDeletionResult NothingToDelete =
        new(0, WalTruncated: true, MasterKeyRotated: false);

    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly string _folder = SessionStore.FolderBeside(database.DatabasePath);

    /// <summary>
    /// Deletes every session, so the Clear action leaves no conversation text on disk. It first advances the
    /// <see cref="CaptureEpoch"/>: a turn that began before this point is dropped wherever it is (still being
    /// relayed, finishing its bodies, or queued for the writer), because its request carries the history being
    /// wiped. It then waits for the writer to go idle, so nothing is mid-write, and deletes. The master key
    /// rotates afterwards, which is what makes the deletion final. A request that arrives after this call is
    /// captured as new data.
    /// </summary>
    /// <returns>
    /// What was deleted and whether the writer drained in time; zero sessions and a final result when nothing was
    /// ever captured.
    /// </returns>
    public async Task<SessionClearResult> DeleteAllAsync()
    {
        epoch?.Advance();

        if (!Directory.Exists(_folder)) return new SessionClearResult(NothingToDelete, QueueDrained: true);

        var drained = writer is null ||
                      await writer.WaitForIdleAsync(drainTimeout ?? DefaultDrainTimeout).ConfigureAwait(false);

        return new SessionClearResult(Open().DeleteAllSessions(), drained);
    }

    /// <summary>
    /// Keeps the newest <paramref name="maxTurns"/> turns by deleting whole oldest sessions (ADR-0019,
    /// "Retention"). The newest session is never deleted, and a session that gained a turn while the pass was
    /// choosing is left alone. Every pass also deletes <c>request_transcripts</c> rows with no
    /// <c>archive_session_id</c> (legacy or unbound metadata), even when no session file is removed.
    /// </summary>
    /// <param name="maxTurns">The most turns to keep (the Sample Size setting).</param>
    /// <returns>What was deleted; zero sessions when the store is within the limit or was never opened.</returns>
    public SessionDeletionResult EnforceRetention(int maxTurns)
    {
        var sessionResult = Directory.Exists(_folder) ? Open().EnforceRetention(maxTurns) : NothingToDelete;
        var purged = PurgeNullArchiveRows();
        return purged == 0
            ? sessionResult
            : sessionResult with { DeletedNullArchiveRows = purged };
    }

    /// <summary>
    /// Opens the store and remembers to drop transcript rows and embeddings when a session is deleted.
    /// The callback is installed once; a later delete on the same store still sees it.
    /// </summary>
    private SessionStore Open()
    {
        var opened = store.Value;
        if (transcripts is not null) opened.SessionsDeleted ??= OnSessionsDeleted;

        return opened;
    }

    /// <summary>Deletes the transcript rows and learned embeddings that belonged to the removed sessions.</summary>
    /// <param name="archiveSessionIds">The sessions whose files were just removed.</param>
    private void OnSessionsDeleted(IReadOnlyCollection<Guid> archiveSessionIds)
    {
        var memoryIds = transcripts!.DeleteByArchiveSessions(archiveSessionIds);
        DeleteMemoryEntries(memoryIds);
    }

    /// <summary>
    /// Removes transcript rows that can never gain a session-file extract, and their linked embeddings.
    /// </summary>
    /// <returns>How many transcript rows were deleted.</returns>
    private int PurgeNullArchiveRows()
    {
        if (transcripts is null) return 0;

        var purge = transcripts.DeleteNullArchiveRows();
        DeleteMemoryEntries(purge.MemoryEntryIds);
        return purge.DeletedRows;
    }

    /// <summary>Deletes the given memory entries when a memory store is wired.</summary>
    /// <param name="memoryIds">Ids collected from deleted transcript rows.</param>
    private void DeleteMemoryEntries(IReadOnlyList<long> memoryIds)
    {
        if (memory is not null && memoryIds.Count > 0)
            memory.DeleteManyAsync(memoryIds).GetAwaiter().GetResult();
    }
}
