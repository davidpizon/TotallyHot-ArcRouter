using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Sessions.Export;

/// <summary>
/// Writes the conversations in a <see cref="SessionStore"/> to one zip file (#165 phase 3). It is a library:
/// the gRPC service and the CLI call it and do not format the zip themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout (schema version 1).</b> <c>manifest.json</c>, <c>turns.jsonl</c>, and one file per body at
/// <c>conversations/{archive_session_id}/turns/{turn_sequence:D4}.{request|response|provider-request|provider-response}.json</c>.
/// The body files hold the stored plaintext (after secrets were obscured at capture) byte for byte, so their
/// SHA-256 is the same in <c>turns.jsonl</c>, in the manifest and in the index. <c>turns.jsonl</c> has one
/// object per turn in <c>(client session id, archive session id, turn sequence)</c> order, with the capture-time
/// metadata snapshot nested under <c>metadata</c>.
/// </para>
/// <para>
/// <b>Complete or absent.</b> The zip is written to <c>{path}.partial</c> and renamed once finished. A cancelled
/// or failed export deletes the partial file, so a zip at the destination is always a whole one.
/// </para>
/// <para>
/// <b>Streaming and locking.</b> Each turn is read through <see cref="SessionStore.OpenTurn"/>, which holds the
/// store's locks for that turn only, and its bodies are decrypted and decompressed chunk by chunk into the zip
/// entry. A turn is read twice: first to verify every body against its stored hash, then to write it, so a body
/// that fails verification is never written. All of it runs on one thread, because the turn locks are owned by
/// the thread that took them.
/// </para>
/// <para>
/// <b>One at a time.</b> A second export while one runs fails with
/// <see cref="ConversationExportFailure.InProgress"/>. Share one writer instance across callers for that to
/// hold.
/// </para>
/// </remarks>
public sealed class ConversationExportWriter
{
    /// <summary>The version of the zip layout; import refuses a version it does not know.</summary>
    public const int SchemaVersion = 1;

    private const long MinimumReserveBytes = 1L << 30;
    private const int CopyBufferBytes = 80 * 1024;
    private const long MaxMetadataBytes = 1024 * 1024;

    private static readonly SessionBodyKind[] ExchangeKinds =
    [
        SessionBodyKind.ClientRequest,
        SessionBodyKind.ClientResponse,
        SessionBodyKind.ProviderRequest,
        SessionBodyKind.ProviderResponse,
    ];

    private static readonly HashSet<SessionBodyKind> MetadataKind = [SessionBodyKind.TurnMetadata];

    private readonly ILogger<ConversationExportWriter> _logger;
    private readonly Func<string, (long FreeBytes, long TotalBytes)> _volumeSpace;
    private readonly TimeProvider _time;
    private int _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationExportWriter"/> class.
    /// </summary>
    /// <param name="logger">Receives export outcomes; optional so a hand-built writer in a test needs no logging.</param>
    /// <param name="volumeSpace">Reports free and total bytes of the volume holding a path; a test passes a fake. Null reads the drive.</param>
    /// <param name="timeProvider">The clock for <c>exported_at_utc</c>; null uses the system clock.</param>
    public ConversationExportWriter(
        ILogger<ConversationExportWriter>? logger = null,
        Func<string, (long FreeBytes, long TotalBytes)>? volumeSpace = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationExportWriter>.Instance;
        _volumeSpace = volumeSpace ?? DriveSpace;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Writes the conversations that match <paramref name="filter"/> to a new zip at
    /// <paramref name="absoluteOutputPath"/>. Sessions captured before body hashes existed are hashed first (a
    /// lazy backfill under the session's gate); one that cannot be is exported with
    /// <c>content_fidelity</c> <c>unverified</c> instead of failing the export.
    /// </summary>
    /// <param name="store">The session store to read.</param>
    /// <param name="filter">Which turns to include.</param>
    /// <param name="absoluteOutputPath">The zip's path: absolute, not yet existing, and outside the data directory and session folder.</param>
    /// <param name="progress">Receives a report after each turn; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the export, which then leaves no file behind.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="ConversationExportException">When another export is running, the destination breaks a rule, or the volume is too full.</exception>
    /// <exception cref="OperationCanceledException">When the export was cancelled.</exception>
    public Task<ConversationExportResult> WriteAsync(
        SessionStore store,
        ConversationExportFilter filter,
        string absoluteOutputPath,
        IProgress<ConversationExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(absoluteOutputPath);

        // Synchronous work on a pool thread: the per-turn store locks belong to the thread that takes them,
        // so no await may sit between opening a turn and disposing it.
        return Task.Run(() => Run(store, filter, absoluteOutputPath, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Claims the single-flight slot and runs one export.
    /// </summary>
    /// <param name="store">The session store to read.</param>
    /// <param name="filter">Which turns to include.</param>
    /// <param name="path">The requested destination.</param>
    /// <param name="progress">Receives a report after each turn.</param>
    /// <param name="ct">Cancels the export.</param>
    /// <returns>What was written.</returns>
    private ConversationExportResult Run(
        SessionStore store,
        ConversationExportFilter filter,
        string path,
        IProgress<ConversationExportProgress>? progress,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new ConversationExportException(
                ConversationExportFailure.InProgress, "A conversation export is already running.");
        }

        try
        {
            var destination = ValidateDestination(store, path);
            var plan = Plan(store, filter, ct);
            EnsureFreeSpace(destination, plan.Sum(session => session.BodyBytes));
            return Write(store, filter, plan, destination, progress, ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Checks the destination rules: absolute, nothing there yet (neither the zip nor its partial), in an
    /// existing folder, and not under the data directory or the session folder.
    /// </summary>
    /// <param name="store">The store whose folders are off limits.</param>
    /// <param name="path">The requested destination.</param>
    /// <returns>The normalized absolute destination.</returns>
    /// <exception cref="ConversationExportException">When a rule is broken.</exception>
    private static string ValidateDestination(SessionStore store, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw Invalid("The export destination must be an absolute path.");
        }

        var full = Path.GetFullPath(path);
        var sessionFolder = Path.GetFullPath(store.Folder);
        var dataDirectory = Path.GetDirectoryName(sessionFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // A symlink or junction in the destination's folder chain can point back into the data directory, which
        // a purely textual comparison would miss, so the destination is also checked with its links followed.
        var resolved = Path.Combine(ResolveLinks(Path.GetDirectoryName(full) ?? full), Path.GetFileName(full));
        if (IsUnder(full, sessionFolder) || (dataDirectory is not null && IsUnder(full, dataDirectory))
            || IsUnder(resolved, ResolveLinks(sessionFolder))
            || (dataDirectory is not null && IsUnder(resolved, ResolveLinks(dataDirectory))))
        {
            throw Invalid("The export destination must be outside the router's data directory.");
        }

        if (File.Exists(full) || Directory.Exists(full) || File.Exists(full + ".partial"))
        {
            throw Invalid("The export destination already exists; an export never overwrites a file.");
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent))
        {
            throw Invalid("The export destination's folder does not exist.");
        }

        return full;

        static ConversationExportException Invalid(string message) =>
            new(ConversationExportFailure.InvalidDestination, message);
    }

    /// <summary>
    /// Follows every symbolic link or junction in a directory path, from the root down, so two spellings of one
    /// place compare equal. A part that does not exist is kept as written.
    /// </summary>
    /// <param name="directory">A normalized absolute directory path.</param>
    /// <returns>The path with each link in it replaced by its final target.</returns>
    private static string ResolveLinks(string directory)
    {
        var root = Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(root)) return directory;

        var current = root;
        var parts = directory[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            try
            {
                var info = new DirectoryInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable part cannot be followed; the textual comparison still applies to it.
            }
        }

        return current;
    }

    /// <summary>Reports whether <paramref name="path"/> is the folder itself or inside it.</summary>
    /// <param name="path">A normalized absolute path.</param>
    /// <param name="folder">A normalized absolute folder path.</param>
    /// <returns><see langword="true"/> when the path is at or below the folder.</returns>
    private static bool IsUnder(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return (path + Path.DirectorySeparatorChar).StartsWith(root, comparison);
    }

    /// <summary>
    /// Refuses the export unless the volume has room for every body plus the larger of 1 GiB and 10% of the
    /// volume. The body total is an upper bound, because the zip compresses.
    /// </summary>
    /// <param name="destination">The destination, whose volume is checked.</param>
    /// <param name="bodyBytes">The sum of the stored plaintext lengths to be written.</param>
    /// <exception cref="ConversationExportException">When the volume is too full.</exception>
    private void EnsureFreeSpace(string destination, long bodyBytes)
    {
        var (free, total) = _volumeSpace(destination);
        var reserve = Math.Max(MinimumReserveBytes, total / 10);
        if (free < bodyBytes + reserve)
        {
            throw new ConversationExportException(
                ConversationExportFailure.InsufficientDiskSpace,
                "The destination volume does not have enough free space for this export.");
        }
    }

    /// <summary>
    /// Resolves which sessions and turns the filter selects, in output order, backfilling hashes for sessions
    /// that lack them. The turn lists are a snapshot: a turn appended later is not in this export.
    /// </summary>
    /// <param name="store">The session store.</param>
    /// <param name="filter">Which turns to include.</param>
    /// <param name="ct">Cancels the planning.</param>
    /// <returns>The selected sessions, ordered by client session id then archive session id.</returns>
    private static List<PlannedSession> Plan(SessionStore store, ConversationExportFilter filter, CancellationToken ct)
    {
        var sessions = store.ListSessions()
            .Where(row => MatchesSession(row, filter.SessionId))
            .OrderBy(row => row.ClientSessionId, StringComparer.Ordinal)
            .ThenBy(row => row.ArchiveSessionId)
            .ToList();

        var plan = new List<PlannedSession>();
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            var turns = store.ListTurns(session.ArchiveSessionId)
                .Where(turn => (filter.From is null || turn.CreatedAtUtc >= filter.From)
                               && (filter.To is null || turn.CreatedAtUtc <= filter.To))
                .ToList();
            if (turns.Count > 0 && filter.NeedsMetadata)
            {
                var metadata = store.TryReadBodies(
                    session.ArchiveSessionId, MetadataKind, turns.Select(turn => turn.ArchiveTurnId).ToList());
                turns = turns.Where(turn => metadata.TryGetValue((turn.ArchiveTurnId, SessionBodyKind.TurnMetadata), out var json)
                                            && MatchesMetadata(json, filter)).ToList();
            }

            if (turns.Count == 0) continue;

            // Only the total is kept: the hash rows are read again, one session at a time, while that session is
            // written, so memory does not grow with the number of sessions in the export.
            var verified = false;
            var bodyBytes = 0L;
            if (store.HasBodyHashes(session.ArchiveSessionId) || store.BackfillBodyHashes(session.ArchiveSessionId))
            {
                verified = true;
                var hashes = store.ListBodyHashes(session.ArchiveSessionId)
                    .ToDictionary(row => (row.ArchiveTurnId, row.Kind));
                foreach (var turn in turns)
                {
                    foreach (var kind in ExchangeKinds)
                    {
                        if (hashes.TryGetValue((turn.ArchiveTurnId, kind), out var row)) bodyBytes += row.Length;
                    }
                }
            }

            plan.Add(new PlannedSession(session, turns, verified, bodyBytes));
        }

        return plan;
    }

    /// <summary>Reports whether a session carries the filter's session id as its archive id or its client id.</summary>
    /// <param name="session">The session row.</param>
    /// <param name="sessionId">The filter's session id, or <see langword="null"/> for any.</param>
    /// <returns><see langword="true"/> when the session is selected.</returns>
    private static bool MatchesSession(SessionFileRow session, string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId)
        || string.Equals(session.ClientSessionId, sessionId, StringComparison.Ordinal)
        || (Guid.TryParse(sessionId, out var archiveId) && archiveId == session.ArchiveSessionId);

    /// <summary>Reports whether a turn's metadata snapshot satisfies the harness, provider and model filters.</summary>
    /// <param name="metadataJson">The decrypted <see cref="SessionBodyKind.TurnMetadata"/> body.</param>
    /// <param name="filter">The filter.</param>
    /// <returns><see langword="true"/> when every metadata filter that is set matches.</returns>
    private static bool MatchesMetadata(byte[] metadataJson, ConversationExportFilter filter)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            return Matches(root, filter.Harness, "harness")
                   && Matches(root, filter.Provider, "provider")
                   && (string.IsNullOrWhiteSpace(filter.Model)
                       || Matches(root, filter.Model, "requested_model")
                       || Matches(root, filter.Model, "routed_model")
                       || Matches(root, filter.Model, "resolved_provider_model_id"));
        }
        catch (JsonException)
        {
            return false;
        }

        static bool Matches(JsonElement root, string? wanted, string property) =>
            string.IsNullOrWhiteSpace(wanted)
            || (root.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Writes the zip to a partial file and renames it into place, deleting the partial file on any failure.
    /// </summary>
    /// <param name="store">The session store.</param>
    /// <param name="filter">The filter recorded in the manifest.</param>
    /// <param name="plan">The sessions and turns to write.</param>
    /// <param name="destination">The validated destination.</param>
    /// <param name="progress">Receives a report after each turn.</param>
    /// <param name="ct">Cancels the export.</param>
    /// <returns>What was written.</returns>
    private ConversationExportResult Write(
        SessionStore store,
        ConversationExportFilter filter,
        List<PlannedSession> plan,
        string destination,
        IProgress<ConversationExportProgress>? progress,
        CancellationToken ct)
    {
        var partial = destination + ".partial";
        var scratch = Path.Combine(store.Folder, $"{Guid.NewGuid():N}.tmp");
        var buffer = new byte[CopyBufferBytes];
        FileStream? zipFile = null;
        var finished = false;
        try
        {
            zipFile = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ConversationExportResult result;
            using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: true))
            {
                // turns.jsonl lines go to a scratch file so memory does not grow with the corpus. It holds
                // no body text and is deleted on close; a crash leaves a .tmp that startup recovery sweeps.
                using var turnsFile = new FileStream(
                    scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
                var state = new ExportState();
                foreach (var session in plan)
                {
                    WriteSession(store, session, zip, turnsFile, state, progress, buffer, ct);
                }

                ct.ThrowIfCancellationRequested();
                var turnsEntry = WriteTurnsEntry(zip, turnsFile, buffer, ct);
                WriteManifest(store, filter, plan, zip, turnsEntry, state);
                result = new ConversationExportResult(
                    destination,
                    state.Sessions.Values.Count(s => s.TurnsExported > 0),
                    state.Turns,
                    state.MissingBodies,
                    state.CorruptTurns.Count,
                    state.Sessions.Values.Count(s => s.Incomplete),
                    state.BodyBytes);
            }

            ct.ThrowIfCancellationRequested();
            zipFile.Flush(flushToDisk: true);
            zipFile.Dispose();
            zipFile = null;
            File.Move(partial, destination, overwrite: false);
            finished = true;
            _logger.LogInformation(
                "Exported {Turns} turns from {Conversations} conversations; {CorruptTurns} corrupt turns, {IncompleteSessions} incomplete sessions.",
                result.Turns, result.Conversations, result.CorruptTurns, result.IncompleteSessions);
            return result;
        }
        finally
        {
            zipFile?.Dispose();
            if (!finished) DeleteQuietly(partial);
        }
    }

    /// <summary>Deletes a file left by a failed export, ignoring a failure to do so.</summary>
    /// <param name="path">The partial file.</param>
    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The partial export file could not be deleted.");
        }
    }

    /// <summary>
    /// Writes every selected turn of one session. A turn that fails verification is left out and listed in the
    /// manifest; a session that disappears ends its turns early and is flagged incomplete.
    /// </summary>
    /// <param name="store">The session store.</param>
    /// <param name="session">The session and its selected turns.</param>
    /// <param name="zip">The archive being written.</param>
    /// <param name="turnsFile">Scratch file collecting the <c>turns.jsonl</c> lines.</param>
    /// <param name="state">Totals and manifest entries so far.</param>
    /// <param name="progress">Receives a report after each turn.</param>
    /// <param name="buffer">A reusable copy buffer.</param>
    /// <param name="ct">Cancels the export.</param>
    private void WriteSession(
        SessionStore store,
        PlannedSession session,
        ZipArchive zip,
        FileStream turnsFile,
        ExportState state,
        IProgress<ConversationExportProgress>? progress,
        byte[] buffer,
        CancellationToken ct)
    {
        var sessionState = new SessionState(session.Row);
        state.Sessions[session.Row.ArchiveSessionId] = sessionState;

        // Null when no stored hashes could be had for this session: its turns are then exported unverified.
        var hashes = session.Verified
            ? store.ListBodyHashes(session.Row.ArchiveSessionId).ToDictionary(row => (row.ArchiveTurnId, row.Kind))
            : null;

        foreach (var turn in session.Turns)
        {
            ct.ThrowIfCancellationRequested();
            SessionTurnReader? reader;
            try
            {
                reader = store.OpenTurn(session.Row.ArchiveSessionId, turn.ArchiveTurnId);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                reader = null;
            }
            catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException)
            {
                RecordCorrupt(state, sessionState, turn, "frame_unreadable", ex);
                continue;
            }

            if (reader is null)
            {
                sessionState.Incomplete = true;
                break;
            }

            bool written;
            using (reader)
            {
                written = WriteTurn(reader, session, hashes, zip, turnsFile, state, sessionState, buffer, ct);
            }

            // After the turn's locks are released, so a progress handler that calls back into the store
            // cannot deadlock against this thread.
            if (written) progress?.Report(new ConversationExportProgress(state.Turns, state.BodyBytes));
        }
    }

    /// <summary>
    /// Verifies one turn against its stored hashes and, if it checks out, writes its bodies and its
    /// <c>turns.jsonl</c> line. A verification failure is recorded and nothing of the turn is written.
    /// </summary>
    /// <param name="reader">The open turn.</param>
    /// <param name="session">The session the turn belongs to.</param>
    /// <param name="hashes">The session's stored body hashes, or <see langword="null"/> when none could be computed.</param>
    /// <param name="zip">The archive being written.</param>
    /// <param name="turnsFile">Scratch file collecting the <c>turns.jsonl</c> lines.</param>
    /// <param name="state">Totals and manifest entries so far.</param>
    /// <param name="sessionState">The session's totals.</param>
    /// <param name="buffer">A reusable copy buffer.</param>
    /// <param name="ct">Cancels the export.</param>
    /// <returns><see langword="true"/> when the turn was written.</returns>
    private bool WriteTurn(
        SessionTurnReader reader,
        PlannedSession session,
        Dictionary<(Guid, SessionBodyKind), SessionBodyHashRow>? hashes,
        ZipArchive zip,
        FileStream turnsFile,
        ExportState state,
        SessionState sessionState,
        byte[] buffer,
        CancellationToken ct)
    {
        var turn = reader.Turn;
        byte[]? metadata;
        string? problem;
        try
        {
            problem = Verify(reader, hashes, buffer, ct, out metadata);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException)
        {
            RecordCorrupt(state, sessionState, turn, "frame_unreadable", ex);
            return false;
        }

        if (problem is not null)
        {
            RecordCorrupt(state, sessionState, turn, problem, exception: null);
            return false;
        }

        var bodies = new Dictionary<SessionBodyKind, WrittenBody>();
        var turnBytes = 0L;
        foreach (var frame in reader.ReadFrames())
        {
            using (frame)
            {
                if (!Array.Exists(ExchangeKinds, kind => kind == frame.Kind)) continue;
                if (frame.Body is null)
                {
                    bodies[frame.Kind] = new WrittenBody(null, null, 0, Missing: true);
                    continue;
                }

                var path = EntryPath(turn, frame.Kind);
                var entry = zip.CreateEntry(path);
                byte[] sha256;
                long length;
                using (var entryStream = entry.Open())
                using (var hashing = new HashingStream(entryStream, leaveOpen: true))
                {
                    Copy(frame.Body, hashing, buffer, limit: null, ct);
                    sha256 = hashing.FinishHash();
                    length = hashing.BytesWritten;
                }

                if (hashes is not null
                    && hashes.TryGetValue((turn.ArchiveTurnId, frame.Kind), out var stored)
                    && (stored.Sha256 is null || !stored.Sha256.AsSpan().SequenceEqual(sha256) || stored.Length != length))
                {
                    // Verify just passed, and the turn is locked, so this means the file changed under us.
                    throw new InvalidDataException("A body changed between verification and writing.");
                }

                bodies[frame.Kind] = new WrittenBody(path, sha256, length, Missing: false);
                turnBytes += length;
                state.BodyEntries.Add(new ManifestBody(path, sha256, length));
            }
        }

        WriteTurnLine(turnsFile, session, turn, metadata, bodies, verified: hashes is not null);
        state.Turns++;
        state.BodyBytes += turnBytes;
        state.MissingBodies += bodies.Values.Count(body => body.Missing);
        sessionState.TurnsExported++;
        return true;
    }

    /// <summary>
    /// Reads a turn once without writing anything: authenticates and decompresses every body, hashes it, and
    /// compares it to the stored hash. Also captures the metadata snapshot.
    /// </summary>
    /// <param name="reader">The open turn.</param>
    /// <param name="hashes">The session's stored hashes, or <see langword="null"/> when none could be computed.</param>
    /// <param name="buffer">A reusable copy buffer.</param>
    /// <param name="ct">Cancels the export.</param>
    /// <param name="metadata">The turn's metadata snapshot, or <see langword="null"/> when it has none.</param>
    /// <returns>A short reason when the turn must not be exported, or <see langword="null"/> when it verified.</returns>
    private static string? Verify(
        SessionTurnReader reader,
        Dictionary<(Guid, SessionBodyKind), SessionBodyHashRow>? hashes,
        byte[] buffer,
        CancellationToken ct,
        out byte[]? metadata)
    {
        metadata = null;
        var seen = new HashSet<SessionBodyKind>();
        var turnId = reader.Turn.ArchiveTurnId;
        foreach (var frame in reader.ReadFrames())
        {
            using (frame)
            {
                if (frame.Kind == SessionBodyKind.Extracts) continue;
                if (!seen.Add(frame.Kind)) return "duplicate_body";

                SessionBodyHashRow? stored = null;
                if (hashes is not null && !hashes.TryGetValue((turnId, frame.Kind), out stored))
                {
                    return "hash_missing";
                }

                if (stored is not null && stored.Missing != frame.IsMissing) return "hash_mismatch";
                if (frame.Body is null) continue;

                using var captured = frame.Kind == SessionBodyKind.TurnMetadata ? new MemoryStream() : null;
                using var hashing = new HashingStream(captured ?? Stream.Null, leaveOpen: true);
                Copy(frame.Body, hashing, buffer, frame.Kind == SessionBodyKind.TurnMetadata ? MaxMetadataBytes : null, ct);
                var sha256 = hashing.FinishHash();
                if (stored is not null
                    && (stored.Sha256 is null || !stored.Sha256.AsSpan().SequenceEqual(sha256) || stored.Length != hashing.BytesWritten))
                {
                    return "hash_mismatch";
                }

                if (captured is not null) metadata = captured.ToArray();
            }
        }

        if (hashes is not null)
        {
            foreach (var kind in ExchangeKinds.Append(SessionBodyKind.TurnMetadata))
            {
                if (hashes.ContainsKey((turnId, kind)) && !seen.Contains(kind)) return "body_absent";
            }
        }

        return null;
    }

    /// <summary>Copies a stream to another in bounded pieces, checking for cancellation between pieces.</summary>
    /// <param name="source">The stream to read.</param>
    /// <param name="destination">The stream to write.</param>
    /// <param name="buffer">A reusable copy buffer.</param>
    /// <param name="limit">The most bytes to accept, or <see langword="null"/> for no limit.</param>
    /// <param name="ct">Cancels the copy.</param>
    /// <exception cref="InvalidDataException">When the source exceeds <paramref name="limit"/>.</exception>
    private static void Copy(Stream source, Stream destination, byte[] buffer, long? limit, CancellationToken ct)
    {
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += read;
            if (limit is { } max && total > max) throw new InvalidDataException("A body is larger than its limit.");
            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>Notes a turn that was left out because it failed verification.</summary>
    /// <param name="state">Totals and manifest entries so far.</param>
    /// <param name="sessionState">The turn's session totals.</param>
    /// <param name="turn">The turn.</param>
    /// <param name="reason">A short reason code, which goes in the manifest.</param>
    /// <param name="exception">The read failure behind it, or <see langword="null"/> for a hash mismatch.</param>
    private void RecordCorrupt(
        ExportState state, SessionState sessionState, SessionTurnRow turn, string reason, Exception? exception)
    {
        state.CorruptTurns.Add(new CorruptTurn(turn.ArchiveSessionId, turn.ArchiveTurnId, reason));
        sessionState.CorruptTurns++;
        _logger.LogWarning(
            exception, "A turn failed verification and was left out of the export: {ArchiveTurnId} ({Reason}).",
            turn.ArchiveTurnId, reason);
    }

    /// <summary>Builds a body's path inside the zip.</summary>
    /// <param name="turn">The turn.</param>
    /// <param name="kind">The exchange body kind.</param>
    /// <returns>The entry name, with forward slashes.</returns>
    private static string EntryPath(SessionTurnRow turn, SessionBodyKind kind) =>
        string.Create(CultureInfo.InvariantCulture,
            $"conversations/{turn.ArchiveSessionId:D}/turns/{turn.TurnSequence:D4}.{BodyFileSuffix(kind)}.json");

    /// <summary>Names a body kind in a file name.</summary>
    /// <param name="kind">The exchange body kind.</param>
    /// <returns>The file-name part between the turn number and <c>.json</c>.</returns>
    private static string BodyFileSuffix(SessionBodyKind kind) => kind switch
    {
        SessionBodyKind.ClientRequest => "request",
        SessionBodyKind.ClientResponse => "response",
        SessionBodyKind.ProviderRequest => "provider-request",
        SessionBodyKind.ProviderResponse => "provider-response",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an exchange body."),
    };

    /// <summary>Names a body kind as a <c>turns.jsonl</c> property.</summary>
    /// <param name="kind">The exchange body kind.</param>
    /// <returns>The property name under <c>bodies</c>.</returns>
    private static string BodyPropertyName(SessionBodyKind kind) => BodyFileSuffix(kind).Replace('-', '_');

    /// <summary>Appends one turn's <c>turns.jsonl</c> line to the scratch file.</summary>
    /// <param name="turnsFile">Scratch file collecting the lines.</param>
    /// <param name="session">The session the turn belongs to.</param>
    /// <param name="turn">The turn.</param>
    /// <param name="metadata">The metadata snapshot JSON, or <see langword="null"/> when the turn has none.</param>
    /// <param name="bodies">What was written for each exchange body the turn has.</param>
    /// <param name="verified">Whether the bodies were checked against stored hashes.</param>
    private static void WriteTurnLine(
        FileStream turnsFile,
        PlannedSession session,
        SessionTurnRow turn,
        byte[]? metadata,
        Dictionary<SessionBodyKind, WrittenBody> bodies,
        bool verified)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("archive_turn_id", turn.ArchiveTurnId);
            json.WriteString("archive_session_id", turn.ArchiveSessionId);
            json.WriteString("client_session_id", session.Row.ClientSessionId);
            json.WriteNumber("turn_sequence", turn.TurnSequence);
            json.WriteString("created_at_utc", FormatTime(turn.CreatedAtUtc));
            json.WriteString("origin", turn.Origin);

            // The streaming obscurer does not report whether it changed anything, so this is not guessed.
            json.WriteNull("secrets_obscured");
            json.WriteString("content_fidelity", ContentFidelity(verified, bodies));

            json.WritePropertyName("metadata");
            if (!TryWriteJson(json, metadata)) json.WriteNullValue();

            json.WriteStartObject("bodies");
            foreach (var kind in ExchangeKinds)
            {
                if (!bodies.TryGetValue(kind, out var body)) continue;

                json.WriteStartObject(BodyPropertyName(kind));
                if (body.Missing)
                {
                    json.WriteNull("path");
                    json.WriteNull("sha256");
                    json.WriteNull("length");
                    json.WriteBoolean("missing", true);
                }
                else
                {
                    json.WriteString("path", body.Path);
                    json.WriteString("sha256", Convert.ToHexStringLower(body.Sha256!));
                    json.WriteNumber("length", body.Length);
                    json.WriteBoolean("missing", false);
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        buffer.Position = 0;
        buffer.CopyTo(turnsFile);
    }

    /// <summary>Chooses a turn's <c>content_fidelity</c> value.</summary>
    /// <param name="hashesVerified">Whether the turn's bodies were checked against stored hashes.</param>
    /// <param name="bodies">What was written for each exchange body.</param>
    /// <returns><c>missing-body</c> when a body is absent, <c>unverified</c> when no stored hash backed it, otherwise <c>full-body</c>.</returns>
    private static string ContentFidelity(bool hashesVerified, Dictionary<SessionBodyKind, WrittenBody> bodies)
    {
        if (bodies.Values.Any(body => body.Missing)) return "missing-body";
        return hashesVerified ? "full-body" : "unverified";
    }

    /// <summary>Writes stored JSON as a value, if it parses.</summary>
    /// <param name="json">The writer, positioned at a property value.</param>
    /// <param name="utf8Json">The JSON to embed, or <see langword="null"/>.</param>
    /// <returns><see langword="false"/> when there was nothing valid to write; nothing has been written then.</returns>
    private static bool TryWriteJson(Utf8JsonWriter json, byte[]? utf8Json)
    {
        if (utf8Json is null) return false;
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            document.RootElement.WriteTo(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Copies the scratch lines into the <c>turns.jsonl</c> entry, hashing them as they are written.</summary>
    /// <param name="zip">The archive being written.</param>
    /// <param name="turnsFile">Scratch file holding every line.</param>
    /// <param name="buffer">A reusable copy buffer.</param>
    /// <param name="ct">Cancels the export.</param>
    /// <returns>The entry's hash and length.</returns>
    private static ManifestBody WriteTurnsEntry(ZipArchive zip, FileStream turnsFile, byte[] buffer, CancellationToken ct)
    {
        const string path = "turns.jsonl";
        turnsFile.Flush();
        turnsFile.Position = 0;
        using var entryStream = zip.CreateEntry(path).Open();
        using var hashing = new HashingStream(entryStream, leaveOpen: true);
        Copy(turnsFile, hashing, buffer, limit: null, ct);
        return new ManifestBody(path, hashing.FinishHash(), hashing.BytesWritten);
    }

    /// <summary>Writes <c>manifest.json</c>.</summary>
    /// <param name="store">The store, for each session's checksum.</param>
    /// <param name="filter">The filter that produced the export.</param>
    /// <param name="plan">The planned sessions.</param>
    /// <param name="zip">The archive being written.</param>
    /// <param name="turnsEntry">The hash and length of <c>turns.jsonl</c>.</param>
    /// <param name="state">Totals and manifest entries.</param>
    private void WriteManifest(
        SessionStore store,
        ConversationExportFilter filter,
        List<PlannedSession> plan,
        ZipArchive zip,
        ManifestBody turnsEntry,
        ExportState state)
    {
        using var entryStream = zip.CreateEntry("manifest.json").Open();
        using var json = new Utf8JsonWriter(entryStream, new JsonWriterOptions { Indented = true });
        var incomplete = state.Sessions.Values.Any(session => session.Incomplete);

        json.WriteStartObject();
        json.WriteNumber("schema_version", SchemaVersion);
        json.WriteString("exported_at_utc", FormatTime(_time.GetUtcNow()));
        json.WriteString("router_version", RouterVersion());
        json.WriteBoolean("incomplete", incomplete);

        json.WriteStartObject("filter");
        WriteNullableTime(json, "from", filter.From);
        WriteNullableTime(json, "to", filter.To);
        json.WriteString("session_id", filter.SessionId);
        json.WriteString("harness", filter.Harness);
        json.WriteString("provider", filter.Provider);
        json.WriteString("model", filter.Model);
        json.WriteEndObject();

        json.WriteStartObject("counts");
        json.WriteNumber("conversations", state.Sessions.Values.Count(session => session.TurnsExported > 0));
        json.WriteNumber("turns", state.Turns);
        json.WriteNumber("missing_bodies", state.MissingBodies);
        json.WriteNumber("corrupt_turns", state.CorruptTurns.Count);
        json.WriteNumber("incomplete_sessions", state.Sessions.Values.Count(session => session.Incomplete));
        json.WriteNumber("bytes", state.BodyBytes);
        json.WriteEndObject();

        json.WriteStartObject("turns_jsonl");
        json.WriteString("path", turnsEntry.Path);
        json.WriteString("sha256", Convert.ToHexStringLower(turnsEntry.Sha256));
        json.WriteNumber("length", turnsEntry.Length);
        json.WriteEndObject();

        json.WriteStartArray("sessions");
        foreach (var planned in plan)
        {
            var sessionState = state.Sessions[planned.Row.ArchiveSessionId];
            json.WriteStartObject();
            json.WriteString("archive_session_id", planned.Row.ArchiveSessionId);
            json.WriteString("client_session_id", planned.Row.ClientSessionId);

            // The checksum covers the whole session as stored, not only the turns this filter selected.
            var checksum = store.GetSessionSha256(planned.Row.ArchiveSessionId);
            if (checksum is null) json.WriteNull("session_sha256");
            else json.WriteString("session_sha256", Convert.ToHexStringLower(checksum));
            json.WriteNumber("turns_selected", planned.Turns.Count);
            json.WriteNumber("turns_exported", sessionState.TurnsExported);
            json.WriteNumber("corrupt_turns", sessionState.CorruptTurns);
            json.WriteBoolean("incomplete", sessionState.Incomplete);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartArray("corrupt_turns");
        foreach (var corrupt in state.CorruptTurns)
        {
            json.WriteStartObject();
            json.WriteString("archive_session_id", corrupt.ArchiveSessionId);
            json.WriteString("archive_turn_id", corrupt.ArchiveTurnId);
            json.WriteString("reason", corrupt.Reason);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartArray("bodies");
        foreach (var body in state.BodyEntries)
        {
            json.WriteStartObject();
            json.WriteString("path", body.Path);
            json.WriteString("sha256", Convert.ToHexStringLower(body.Sha256));
            json.WriteNumber("length", body.Length);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>Writes a UTC timestamp property, or <see langword="null"/> when there is none.</summary>
    /// <param name="json">The writer.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The timestamp, or <see langword="null"/>.</param>
    private static void WriteNullableTime(Utf8JsonWriter json, string name, DateTimeOffset? value)
    {
        if (value is { } time) json.WriteString(name, FormatTime(time));
        else json.WriteNull(name);
    }

    /// <summary>Formats a timestamp as round-trip UTC.</summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The ISO 8601 text.</returns>
    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Gets the router's version without its build metadata.</summary>
    /// <returns>The informational version up to any <c>+</c>.</returns>
    private static string RouterVersion()
    {
        var version = typeof(ConversationExportWriter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    /// <summary>Reads free and total bytes of the volume holding a path.</summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns>The sizes; an unreadable drive (a UNC share, for one) counts as having plenty of room.</returns>
    private static (long FreeBytes, long TotalBytes) DriveSpace(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (long.MaxValue, 0);
        }
    }

    /// <summary>A session chosen for export, with its selected turns and the hashes to verify them against.</summary>
    /// <param name="Row">The session's index row.</param>
    /// <param name="Turns">The selected turns in order.</param>
    /// <param name="Verified">Whether stored body hashes exist for the session, so its turns can be verified.</param>
    /// <param name="BodyBytes">The sum of the selected turns' exchange body lengths, for the free-space check.</param>
    private sealed record PlannedSession(
        SessionFileRow Row,
        List<SessionTurnRow> Turns,
        bool Verified,
        long BodyBytes);

    /// <summary>One body written to the zip, or recorded as missing.</summary>
    /// <param name="Path">The entry name, or <see langword="null"/> for a missing body.</param>
    /// <param name="Sha256">The hash of the written bytes, or <see langword="null"/> for a missing body.</param>
    /// <param name="Length">The written length.</param>
    /// <param name="Missing">Whether capture failed and no file was written.</param>
    private sealed record WrittenBody(string? Path, byte[]? Sha256, long Length, bool Missing);

    /// <summary>One entry the manifest lists.</summary>
    /// <param name="Path">The entry name.</param>
    /// <param name="Sha256">The hash of the entry's bytes.</param>
    /// <param name="Length">The entry's length.</param>
    private sealed record ManifestBody(string Path, byte[] Sha256, long Length);

    /// <summary>A turn left out of the export.</summary>
    /// <param name="ArchiveSessionId">The turn's session.</param>
    /// <param name="ArchiveTurnId">The turn.</param>
    /// <param name="Reason">A short reason code.</param>
    private sealed record CorruptTurn(Guid ArchiveSessionId, Guid ArchiveTurnId, string Reason);

    /// <summary>Per-session totals the manifest reports.</summary>
    private sealed class SessionState(SessionFileRow row)
    {
        /// <summary>Gets the session's index row.</summary>
        public SessionFileRow Row { get; } = row;

        /// <summary>Gets or sets how many of the session's turns were written.</summary>
        public int TurnsExported { get; set; }

        /// <summary>Gets or sets how many of the session's turns were left out as corrupt.</summary>
        public int CorruptTurns { get; set; }

        /// <summary>Gets or sets a value indicating whether the session disappeared before all its turns were written.</summary>
        public bool Incomplete { get; set; }
    }

    /// <summary>Totals and manifest entries accumulated while the zip is written.</summary>
    private sealed class ExportState
    {
        /// <summary>Gets the per-session totals.</summary>
        public Dictionary<Guid, SessionState> Sessions { get; } = [];

        /// <summary>Gets the body entries, in the order they were written.</summary>
        public List<ManifestBody> BodyEntries { get; } = [];

        /// <summary>Gets the turns left out of the export.</summary>
        public List<CorruptTurn> CorruptTurns { get; } = [];

        /// <summary>Gets or sets how many turns were written.</summary>
        public int Turns { get; set; }

        /// <summary>Gets or sets how many bodies were recorded as missing.</summary>
        public int MissingBodies { get; set; }

        /// <summary>Gets or sets the stored plaintext bytes written.</summary>
        public long BodyBytes { get; set; }
    }
}
