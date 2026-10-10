using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>One body to store with a turn, or the record that capturing it failed.</summary>
/// <param name="Kind">Which body this is.</param>
/// <param name="Body">The raw bytes, or <see langword="null"/> to record the body as missing (never an empty stand-in).</param>
/// <param name="Spool">
/// A spooled capture to store instead of <paramref name="Body"/>. A spool that is not complete (abandoned or
/// never finished) is recorded as missing. The store does not dispose it; the owner does once the turn commits.
/// </param>
public sealed record SessionBodyInput(SessionBodyKind Kind, byte[]? Body, SessionBodySpool? Spool = null);

/// <summary>One turn to append to a session.</summary>
/// <param name="ArchiveTurnId">The turn's cross-machine identity, minted at capture or import.</param>
/// <param name="CreatedAtUtc">When the turn was captured, or its original time when imported.</param>
/// <param name="Bodies">The turn's bodies, in the order their frames are written.</param>
/// <param name="Origin"><see cref="SessionTurnOrigin.Captured"/> by default.</param>
public sealed record SessionTurnInput(
    Guid ArchiveTurnId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<SessionBodyInput> Bodies,
    string Origin = SessionTurnOrigin.Captured);

/// <summary>What <see cref="SessionStore.RecoverOnStartup"/> found and fixed.</summary>
/// <param name="TruncatedFiles">Session files that held frames beyond their committed extent, now cut back.</param>
/// <param name="DeletedOrphanFiles">Files in the folder that no index row names, now deleted.</param>
/// <param name="DroppedMissingFiles">Index rows whose file was gone, now removed because the session cannot be read.</param>
/// <param name="CorruptFiles">Files shorter than their committed extent, left in place for the operator.</param>
/// <param name="RotationOutcome">What happened to a master-key rotation a crash interrupted.</param>
public sealed record SessionRecoveryResult(
    int TruncatedFiles,
    int DeletedOrphanFiles,
    int DroppedMissingFiles,
    int CorruptFiles,
    SessionRotationRecovery RotationOutcome);

/// <summary>How startup resolved a master-key rotation that did not finish.</summary>
public enum SessionRotationRecovery
{
    /// <summary>No rotation was in progress.</summary>
    None,

    /// <summary>The index was never re-wrapped, so the staged key was discarded.</summary>
    Discarded,

    /// <summary>The index was re-wrapped under the staged key, so it was promoted.</summary>
    Promoted,

    /// <summary>Neither key opens the index; the sessions are unreadable and need the operator.</summary>
    Unresolved
}

/// <summary>What <see cref="SessionStore.DeleteSessions"/> (and retention's null-archive purge) did.</summary>
/// <param name="DeletedSessions">How many sessions were removed from the index.</param>
/// <param name="WalTruncated">Whether the write-ahead log was truncated; <see langword="false"/> means retry.</param>
/// <param name="MasterKeyRotated">Whether the master key was replaced, which is what makes the deletion final.</param>
/// <param name="DeletedNullArchiveRows">
/// How many <c>request_transcripts</c> rows with no <c>archive_session_id</c> retention removed (#165 phase 2
/// follow-up). Zero when that purge was not run or found nothing.
/// </param>
public sealed record SessionDeletionResult(
    int DeletedSessions,
    bool WalTruncated,
    bool MasterKeyRotated,
    int DeletedNullArchiveRows = 0);

/// <summary>
/// The storage layer of ADR-0019: one encrypted file per session in a protected folder, indexed by
/// <see cref="SessionIndex"/>, with each session key wrapped under a master key kept in
/// <see cref="ISessionMasterKeyStore"/>. It owns the commit order, per-session serialization, startup
/// recovery and final deletion; it does not capture traffic, which arrives with the hot-path slice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Commit order.</b> A turn's frames are appended to the file and flushed first, then its index rows
/// commit. A turn exists only once the second step has run, and <see cref="RecoverOnStartup"/> cuts every
/// file back to its committed extent.
/// </para>
/// <para>
/// <b>Protection.</b> The folder sits inside the data directory the bootstrap verified as administrator-only
/// (#184, ADR-0024), so it inherits that protection; this type additionally refuses a folder that is a link,
/// and names files randomly.
/// </para>
/// <para>
/// <b>Concurrency.</b> Appends to one session are serialized; different sessions append in parallel.
/// Resolving a client session id and inserting its first index row share a per-client lock so concurrent
/// first requests cannot mint two archive ids across that commit. Rotation takes the exclusive side of a
/// lock that every append and delete holds shared, so no session key is wrapped under a master key that is
/// about to be replaced.
/// </para>
/// </remarks>
public sealed class SessionStore : ISessionExtractReader
{
    private const string SessionFileExtension = ".thsess";

    private const string FolderName = "sessions";

    private static readonly HashSet<SessionBodyKind> ExtractsKind = [SessionBodyKind.Extracts];

    private readonly SessionIndex _index;
    private readonly ISessionMasterKeyStore _masterKeys;
    private readonly string _folder;
    private readonly ILogger<SessionStore> _logger;
    private readonly ConcurrentDictionary<Guid, object> _sessionGates = new();
    private readonly ConcurrentDictionary<string, object> _clientSessionGates = new();
    private readonly ConcurrentDictionary<string, Guid> _pendingSessionIds = new();
    private readonly ReaderWriterLockSlim _rotationLock = new();
    private readonly Lock _masterKeyGate = new();
    private byte[]? _cachedMasterKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStore"/> class and prepares its folder.
    /// </summary>
    /// <param name="index">The text-free index.</param>
    /// <param name="masterKeys">Custody of the key that wraps session keys.</param>
    /// <param name="folder">The session folder, inside the verified data directory.</param>
    /// <param name="logger">Receives recovery and deletion outcomes; optional so a hand-built store in a test needs no logging.</param>
    /// <exception cref="InvalidOperationException">When <paramref name="folder"/> exists as a link or a file.</exception>
    public SessionStore(
        SessionIndex index,
        ISessionMasterKeyStore masterKeys,
        string folder,
        ILogger<SessionStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(masterKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        _index = index;
        _masterKeys = masterKeys;
        _folder = Path.GetFullPath(folder);
        _logger = logger ?? NullLogger<SessionStore>.Instance;
        PrepareFolder(_folder);
    }

    /// <summary>
    /// Gets where the session folder lives for a given transcript database: a <c>sessions</c> directory beside
    /// it, inside the data directory the bootstrap verified (#184), so it inherits that protection. The
    /// uninstall shred and the service both use this, so they cannot disagree.
    /// </summary>
    /// <param name="transcriptDatabasePath">The resolved path of <c>transcripts.db</c>.</param>
    /// <returns>The absolute session folder path.</returns>
    public static string FolderBeside(string transcriptDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptDatabasePath);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(transcriptDatabasePath)) ?? ".", FolderName);
    }

    /// <summary>
    /// Returns the archive id for a client session, minting a new one when the client has none yet. The
    /// client id only repeats across machines, so locally the newest match is the live session.
    /// </summary>
    /// <param name="clientSessionId">The client's session id.</param>
    /// <returns>
    /// The existing or newly minted archive session id. Minting creates nothing on disk, and the minted id is
    /// remembered until the session's first turn commits, so concurrent first requests of one client session
    /// (a main request and its subagents) land in one session instead of one each.
    /// </returns>
    public Guid ResolveArchiveSessionId(string clientSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSessionId);

        // Same client-session gate as the first InsertSession: a resolver that observed no row must not
        // mint a second pending id across the commit that removes the first.
        lock (_clientSessionGates.GetOrAdd(clientSessionId, static _ => new object()))
        {
            return _index.FindNewestByClientSessionId(clientSessionId)?.ArchiveSessionId
                   ?? _pendingSessionIds.GetOrAdd(clientSessionId, static _ => SessionArchiveIds.NewArchiveSessionId());
        }
    }

    /// <summary>
    /// Appends one turn to a session, creating the session's file and key on first use. The frames are
    /// flushed before the index commits, and a failure part-way cuts the file back so no half turn
    /// survives.
    /// </summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <param name="clientSessionId">The client's session id, recorded when the session is created.</param>
    /// <param name="turn">The turn to append.</param>
    /// <returns>The committed turn's index row.</returns>
    public SessionTurnRow AppendTurn(Guid archiveSessionId, string clientSessionId, SessionTurnInput turn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSessionId);
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.Bodies.Count == 0)
        {
            throw new ArgumentException("A turn needs at least one body.", nameof(turn));
        }

        if (turn.Bodies.Any(b => b.Body is not null && b.Spool is not null))
        {
            throw new ArgumentException("A body is either in memory or spooled, not both.", nameof(turn));
        }

        _rotationLock.EnterReadLock();
        try
        {
            lock (_sessionGates.GetOrAdd(archiveSessionId, static _ => new object()))
            {
                return AppendLocked(archiveSessionId, clientSessionId, turn);
            }
        }
        finally
        {
            _rotationLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Invoked with the archive ids actually removed by a delete, so transcript rows and learned embeddings
    /// can leave with the session. Optional; a store built for tests leaves it unset.
    /// </summary>
    public Action<IReadOnlyCollection<Guid>>? SessionsDeleted { get; set; }

    /// <inheritdoc />
    public SessionExtracts? TryReadExtracts(Guid archiveSessionId, Guid archiveTurnId) =>
        TryReadExtracts(archiveSessionId, [archiveTurnId]).GetValueOrDefault(archiveTurnId);

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, SessionExtracts> TryReadExtracts(
        Guid archiveSessionId, IReadOnlyCollection<Guid> archiveTurnIds)
    {
        ArgumentNullException.ThrowIfNull(archiveTurnIds);
        var results = new Dictionary<Guid, SessionExtracts>();
        if (archiveTurnIds.Count == 0) return results;

        var bodies = TryReadBodies(archiveSessionId, ExtractsKind, archiveTurnIds);
        try
        {
            foreach (var ((turnId, _), plaintext) in bodies)
            {
                if (plaintext.Length > 0) results[turnId] = SessionExtracts.Parse(plaintext);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not read extracts for session {ArchiveSessionId}.", archiveSessionId);
        }

        return results;
    }

    /// <summary>
    /// Decrypts the frames of the wanted kinds for the wanted turns of one session in a single pass over its
    /// file, skipping every other frame without decrypting it. Export uses this to read the small
    /// <see cref="SessionBodyKind.TurnMetadata"/> frames for its harness, provider and model filters without
    /// touching a body. A session that cannot be read yields what was read before the failure, and the failure
    /// is logged.
    /// </summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <param name="kinds">The body kinds wanted.</param>
    /// <param name="archiveTurnIds">The turns whose frames are wanted.</param>
    /// <returns>The decompressed plaintext by turn and kind; a frame that is absent, missing or unreadable has no entry.</returns>
    public IReadOnlyDictionary<(Guid ArchiveTurnId, SessionBodyKind Kind), byte[]> TryReadBodies(
        Guid archiveSessionId, IReadOnlySet<SessionBodyKind> kinds, IReadOnlyCollection<Guid> archiveTurnIds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(archiveTurnIds);
        var results = new Dictionary<(Guid ArchiveTurnId, SessionBodyKind Kind), byte[]>();
        if (archiveTurnIds.Count == 0) return results;

        _rotationLock.EnterReadLock();
        try
        {
            lock (_sessionGates.GetOrAdd(archiveSessionId, static _ => new object()))
            {
                var row = _index.TryGetSession(archiveSessionId);
                if (row is null) return results;

                var sessionKey = UnwrapSessionKey(row.WrappedKey);
                try
                {
                    using var file = SessionFile.Open(PathOf(row), sessionKey, archiveSessionId);
                    if ((long)file.FrameCount < row.CommittedFrames || file.Length < row.CommittedLength)
                    {
                        return results;
                    }

                    return file.TryReadBodies(kinds, archiveTurnIds.ToHashSet(), row.CommittedFrames);
                }
                catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException)
                {
                    _logger.LogWarning(ex, "Could not read bodies for session {ArchiveSessionId}.", archiveSessionId);
                    return results;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sessionKey);
                }
            }
        }
        finally
        {
            _rotationLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Opens one turn for streaming, for export. It holds the master-key rotation lock (shared) and the
    /// session's gate until the reader is disposed, so a rotation or a delete cannot pull the file away
    /// mid-turn, but only for that one turn: appends to the session wait for it, other sessions and later
    /// turns do not. The caller reads and disposes the reader on the thread that opened it, because the locks
    /// are owned by a thread.
    /// </summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <param name="archiveTurnId">The turn's archive id.</param>
    /// <returns>A reader for the turn, or <see langword="null"/> when the session or turn is not in the index (for instance, it was deleted).</returns>
    /// <exception cref="InvalidDataException">When the session file is shorter than the turn's committed extent.</exception>
    /// <exception cref="IOException">When the session file is gone or cannot be opened.</exception>
    public SessionTurnReader? OpenTurn(Guid archiveSessionId, Guid archiveTurnId)
    {
        var gate = _sessionGates.GetOrAdd(archiveSessionId, static _ => new object());
        _rotationLock.EnterReadLock();
        var gateTaken = false;
        var handedOff = false;
        try
        {
            Monitor.Enter(gate, ref gateTaken);
            var turn = _index.TryGetTurn(archiveTurnId);
            var row = turn is null ? null : _index.TryGetSession(archiveSessionId);
            if (turn is null || row is null || turn.ArchiveSessionId != archiveSessionId) return null;

            var startOffset = SessionFile.FirstFrameOffset;
            if (turn.TurnSequence > 0)
            {
                startOffset = _index.TryGetTurnBySequence(archiveSessionId, turn.TurnSequence - 1)?.EndOffset
                              ?? throw new InvalidDataException(
                                  $"Session {archiveSessionId} has no turn before sequence {turn.TurnSequence}.");
            }

            var sessionKey = UnwrapSessionKey(row.WrappedKey);
            SessionFile? file = null;
            try
            {
                file = SessionFile.OpenReadOnly(PathOf(row), sessionKey, archiveSessionId);
                if (file.Length < turn.EndOffset)
                {
                    throw new InvalidDataException(
                        $"Session {archiveSessionId} is shorter than the turn being read.");
                }

                var reader = new SessionTurnReader(turn, file, startOffset, () =>
                {
                    Monitor.Exit(gate);
                    _rotationLock.ExitReadLock();
                });
                handedOff = true;
                return reader;
            }
            catch
            {
                file?.Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
        }
        finally
        {
            if (!handedOff)
            {
                if (gateTaken) Monitor.Exit(gate);
                _rotationLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Computes the body hashes and the checksum for a session captured before hashes existed, by stream
    /// decrypting it once under the session's gate. A session that already has them is left alone. A failure
    /// is logged and leaves the session unhashed, so an export can say so instead of failing.
    /// </summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns><see langword="true"/> when the session has hashes now; <see langword="false"/> when it is unknown or could not be read.</returns>
    public bool BackfillBodyHashes(Guid archiveSessionId)
    {
        _rotationLock.EnterReadLock();
        try
        {
            lock (_sessionGates.GetOrAdd(archiveSessionId, static _ => new object()))
            {
                var row = _index.TryGetSession(archiveSessionId);
                if (row is null) return false;
                if (_index.HasBodyHashes(archiveSessionId)) return true;

                var sessionKey = UnwrapSessionKey(row.WrappedKey);
                try
                {
                    using var file = SessionFile.OpenReadOnly(PathOf(row), sessionKey, archiveSessionId);
                    if (file.Length < row.CommittedLength)
                    {
                        _logger.LogWarning(
                            "Session {ArchiveSessionId} is shorter than its committed extent and cannot be hashed.", archiveSessionId);
                        return false;
                    }

                    var hashes = new List<SessionBodyHashRow>();
                    var startOffset = SessionFile.FirstFrameOffset;
                    foreach (var turn in _index.ListTurns(archiveSessionId))
                    {
                        foreach (var frame in file.ReadTurn(startOffset, turn.FirstFrameOrdinal, turn.FrameCount))
                        {
                            using (frame) hashes.Add(HashFrame(frame));
                        }

                        startOffset = turn.EndOffset;
                    }

                    _index.ReplaceBodyHashes(archiveSessionId, hashes);
                    return true;
                }
                catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException)
                {
                    _logger.LogWarning(ex, "Could not compute body hashes for session {ArchiveSessionId}.", archiveSessionId);
                    return false;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sessionKey);
                }
            }
        }
        finally
        {
            _rotationLock.ExitReadLock();
        }
    }

    /// <summary>Lists the hashes of every body of a session, in turn order then kind order.</summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns>The hash rows; empty for an unknown session or one without hashes.</returns>
    public IReadOnlyList<SessionBodyHashRow> ListBodyHashes(Guid archiveSessionId) =>
        _index.ListBodiesForSession(archiveSessionId);

    /// <summary>Reports whether every turn of a session has body hashes.</summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns><see langword="false"/> for a session that still needs <see cref="BackfillBodyHashes"/>.</returns>
    public bool HasBodyHashes(Guid archiveSessionId) => _index.HasBodyHashes(archiveSessionId);

    /// <summary>Reads a session's checksum over its exchange bodies.</summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns>The 32-byte checksum, or <see langword="null"/> when the session is unknown or unhashed.</returns>
    public byte[]? GetSessionSha256(Guid archiveSessionId) => _index.GetSessionSha256(archiveSessionId);

    /// <summary>Gets the absolute path of the folder that holds the session files.</summary>
    public string Folder => _folder;

    /// <summary>Hashes one frame's stored plaintext for a backfill.</summary>
    /// <param name="frame">The frame, whose body is read to the end.</param>
    /// <returns>The frame's hash row; a missing marker has no hash.</returns>
    private static SessionBodyHashRow HashFrame(SessionFrameStream frame)
    {
        if (frame.Body is null)
        {
            return new SessionBodyHashRow(frame.ArchiveTurnId, frame.Kind, Sha256: null, Length: 0, Missing: true, Obscured: null);
        }

        using var hashing = new HashingStream(Stream.Null, leaveOpen: true);
        frame.Body.CopyTo(hashing);
        return new SessionBodyHashRow(
            frame.ArchiveTurnId, frame.Kind, hashing.FinishHash(), hashing.BytesWritten, Missing: false, Obscured: null);
    }

    /// <summary>Lists a session's committed turns in order.</summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns>The turns, or an empty list for an unknown session.</returns>
    public IReadOnlyList<SessionTurnRow> ListTurns(Guid archiveSessionId) => _index.ListTurns(archiveSessionId);

    /// <summary>
    /// Decrypts a session's committed bodies, for tests and for the export that arrives in a later phase.
    /// </summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns>The committed frames in file order.</returns>
    /// <exception cref="KeyNotFoundException">When the session is not in the index.</exception>
    public IReadOnlyList<SessionBodyFrame> ReadBodies(Guid archiveSessionId)
    {
        _rotationLock.EnterReadLock();
        try
        {
            lock (_sessionGates.GetOrAdd(archiveSessionId, static _ => new object()))
            {
                var row = _index.TryGetSession(archiveSessionId)
                          ?? throw new KeyNotFoundException($"Unknown session {archiveSessionId}.");
                var sessionKey = UnwrapSessionKey(row.WrappedKey);
                try
                {
                    using var file = SessionFile.Open(PathOf(row), sessionKey, archiveSessionId);
                    // Open truncates a torn trailing frame; a short committed prefix must not look like a
                    // successful partial read the way Take would.
                    if ((long)file.FrameCount < row.CommittedFrames || file.Length < row.CommittedLength)
                    {
                        throw new InvalidDataException(
                            $"Session {archiveSessionId} is shorter than its committed extent and cannot be read.");
                    }

                    return file.ReadAllBodies().Take(checked((int)row.CommittedFrames)).ToList();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sessionKey);
                }
            }
        }
        finally
        {
            _rotationLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deletes sessions for good (ADR-0019, "Deletion"): their wrapped keys go under <c>secure_delete</c>,
    /// the log is truncated, the files are deleted, and then the master key rotates so no leftover copy of a
    /// deleted key can be unwrapped.
    /// </summary>
    /// <param name="archiveSessionIds">The sessions to delete; unknown ids are ignored.</param>
    /// <returns>What was deleted, and whether the log was truncated and the key rotated.</returns>
    public SessionDeletionResult DeleteSessions(IReadOnlyCollection<Guid> archiveSessionIds)
    {
        ArgumentNullException.ThrowIfNull(archiveSessionIds);

        return DeleteSessionsCore(archiveSessionIds, unchangedSince: null);
    }

    /// <summary>
    /// Deletes sessions, optionally only those still exactly as a caller saw them. With
    /// <paramref name="unchangedSince"/> each session is looked at again under the same per-session gate an
    /// append holds, and one that gained a turn since the caller's snapshot is skipped, so a decision made from a
    /// stale list never deletes a conversation that has just been written to.
    /// </summary>
    /// <param name="archiveSessionIds">The sessions to delete; unknown ids are ignored.</param>
    /// <param name="unchangedSince">The rows the caller based its choice on, keyed by session id; <see langword="null"/> deletes unconditionally.</param>
    /// <returns>What was deleted, and whether the log was truncated and the key rotated.</returns>
    private SessionDeletionResult DeleteSessionsCore(
        IReadOnlyCollection<Guid> archiveSessionIds, IReadOnlyDictionary<Guid, SessionFileRow>? unchangedSince)
    {
        var deletedFiles = new List<string>();
        var deletedIds = new List<Guid>();
        _rotationLock.EnterReadLock();
        try
        {
            var ids = archiveSessionIds.Distinct().ToList();

            // Mark before the first row removal: if rotation fails (or the process dies) after the rows
            // are gone, retrying with the same ids must still finish the key retirement. A conditional delete
            // cannot know up front whether anything will qualify, so it marks just before its first removal.
            var marked = false;
            if (unchangedSince is null && ids.Exists(id => _index.TryGetSession(id) is not null))
            {
                _masterKeys.RequireRotation();
                marked = true;
            }

            foreach (var id in ids)
            {
                lock (_sessionGates.GetOrAdd(id, static _ => new object()))
                {
                    if (unchangedSince is not null)
                    {
                        var current = _index.TryGetSession(id);
                        if (current is null || !IsUnchanged(current, unchangedSince[id])) continue;

                        if (!marked)
                        {
                            _masterKeys.RequireRotation();
                            marked = true;
                        }
                    }

                    if (_index.DeleteSession(id) is { } fileName)
                    {
                        deletedFiles.Add(fileName);
                        deletedIds.Add(id);
                    }
                }

                // The gate stays: removing it would let an append that already holds it and one that creates
                // a replacement run together. One small object per session is the whole cost.
            }
        }
        finally
        {
            _rotationLock.ExitReadLock();
        }

        // Runs before the key rotation below: the index rows are already gone, so if the rotation throws no retry
        // would find these sessions again, and their transcript rows and embeddings would be stranded.
        if (deletedIds.Count > 0 && SessionsDeleted is { } onDeleted)
        {
            // The files are already gone, so a failing cleanup must not turn a completed delete into an error.
            try
            {
                onDeleted(deletedIds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Transcript rows for {Count} deleted sessions could not be removed.",
                    deletedIds.Count);
            }
        }


        SessionDeletionResult result;
        if (deletedFiles.Count == 0 && !_masterKeys.IsRotationRequired())
        {
            result = new SessionDeletionResult(0, WalTruncated: true, MasterKeyRotated: false);
        }
        else
        {
            var walTruncated = deletedFiles.Count == 0 || _index.TruncateWal();
            foreach (var fileName in deletedFiles) DeleteFile(Path.Combine(_folder, fileName));

            // Database remnants could still hold an old copy of a wrapped key; the rotation is what makes that
            // copy useless, so it runs after every pass that deleted anything (and after a prior pass that
            // recorded RequireRotation but could not finish).
            RotateMasterKey();
            walTruncated = _index.TruncateWal() && walTruncated;

            _logger.LogInformation("Deleted {Count} sessions; write-ahead log truncated: {WalTruncated}.",
                deletedFiles.Count, walTruncated);
            result = new SessionDeletionResult(deletedFiles.Count, walTruncated, MasterKeyRotated: true);
        }

        return result;
    }

    /// <summary>Lists every session in the index, oldest first.</summary>
    /// <returns>All session rows.</returns>
    public IReadOnlyList<SessionFileRow> ListSessions() => _index.ListSessions();

    /// <summary>
    /// Deletes every session, which is what the Transcription Capture "Clear" action means: no conversation
    /// text is left, and the master key rotates so no copy of a deleted session key can be unwrapped.
    /// </summary>
    /// <returns>What was deleted, and whether the deletion is final.</returns>
    public SessionDeletionResult DeleteAllSessions() =>
        DeleteSessions(_index.ListSessions().Select(row => row.ArchiveSessionId).ToList());

    /// <summary>
    /// Applies ADR-0019's retention rule: keeps the newest <paramref name="maxTurns"/> turns by deleting whole
    /// sessions, oldest first (by last turn). The newest session is never deleted, even when it alone exceeds
    /// the limit, because it may be the one still being written and deleting it would empty the store.
    /// </summary>
    /// <param name="maxTurns">
    /// The most turns to keep (the Sample Size setting). A value of zero or less is not a retention policy but a
    /// misconfiguration (the setting's own minimum is 500), so it deletes nothing, like the transcript retention
    /// sweep treats a non-positive age.
    /// </param>
    /// <returns>What was deleted; zero sessions when the store is within the limit.</returns>
    public SessionDeletionResult EnforceRetention(int maxTurns) => DeleteUnchanged(SelectExpiredSessions(maxTurns));

    /// <summary>
    /// Picks the sessions retention would delete: the oldest, by last turn, until what remains fits in
    /// <paramref name="maxTurns"/>. This is a snapshot; <see cref="DeleteUnchanged"/> checks each one again
    /// before deleting it.
    /// </summary>
    /// <param name="maxTurns">The most turns to keep; zero or less selects nothing.</param>
    /// <returns>The rows the choice was based on, oldest first.</returns>
    internal IReadOnlyList<SessionFileRow> SelectExpiredSessions(int maxTurns)
    {
        if (maxTurns <= 0) return [];

        var sessions = _index.ListSessions()
            .OrderBy(row => row.LastTurnAtUtc ?? row.CreatedAtUtc)
            .ThenBy(row => row.CreatedAtUtc)
            .ToList();
        var total = sessions.Sum(row => (long)row.TurnCount);
        var expired = new List<SessionFileRow>();

        // Stops one short of the end so the newest session survives.
        for (var i = 0; i < sessions.Count - 1 && total > maxTurns; i++)
        {
            expired.Add(sessions[i]);
            total -= sessions[i].TurnCount;
        }

        return expired;
    }

    /// <summary>
    /// Deletes the given sessions unless one has gained a turn since it was selected. A session appended to after
    /// the snapshot is no longer an old one, and deleting it would delete the turn just written.
    /// </summary>
    /// <param name="candidates">The rows <see cref="SelectExpiredSessions"/> returned.</param>
    /// <returns>What was deleted; zero sessions when none was still unchanged.</returns>
    internal SessionDeletionResult DeleteUnchanged(IReadOnlyList<SessionFileRow> candidates)
    {
        if (candidates.Count == 0) return new SessionDeletionResult(0, WalTruncated: true, MasterKeyRotated: false);

        return DeleteSessionsCore(
            candidates.Select(row => row.ArchiveSessionId).ToList(),
            candidates.ToDictionary(row => row.ArchiveSessionId));
    }

    /// <summary>Reports whether a session still has the turn count and last turn it had when it was selected.</summary>
    /// <param name="current">The session as it is now.</param>
    /// <param name="snapshot">The session as it was when selected.</param>
    /// <returns><see langword="true"/> when nothing has been appended since.</returns>
    private static bool IsUnchanged(SessionFileRow current, SessionFileRow snapshot) =>
        current.TurnCount == snapshot.TurnCount && current.LastTurnAtUtc == snapshot.LastTurnAtUtc;

    /// <summary>
    /// Replaces the master key: stages a new one, re-wraps every session key in a single transaction, then
    /// promotes it and destroys the old one. A crash at any point is resolved by
    /// <see cref="RecoverOnStartup"/>.
    /// </summary>
    public void RotateMasterKey()
    {
        _rotationLock.EnterWriteLock();
        try
        {
            RotateMasterKeyUnlocked();
        }
        finally
        {
            _rotationLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Brings the folder and index back to a consistent state after a crash: resolves an interrupted
    /// rotation, finishes a deletion that still needs key retirement, cuts every file back to its
    /// committed extent, drops index rows whose file is gone, and deletes files and spools that no
    /// committed index row names (ADR-0019, "Commit order and recovery"). Run once at startup, before any
    /// append.
    /// </summary>
    /// <returns>What was found and fixed.</returns>
    public SessionRecoveryResult RecoverOnStartup()
    {
        _rotationLock.EnterWriteLock();
        try
        {
            ClearMasterKeyCache();
            var rotation = RecoverRotation();
            ClearMasterKeyCache();

            // Deletion finalization can leave RequireRotation set with no staged next key (crash before
            // StageNext). Finish that here; mid-rotation discard/promote above does not clear it.
            if (_masterKeys.IsRotationRequired())
            {
                RotateMasterKeyUnlocked();
                if (rotation is SessionRotationRecovery.None or SessionRotationRecovery.Discarded)
                {
                    rotation = SessionRotationRecovery.Promoted;
                }
            }

            var truncated = 0;
            var corrupt = 0;
            var dropped = 0;

            foreach (var row in _index.ListSessions())
            {
                var path = PathOf(row);
                if (!File.Exists(path))
                {
                    _index.DeleteSession(row.ArchiveSessionId);
                    dropped++;
                    _logger.LogWarning("Session {ArchiveSessionId} had no file and was removed from the index.", row.ArchiveSessionId);
                    continue;
                }

                var outcome = CutBackToCommitted(row, path);
                if (outcome == FileRecovery.Truncated) truncated++;
                else if (outcome == FileRecovery.Corrupt) corrupt++;
            }

            var orphans = DeleteOrphanFiles();
            return new SessionRecoveryResult(truncated, orphans, dropped, corrupt, rotation);
        }
        finally
        {
            _rotationLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Runs a master-key rotation while the caller already holds the exclusive rotation lock.
    /// </summary>
    private void RotateMasterKeyUnlocked()
    {
        var oldKey = _masterKeys.TryGetCurrent();
        if (oldKey is null)
        {
            // Nothing to retire; clear a stale deletion marker so startup does not loop.
            _masterKeys.ClearRotationRequired();
            return;
        }

        var newKey = SessionKeyMaterial.CreateMasterKey();
        try
        {
            _masterKeys.StageNext(newKey);
            _index.RewrapAllKeys(wrapped =>
            {
                var sessionKey = SessionKeyMaterial.UnwrapSessionKey(oldKey, wrapped);
                try
                {
                    return SessionKeyMaterial.WrapSessionKey(newKey, sessionKey);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sessionKey);
                }
            });
            _masterKeys.PromoteNext();
            _masterKeys.ClearRotationRequired();
        }
        catch (Exception ex)
        {
            // The index may already be wrapped under the staged key while the secret store still names the
            // old one. Left alone, every later append would fail until a restart, so resolve it now.
            _logger.LogError(ex, "Master-key rotation failed; resolving it before reporting the failure.");
            try
            {
                if (RecoverRotation() is SessionRotationRecovery.Promoted)
                {
                    _masterKeys.ClearRotationRequired();
                }
            }
            catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or CryptographicException)
            {
                _logger.LogError(recovery, "The interrupted master-key rotation could not be resolved.");
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKey);
            CryptographicOperations.ZeroMemory(newKey);
            ClearMasterKeyCache();
        }
    }

    private SessionTurnRow AppendLocked(Guid archiveSessionId, string clientSessionId, SessionTurnInput turn)
    {
        var masterKey = GetMasterKey(create: true)!;
        byte[]? sessionKey = null;
        SessionFile? file = null;
        var row = _index.TryGetSession(archiveSessionId);
        try
        {
            if (row is null)
            {
                sessionKey = SessionKeyMaterial.CreateSessionKey();
                var fileName = $"{Guid.NewGuid():N}{SessionFileExtension}";
                file = SessionFile.Create(Path.Combine(_folder, fileName), archiveSessionId, sessionKey);
                row = new SessionFileRow(
                    archiveSessionId, clientSessionId, fileName,
                    SessionKeyMaterial.WrapSessionKey(masterKey, sessionKey),
                    file.Length, (long)file.FrameCount, TurnCount: 0, DateTimeOffset.UtcNow, LastTurnAtUtc: null);

                // Same client-session gate as ResolveArchiveSessionId: InsertSession and pending removal are
                // one critical section with the lookup that mints a pending id.
                lock (_clientSessionGates.GetOrAdd(clientSessionId, static _ => new object()))
                {
                    // The wrapped key commits before any frame is written, so a frame can never exist whose key
                    // was lost. A crash after this leaves an empty session that recovery keeps.
                    _index.InsertSession(row);
                    _pendingSessionIds.TryRemove(clientSessionId, out _);
                }
            }
            else
            {
                sessionKey = SessionKeyMaterial.UnwrapSessionKey(masterKey, row.WrappedKey);
                file = SessionFile.Open(PathOf(row), sessionKey, archiveSessionId);
                if ((long)file.FrameCount < row.CommittedFrames || file.Length < row.CommittedLength)
                {
                    throw new InvalidDataException(
                        $"Session {archiveSessionId} is shorter than its committed extent and cannot take another turn.");
                }

                // Frames a failed earlier append left behind are cut away before this turn writes its own.
                if ((long)file.FrameCount != row.CommittedFrames || file.Length != row.CommittedLength)
                {
                    file.TruncateTo(row.CommittedLength, (ulong)row.CommittedFrames);
                }
            }

            var firstOrdinal = (long)file.FrameCount;
            var sequence = (uint)row.TurnCount;
            var hashes = new List<SessionBodyHashRow>(turn.Bodies.Count);
            try
            {
                foreach (var body in turn.Bodies)
                {
                    if (body.Spool is { IsComplete: true } spool) hashes.Add(file.AppendBodyFromSpool(sequence, body.Kind, spool, turn.ArchiveTurnId));
                    else if (body.Body is null) hashes.Add(file.AppendMissingBody(sequence, body.Kind, turn.ArchiveTurnId));
                    else hashes.Add(file.AppendBody(sequence, body.Kind, body.Body, turn.ArchiveTurnId));
                }
            }
            catch
            {
                RollBackQuietly(file, row);
                throw;
            }

            var turnRow = new SessionTurnRow(
                turn.ArchiveTurnId, archiveSessionId, row.TurnCount, firstOrdinal,
                turn.Bodies.Count, file.Length, turn.CreatedAtUtc, turn.Origin);
            try
            {
                _index.CommitTurn(turnRow, hashes);
            }
            catch
            {
                RollBackQuietly(file, row);
                throw;
            }

            return turnRow;
        }
        finally
        {
            file?.Dispose();
            if (sessionKey is not null) CryptographicOperations.ZeroMemory(sessionKey);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private SessionRotationRecovery RecoverRotation()
    {
        var staged = _masterKeys.TryGetNext();
        if (staged is null) return SessionRotationRecovery.None;

        var current = _masterKeys.TryGetCurrent();
        try
        {
            var sample = _index.ListSessions().FirstOrDefault();
            if (sample is null || (current is not null && Opens(current, sample.WrappedKey)))
            {
                // Nothing was re-wrapped (or there is nothing to re-wrap): the old key is still the right one.
                _masterKeys.DiscardNext();
                return SessionRotationRecovery.Discarded;
            }

            if (Opens(staged, sample.WrappedKey))
            {
                _masterKeys.PromoteNext();
                return SessionRotationRecovery.Promoted;
            }

            _logger.LogError("A master-key rotation was interrupted and neither key opens the session index.");
            return SessionRotationRecovery.Unresolved;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(staged);
            if (current is not null) CryptographicOperations.ZeroMemory(current);
        }
    }

    /// <summary>
    /// Cuts a file back to its committed extent after a failed append. A failure here is logged and
    /// swallowed so it cannot replace the exception that caused the rollback; startup recovery repeats the cut.
    /// </summary>
    private void RollBackQuietly(SessionFile file, SessionFileRow row)
    {
        try
        {
            file.TruncateTo(row.CommittedLength, (ulong)row.CommittedFrames);
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or ObjectDisposedException)
        {
            _logger.LogError(ex, "Could not cut session {ArchiveSessionId} back after a failed append.", row.ArchiveSessionId);
        }
    }

    /// <summary>
    /// Returns a copy of the master key, reading the secret store once and caching the result until a
    /// rotation or recovery clears it, so an append does not decrypt the secret store every time.
    /// </summary>
    /// <param name="create">Whether to create the key when none exists yet.</param>
    /// <returns>A key the caller must zero, or <see langword="null"/> when none exists and <paramref name="create"/> is false.</returns>
    private byte[]? GetMasterKey(bool create)
    {
        lock (_masterKeyGate)
        {
            _cachedMasterKey ??= create ? _masterKeys.GetOrCreateCurrent() : _masterKeys.TryGetCurrent();
            return (byte[]?)_cachedMasterKey?.Clone();
        }
    }

    private void ClearMasterKeyCache()
    {
        lock (_masterKeyGate)
        {
            if (_cachedMasterKey is not null) CryptographicOperations.ZeroMemory(_cachedMasterKey);
            _cachedMasterKey = null;
        }
    }

    private FileRecovery CutBackToCommitted(SessionFileRow row, string path)
    {
        var masterKey = GetMasterKey(create: false);
        if (masterKey is null)
        {
            _logger.LogError("Session {ArchiveSessionId} has an index row but the master key is gone.", row.ArchiveSessionId);
            return FileRecovery.Corrupt;
        }

        byte[]? sessionKey = null;
        try
        {
            sessionKey = SessionKeyMaterial.UnwrapSessionKey(masterKey, row.WrappedKey);
            using var file = SessionFile.Open(path, sessionKey, row.ArchiveSessionId);
            if ((long)file.FrameCount < row.CommittedFrames || file.Length < row.CommittedLength)
            {
                _logger.LogError("Session {ArchiveSessionId} is shorter than its committed extent.", row.ArchiveSessionId);
                return FileRecovery.Corrupt;
            }

            if ((long)file.FrameCount == row.CommittedFrames && file.Length == row.CommittedLength)
            {
                return FileRecovery.Clean;
            }

            file.TruncateTo(row.CommittedLength, (ulong)row.CommittedFrames);
            return FileRecovery.Truncated;
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException)
        {
            _logger.LogError(ex, "Session {ArchiveSessionId} could not be opened during recovery.", row.ArchiveSessionId);
            return FileRecovery.Corrupt;
        }
        finally
        {
            if (sessionKey is not null) CryptographicOperations.ZeroMemory(sessionKey);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private int DeleteOrphanFiles()
    {
        var known = _index.ListSessions().Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_folder))
        {
            // Only the store's own artifacts: a stray file that is none of these is not ours to delete.
            if (!IsOwnArtifact(path) || known.Contains(Path.GetFileName(path))) continue;

            if (DeleteFile(path)) deleted++;
        }

        if (deleted > 0) _logger.LogInformation("Deleted {Count} session files that no index row names.", deleted);
        return deleted;
    }

    private static bool IsOwnArtifact(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is SessionFileExtension or ".spool" or ".tmp";

    private bool DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not delete the session file {Path}.", path);
            return false;
        }
    }

    private byte[] UnwrapSessionKey(byte[] wrapped)
    {
        var masterKey = GetMasterKey(create: false)
                        ?? throw new InvalidOperationException("The session master key is not available.");
        try
        {
            return SessionKeyMaterial.UnwrapSessionKey(masterKey, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static bool Opens(byte[] masterKey, byte[] wrapped)
    {
        try
        {
            CryptographicOperations.ZeroMemory(SessionKeyMaterial.UnwrapSessionKey(masterKey, wrapped));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private string PathOf(SessionFileRow row) => Path.Combine(_folder, row.FileName);

    private static void PrepareFolder(string folder)
    {
        if (File.Exists(folder))
        {
            throw new InvalidOperationException($"The session folder path {folder} is a file.");
        }

        var directory = new DirectoryInfo(folder);
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"The session folder {folder} is a link, which is not followed.");
        }

        Directory.CreateDirectory(folder);
    }

    private enum FileRecovery
    {
        Clean,
        Truncated,
        Corrupt
    }
}
