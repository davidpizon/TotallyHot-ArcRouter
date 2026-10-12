namespace TotallyHot.ArcRouter.Sessions.Import;

/// <summary>What deleting an import removed.</summary>
/// <param name="SessionsDeleted">Sessions the import created that were deleted whole.</param>
/// <param name="TurnsRetired">Turns retired instead, because their session holds turns from elsewhere too.</param>
public sealed record ConversationImportDeleteResult(int SessionsDeleted, int TurnsRetired);

/// <summary>
/// Lists past conversation imports and deletes one (#165 phase 4). Deleting an import removes what it added and
/// nothing else: a session it created leaves whole, with its transcript rows and learned embeddings, when every
/// live turn in it came from that import; a turn it added to a session that already held other turns is retired
/// instead (the file is append-only), and its transcript row and embedding are removed.
/// </summary>
/// <param name="store">The lazily opened session store.</param>
/// <param name="maintenance">Deletes sessions and the rows and embeddings that belong to them.</param>
/// <param name="database">Names where the session folder lives, so a listing does not create it.</param>
/// <param name="importer">The shared importer; a delete is refused while it runs, because the running import is still adding links to its own record.</param>
public sealed class ConversationImportCatalog(
    Lazy<SessionStore> store,
    SessionMaintenance maintenance,
    Transcripts.TranscriptDatabase database,
    ConversationImporter? importer = null)
{
    private readonly ConversationImporter? _importer = importer;
    private readonly string _folder = SessionStore.FolderBeside(database.DatabasePath);

    /// <summary>Lists past imports, newest first. Opens no store, and creates no folder, on a router that never captured.</summary>
    /// <returns>One row per import.</returns>
    public IReadOnlyList<SessionImportRow> List() =>
        Directory.Exists(_folder) ? store.Value.ListImports() : [];

    /// <summary>Deletes one import's contribution to the history and forgets the import.</summary>
    /// <param name="importId">The import to delete.</param>
    /// <returns>What was removed, or <see langword="null"/> when no such import exists.</returns>
    /// <exception cref="ConversationImportException">With <see cref="ConversationImportFailure.InProgress"/> while an import is running.</exception>
    public ConversationImportDeleteResult? Delete(Guid importId)
    {
        if (_importer?.IsRunning == true)
        {
            throw new ConversationImportException(
                ConversationImportFailure.InProgress, "An import is running. Delete the import after it finishes.");
        }

        if (!Directory.Exists(_folder)) return null;

        var sessions = store.Value;
        if (sessions.ListImports().All(row => row.ImportId != importId)) return null;

        var importTurns = sessions.ListImportTurnIds(importId).ToHashSet();
        var wholeSessions = sessions.ListImportSessionIds(importId)
            .Where(sessionId => sessions.ListTurns(sessionId).All(turn => importTurns.Contains(turn.ArchiveTurnId)))
            .ToList();
        var deleted = wholeSessions.Count == 0 ? 0 : maintenance.DeleteSessions(wholeSessions).DeletedSessions;

        // Deleting a session removed its turns' import links, so what is left are turns in sessions that stay.
        var retired = new List<Guid>();
        foreach (var turnId in sessions.ListImportTurnIds(importId))
        {
            if (sessions.RetireTurn(turnId)) retired.Add(turnId);
        }

        maintenance.DeleteTurnRows(retired);
        sessions.DeleteImportRecords(importId);
        return new ConversationImportDeleteResult(deleted, retired.Count);
    }
}
