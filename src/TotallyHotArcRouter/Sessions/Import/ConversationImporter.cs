using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Sessions.Export;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Sessions.Import;

/// <summary>
/// Reads a conversation zip written by <see cref="ConversationExportWriter"/> back into a
/// <see cref="SessionStore"/> (#165 phase 4). It is a library: the gRPC service and the CLI call it and do not
/// parse the zip themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a new write path.</b> Every stored turn goes through <see cref="SessionStore.AppendTurn"/>, so secrets
/// are obscured, bodies are hashed, the session checksum advances and the commit order of ADR-0019 holds exactly
/// as for a captured turn. A killed import loses at most the turn in flight, and running it again under the
/// default conflict mode fills the gap without duplicating anything.
/// </para>
/// <para>
/// <b>Two passes.</b> The first pass reads <c>turns.jsonl</c> only, to decide which turns are already present, to
/// sum the bodies that will be stored (the disk-budget check) and to find the newest timestamp (the
/// <see cref="ImportTimestampMode.ImportTime"/> offset). The second pass reads bodies, and only the bodies of
/// turns it stores: a session whose checksum equals the manifest's, and a turn already present, never have a
/// body entry opened.
/// </para>
/// <para>
/// <b>Bodies are verified before they are stored.</b> A body is read through a guard that stops at its declared
/// length, so a compression bomb or a lying manifest cannot inflate past what the line promised, and its SHA-256
/// must equal the line's. A turn with a body that fails either check is rejected whole and nothing of it is
/// written.
/// </para>
/// <para>
/// <b>One at a time.</b> A second import while one runs fails with <see cref="ConversationImportFailure.InProgress"/>.
/// Share one importer instance across callers for that to hold.
/// </para>
/// </remarks>
public sealed class ConversationImporter
{
    /// <summary>The largest body kept in memory; a larger one is spooled to disk. Matches the telemetry capture cap.</summary>
    internal const int MaxInMemoryBodyBytes = 4 * 1024 * 1024;

    private const string ManifestPath = "manifest.json";
    private const string TurnsPath = "turns.jsonl";
    private const long MaxManifestBytes = 256L << 20;
    private const int MaxLineBytes = 16 << 20;
    private const int CopyBufferBytes = 80 * 1024;
    private const long MinimumReserveBytes = 1L << 30;
    private const int HashLength = 32;

    private static readonly SessionBodyKind[] ExchangeKinds =
    [
        SessionBodyKind.ClientRequest,
        SessionBodyKind.ClientResponse,
        SessionBodyKind.ProviderRequest,
        SessionBodyKind.ProviderResponse,
    ];

    private static readonly string[] ResponseShapeFallbacks = ["openai", "anthropic"];

    private readonly ILogger<ConversationImporter> _logger;
    private readonly Func<string, (long FreeBytes, long TotalBytes)> _volumeSpace;
    private readonly TimeProvider _time;
    private readonly IImportedTurnSink? _sink;
    private readonly ResponseTextExtractor _responseTextExtractor = new();
    private int _running;

    /// <summary>Initializes a new instance of the <see cref="ConversationImporter"/> class.</summary>
    /// <param name="sink">Told about each stored turn so a transcript row can be written; optional so a store-only import needs none.</param>
    /// <param name="logger">Receives import outcomes; optional so a hand-built importer in a test needs no logging.</param>
    /// <param name="volumeSpace">Reports free and total bytes of the volume holding a path; a test passes a fake. Null reads the drive.</param>
    /// <param name="timeProvider">The clock for the import time; null uses the system clock.</param>
    public ConversationImporter(
        IImportedTurnSink? sink = null,
        ILogger<ConversationImporter>? logger = null,
        Func<string, (long FreeBytes, long TotalBytes)>? volumeSpace = null,
        TimeProvider? timeProvider = null)
    {
        _sink = sink;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationImporter>.Instance;
        _volumeSpace = volumeSpace ?? ConversationExportWriter.DriveSpace;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Imports the conversations in a zip into <paramref name="store"/>.
    /// </summary>
    /// <param name="store">The session store to add to.</param>
    /// <param name="zipPath">The zip to read. It must not change while the import runs; the gRPC service imports from a private staged copy for that reason.</param>
    /// <param name="options">The import options.</param>
    /// <param name="progress">Receives a report after each turn; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the import. What was stored stays stored; running it again fills in the rest.</param>
    /// <param name="sourceSha256Hex">Lowercase hex SHA-256 of <paramref name="zipPath"/> if the caller already has it; otherwise it is computed.</param>
    /// <param name="sampleSize">
    /// The retention limit (the Sample Size setting), so the result can report how many turns the next retention
    /// pass would delete. <see langword="null"/> skips the report.
    /// </param>
    /// <returns>What the import did.</returns>
    /// <exception cref="ConversationImportException">When another import is running, the file is not a usable archive, or the volume is too full.</exception>
    /// <exception cref="OperationCanceledException">When the import was cancelled.</exception>
    public Task<ConversationImportResult> ImportAsync(
        SessionStore store,
        string zipPath,
        ConversationImportOptions options,
        IProgress<ConversationImportProgress>? progress,
        CancellationToken cancellationToken,
        string? sourceSha256Hex = null,
        int? sampleSize = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(
            async () =>
            {
                var result = await RunAsync(store, zipPath, options, progress, sourceSha256Hex, cancellationToken)
                    .ConfigureAwait(false);
                if (sampleSize is not { } limit) return result;

                var due = store.SelectExpiredSessions(limit).Sum(session => session.TurnCount);
                return result with { TurnsDueForRetention = due };
            },
            cancellationToken);
    }

    /// <summary>
    /// Builds the digest an import records for a turn: SHA-256 over the turn's 16-byte id and, for each exchange
    /// kind in fixed order, one kind byte and either the body's hash as the zip declared it or 32 zero bytes. A
    /// later import compares it before reading any body, so a turn whose bodies were changed by obscuring (and so
    /// no longer match the zip's hashes) is still recognised as already imported.
    /// </summary>
    /// <param name="archiveTurnId">The turn's id in the zip.</param>
    /// <param name="exchangeHashes">The zip's declared hash for each exchange kind that has a stored body.</param>
    /// <returns>The 32-byte digest.</returns>
    private static byte[] SourceDigest(Guid archiveTurnId, IReadOnlyDictionary<SessionBodyKind, byte[]> exchangeHashes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> id = stackalloc byte[16];
        archiveTurnId.TryWriteBytes(id);
        hash.AppendData(id);
        Span<byte> kindByte = stackalloc byte[1];
        Span<byte> zero = stackalloc byte[HashLength];
        foreach (var kind in ExchangeKinds)
        {
            kindByte[0] = (byte)kind;
            hash.AppendData(kindByte);
            if (exchangeHashes.TryGetValue(kind, out var digest) && digest.Length == HashLength) hash.AppendData(digest);
            else hash.AppendData(zero);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>Claims the single-flight slot and runs one import.</summary>
    private async Task<ConversationImportResult> RunAsync(
        SessionStore store,
        string zipPath,
        ConversationImportOptions options,
        IProgress<ConversationImportProgress>? progress,
        string? sourceSha256Hex,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new ConversationImportException(
                ConversationImportFailure.InProgress, "A conversation import is already running.");
        }

        try
        {
            if (!File.Exists(zipPath))
            {
                throw new ConversationImportException(ConversationImportFailure.ArchiveNotFound, "The archive does not exist.");
            }

            await using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes);
            ZipArchive zip;
            try
            {
                zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException)
            {
                throw new ConversationImportException(ConversationImportFailure.InvalidArchive, "The file is not a readable zip.");
            }

            await using (zip)
            {
                var entries = IndexEntries(zip);
                var manifest = ReadManifest(entries, out var turnsEntry);
                VerifyTurnsEntry(turnsEntry, manifest, ct);

                var plan = Plan(store, entries, turnsEntry, manifest, options, ct);
                EnsureFreeSpace(store, plan.BytesToStore);

                sourceSha256Hex ??= HashFile(file, ct);
                return await StoreAsync(store, entries, turnsEntry, plan, options, progress, sourceSha256Hex, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Checks every entry name and indexes the entries by name. A name that is absolute, climbs out of the
    /// archive, or repeats another is a zip-slip attempt or a corrupt archive, and fails the whole import.
    /// </summary>
    /// <param name="zip">The archive.</param>
    /// <returns>The entries by name, directory entries excluded.</returns>
    /// <exception cref="ConversationImportException">When a name is unsafe or duplicated.</exception>
    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (!IsSafeEntryName(entry.FullName))
            {
                throw Invalid("The archive contains an entry name that is not allowed.");
            }

            if (entry.FullName.EndsWith('/')) continue;
            if (!entries.TryAdd(entry.FullName, entry))
            {
                throw Invalid("The archive contains two entries with the same name.");
            }
        }

        return entries;
    }

    /// <summary>Reports whether an entry name stays inside the archive: relative, forward slashes, no <c>..</c>, no drive or stream marker.</summary>
    /// <param name="name">The entry's full name.</param>
    /// <returns><see langword="true"/> when the name is safe.</returns>
    internal static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '/' || name.Contains('\\') || name.Contains(':') || name.Contains('\0'))
        {
            return false;
        }

        foreach (var segment in name.Split('/'))
        {
            if (segment == "..") return false;
        }

        return true;
    }

    /// <summary>
    /// Reads and checks <c>manifest.json</c>: it exists, parses, and is schema version 1. Nothing is stored before
    /// this passes, and a failure stops the import.
    /// </summary>
    /// <param name="entries">The archive's entries.</param>
    /// <param name="turnsEntry">The <c>turns.jsonl</c> entry.</param>
    /// <returns>The parsed manifest values the import needs.</returns>
    /// <exception cref="ConversationImportException">When the manifest or <c>turns.jsonl</c> is missing, malformed, or of another version.</exception>
    private static ImportManifest ReadManifest(Dictionary<string, ZipArchiveEntry> entries, out ZipArchiveEntry turnsEntry)
    {
        if (!entries.TryGetValue(ManifestPath, out var manifestEntry))
        {
            throw Invalid("The archive has no manifest.json.");
        }

        if (manifestEntry.Length > MaxManifestBytes)
        {
            throw Invalid("The archive's manifest is larger than this router reads.");
        }

        using var stream = manifestEntry.Open();
        using var buffer = new MemoryStream();
        CopyBounded(stream, buffer, MaxManifestBytes, CancellationToken.None);
        buffer.Position = 0;

        try
        {
            using var document = JsonDocument.Parse(buffer);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("manifest.json is not a JSON object.");

            if (!root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion))
            {
                throw new ConversationImportException(
                    ConversationImportFailure.UnsupportedVersion, "The archive's manifest has no schema_version.");
            }

            if (schemaVersion != ConversationExportWriter.SchemaVersion)
            {
                throw new ConversationImportException(
                    ConversationImportFailure.UnsupportedVersion,
                    $"The archive has schema_version {schemaVersion}; this router reads version {ConversationExportWriter.SchemaVersion}.");
            }

            if (!entries.TryGetValue(TurnsPath, out turnsEntry!))
            {
                throw Invalid("The archive has no turns.jsonl.");
            }

            if (!root.TryGetProperty("turns_jsonl", out var turnsInfo) || turnsInfo.ValueKind != JsonValueKind.Object
                || !TryHex(turnsInfo, "sha256", out var turnsSha) || !turnsInfo.TryGetProperty("length", out var turnsLength)
                || !turnsLength.TryGetInt64(out var turnsBytes) || turnsBytes < 0)
            {
                throw Invalid("The manifest does not describe turns.jsonl.");
            }

            long declaredBytes = 0;
            if (root.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object
                && counts.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out var parsed))
            {
                declaredBytes = parsed;
            }

            var sessions = new Dictionary<Guid, byte[]?>();
            if (root.TryGetProperty("sessions", out var sessionList) && sessionList.ValueKind == JsonValueKind.Array)
            {
                foreach (var session in sessionList.EnumerateArray())
                {
                    if (session.ValueKind != JsonValueKind.Object || !TryGuid(session, "archive_session_id", out var id)) continue;
                    sessions[id] = TryHex(session, "session_sha256", out var checksum) ? checksum : null;
                }
            }

            return new ImportManifest(turnsSha, turnsBytes, declaredBytes, sessions);
        }
        catch (JsonException)
        {
            throw Invalid("manifest.json is not valid JSON.");
        }
    }

    /// <summary>
    /// Recomputes the SHA-256 and length of <c>turns.jsonl</c>, which is always checked because it is the index the
    /// rest of the import trusts.
    /// </summary>
    /// <param name="turnsEntry">The entry.</param>
    /// <param name="manifest">What the manifest declares for it.</param>
    /// <param name="ct">Cancels the check.</param>
    /// <exception cref="ConversationImportException">When the hash or length differs from the manifest.</exception>
    private static void VerifyTurnsEntry(ZipArchiveEntry turnsEntry, ImportManifest manifest, CancellationToken ct)
    {
        using var stream = turnsEntry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += read;
            if (total > manifest.TurnsLength) throw Invalid("turns.jsonl is longer than the manifest says.");
            hash.AppendData(buffer, 0, read);
        }

        if (total != manifest.TurnsLength || !hash.GetHashAndReset().AsSpan().SequenceEqual(manifest.TurnsSha256))
        {
            throw Invalid("turns.jsonl does not match the hash in the manifest.");
        }
    }

    /// <summary>
    /// First pass: reads <c>turns.jsonl</c> only. Decides which sessions are skipped whole (checksum equal to the
    /// manifest's), which turns are already present, new or in conflict, and totals what a store pass would write.
    /// </summary>
    /// <param name="store">The store being imported into.</param>
    /// <param name="entries">The archive's entries.</param>
    /// <param name="turnsEntry">The <c>turns.jsonl</c> entry.</param>
    /// <param name="manifest">The manifest values.</param>
    /// <param name="options">The import options.</param>
    /// <param name="ct">Cancels the pass.</param>
    /// <returns>What the second pass needs to know.</returns>
    /// <exception cref="ConversationImportException">When the lines declare more body bytes than the manifest.</exception>
    private ImportPlan Plan(
        SessionStore store,
        Dictionary<string, ZipArchiveEntry> entries,
        ZipArchiveEntry turnsEntry,
        ImportManifest manifest,
        ConversationImportOptions options,
        CancellationToken ct)
    {
        var skipped = new HashSet<Guid>();
        foreach (var (sessionId, checksum) in manifest.SessionChecksums)
        {
            if (checksum is not null && store.TryGetSession(sessionId)?.SessionSha256 is { } local
                && local.AsSpan().SequenceEqual(checksum))
            {
                skipped.Add(sessionId);
            }
        }

        var newSessions = new HashSet<Guid>();
        var seenSessions = new HashSet<Guid>();
        var mode = options.EffectiveConflictMode;
        var cache = new LocalHashCache(store);
        long declaredTotal = 0;
        long bytesToStore = 0;
        DateTimeOffset? newest = null;

        foreach (var parsed in ReadLines(turnsEntry, entries, ct))
        {
            using var line = parsed.Line;
            if (line is null) continue;

            declaredTotal += line.DeclaredBytes;
            if (skipped.Contains(line.ArchiveSessionId)) continue;

            var isNewSession = seenSessions.Add(line.ArchiveSessionId)
                ? (store.TryGetSession(line.ArchiveSessionId) is null && newSessions.Add(line.ArchiveSessionId))
                : newSessions.Contains(line.ArchiveSessionId);

            var disposition = Classify(store, cache, line);
            var stores = disposition == Disposition.New || (disposition == Disposition.Conflict && mode != ImportConflictMode.Skip);
            if (!stores) continue;

            bytesToStore += line.DeclaredBytes;
            if (isNewSession && (newest is null || line.CreatedAtUtc > newest)) newest = line.CreatedAtUtc;
        }

        if (manifest.DeclaredBytes > 0 && declaredTotal > manifest.DeclaredBytes)
        {
            throw Invalid("The turns declare more body bytes than the manifest says.");
        }

        return new ImportPlan(skipped, newSessions, bytesToStore, newest);
    }

    /// <summary>Refuses the import unless the volume has room for the bodies to be stored plus the larger of 1 GiB and 10% of the volume.</summary>
    /// <param name="store">The store whose volume receives the session files.</param>
    /// <param name="bytesToStore">The sum of declared lengths of the bodies that will be stored.</param>
    /// <exception cref="ConversationImportException">When the volume is too full.</exception>
    private void EnsureFreeSpace(SessionStore store, long bytesToStore)
    {
        var (free, total) = _volumeSpace(store.Folder);
        var reserve = Math.Max(MinimumReserveBytes, total / 10);
        if (free < bytesToStore + reserve)
        {
            throw new ConversationImportException(
                ConversationImportFailure.InsufficientDiskSpace,
                "The router's data volume does not have enough free space for this import.");
        }
    }

    /// <summary>
    /// Second pass: stores the turns the plan selected. The bodies of a skipped session or an already-present turn
    /// are never opened.
    /// </summary>
    /// <param name="store">The store being imported into.</param>
    /// <param name="entries">The archive's entries.</param>
    /// <param name="turnsEntry">The <c>turns.jsonl</c> entry.</param>
    /// <param name="plan">The first pass's findings.</param>
    /// <param name="options">The import options.</param>
    /// <param name="progress">Receives a report after each turn.</param>
    /// <param name="sourceSha256Hex">The zip's SHA-256, recorded with the import.</param>
    /// <param name="ct">Cancels the pass.</param>
    /// <returns>What the import did.</returns>
    private async Task<ConversationImportResult> StoreAsync(
        SessionStore store,
        Dictionary<string, ZipArchiveEntry> entries,
        ZipArchiveEntry turnsEntry,
        ImportPlan plan,
        ConversationImportOptions options,
        IProgress<ConversationImportProgress>? progress,
        string sourceSha256Hex,
        CancellationToken ct)
    {
        var importId = SessionArchiveIds.NewArchiveSessionId();
        var importedAt = _time.GetUtcNow();
        store.BeginImport(importId, importedAt, sourceSha256Hex, JsonSerializer.Serialize(new
        {
            timestamps = options.Timestamps.ToString(),
            on_conflict = options.OnConflict.ToString(),
            keep_local_copy = options.KeepLocalCopy,
        }));

        var offset = options.Timestamps == ImportTimestampMode.ImportTime && plan.NewestInNewSessions is { } newest
            ? importedAt - newest
            : TimeSpan.Zero;
        var mode = options.EffectiveConflictMode;
        var cache = new LocalHashCache(store);
        var added = new HashSet<Guid>();
        var skippedSessions = new HashSet<Guid>();
        int processed = 0, stored = 0, alreadyPresent = 0, conflictKept = 0, rejected = 0;
        long bodyBytes = 0;
        var buffer = new byte[CopyBufferBytes];

        foreach (var parsed in ReadLines(turnsEntry, entries, ct))
        {
            ct.ThrowIfCancellationRequested();
            using var line = parsed.Line;
            processed++;
            if (line is null)
            {
                rejected++;
                progress?.Report(new ConversationImportProgress(processed, stored));
                continue;
            }

            if (plan.SkippedSessions.Contains(line.ArchiveSessionId))
            {
                alreadyPresent++;
                skippedSessions.Add(line.ArchiveSessionId);
                progress?.Report(new ConversationImportProgress(processed, stored));
                continue;
            }

            var disposition = Classify(store, cache, line);
            if (disposition == Disposition.AlreadyPresent)
            {
                alreadyPresent++;
            }
            else if (disposition == Disposition.Conflict && mode == ImportConflictMode.Skip)
            {
                conflictKept++;
            }
            else
            {
                var isNewSession = plan.NewSessions.Contains(line.ArchiveSessionId);
                var createsSession = isNewSession && !added.Contains(line.ArchiveSessionId);
                var shift = isNewSession ? offset : TimeSpan.Zero;

                // A conflicting turn arrives under a fresh local id: the local id stays on the turn it belongs to,
                // and the zip's id is recorded as the new turn's source so a later import recognises it.
                var localId = disposition == Disposition.New ? line.ArchiveTurnId : SessionArchiveIds.NewArchiveTurnId();
                var outcome = await StoreTurnAsync(
                    store, entries, line, localId, shift, importId, importedAt, createsSession, buffer, ct).ConfigureAwait(false);

                if (outcome is null)
                {
                    rejected++;
                }
                else
                {
                    stored++;
                    bodyBytes += outcome.Value;
                    added.Add(line.ArchiveSessionId);
                    if (disposition == Disposition.Conflict && mode == ImportConflictMode.Overwrite)
                    {
                        store.RetireTurn(line.ArchiveTurnId);
                    }
                }
            }

            progress?.Report(new ConversationImportProgress(processed, stored));
        }

        var sessionsAdded = added.Count(id => plan.NewSessions.Contains(id));
        store.CompleteImport(importId, sessionsAdded, stored, alreadyPresent, rejected);
        _logger.LogInformation(
            "Imported {Turns} turns into {Sessions} new conversations; {AlreadyPresent} already present, {Rejected} rejected.",
            stored, sessionsAdded, alreadyPresent, rejected);

        return new ConversationImportResult(
            importId,
            sessionsAdded,
            skippedSessions.Count,
            stored,
            alreadyPresent,
            conflictKept,
            rejected,
            TurnsToGrade: stored,
            bodyBytes);
    }

    /// <summary>
    /// Decides what a zip turn is to the local history: already there with the same content, not there, or there
    /// with different content. A turn a past import stored under the zip's id counts as present whenever the
    /// digest the zip declared then is the one it declares now, even if obscuring changed the stored bodies.
    /// </summary>
    /// <param name="store">The store being imported into.</param>
    /// <param name="cache">The local body hashes of the session last looked at.</param>
    /// <param name="line">The zip's turn.</param>
    /// <returns>The turn's disposition.</returns>
    private static Disposition Classify(SessionStore store, LocalHashCache cache, TurnLine line)
    {
        var imported = store.ListImportedBySource(line.ArchiveTurnId);
        if (imported.Any(row => row.SourceDigest.AsSpan().SequenceEqual(line.Digest))) return Disposition.AlreadyPresent;

        var local = store.TryGetTurn(line.ArchiveTurnId);
        if (local is null) return Disposition.New;

        if (local.ArchiveSessionId != line.ArchiveSessionId) return Disposition.Conflict;

        var hashes = cache.For(local.ArchiveSessionId);
        if (hashes is null) return Disposition.Conflict;

        foreach (var kind in ExchangeKinds)
        {
            hashes.TryGetValue((line.ArchiveTurnId, kind), out var localRow);
            var localSha = localRow is { Missing: false, Sha256: { } sha } ? sha : null;
            line.Bodies.TryGetValue(kind, out var zipBody);
            var zipSha = zipBody is { Missing: false } ? zipBody.Sha256 : null;
            if (localSha is null != zipSha is null) return Disposition.Conflict;
            if (localSha is not null && !localSha.AsSpan().SequenceEqual(zipSha)) return Disposition.Conflict;
        }

        return Disposition.AlreadyPresent;
    }

    /// <summary>
    /// Reads, verifies and stores one turn. Nothing is written unless every body of the turn verifies.
    /// </summary>
    /// <param name="store">The store to add to.</param>
    /// <param name="entries">The archive's entries.</param>
    /// <param name="line">The zip's turn.</param>
    /// <param name="localId">The id to store the turn under.</param>
    /// <param name="shift">How far its timestamp moves (zero except for a session new to the history under <see cref="ImportTimestampMode.ImportTime"/>).</param>
    /// <param name="importId">The import this turn belongs to.</param>
    /// <param name="importedAt">When the import started.</param>
    /// <param name="createsSession">Whether this turn creates its session.</param>
    /// <param name="buffer">A reusable copy buffer.</param>
    /// <param name="ct">Cancels the import.</param>
    /// <returns>The plaintext body bytes stored, or <see langword="null"/> when the turn was rejected.</returns>
    private async Task<long?> StoreTurnAsync(
        SessionStore store,
        Dictionary<string, ZipArchiveEntry> entries,
        TurnLine line,
        Guid localId,
        TimeSpan shift,
        Guid importId,
        DateTimeOffset importedAt,
        bool createsSession,
        byte[] buffer,
        CancellationToken ct)
    {
        var spools = new List<SessionBodySpool>();
        try
        {
            var bodies = new List<SessionBodyInput>(ExchangeKinds.Length + 2);
            byte[]? requestBytes = null;
            byte[]? responseBytes = null;
            var responseTooLarge = false;
            long stored = 0;

            foreach (var kind in ExchangeKinds)
            {
                if (!line.Bodies.TryGetValue(kind, out var body)) continue;
                if (body.Missing)
                {
                    bodies.Add(new SessionBodyInput(kind, null));
                    continue;
                }

                var entry = entries[body.Path!];
                if (body.Length <= MaxInMemoryBodyBytes)
                {
                    var bytes = ReadVerified(entry, body, buffer, ct);
                    if (bytes is null) return Reject(line);

                    bodies.Add(new SessionBodyInput(kind, bytes));
                    if (kind == SessionBodyKind.ClientRequest) requestBytes = bytes;
                    if (kind == SessionBodyKind.ClientResponse) responseBytes = bytes;
                }
                else
                {
                    var spool = SpoolVerified(store, entry, body, buffer, ct);
                    if (spool is null) return Reject(line);

                    spools.Add(spool);
                    bodies.Add(new SessionBodyInput(kind, null, spool));
                    if (kind == SessionBodyKind.ClientResponse) responseTooLarge = true;
                }

                stored += body.Length;
            }

            var created = line.CreatedAtUtc + shift;
            var metadata = BuildMetadata(line, created, shift != TimeSpan.Zero, importId, importedAt);
            bodies.Add(new SessionBodyInput(SessionBodyKind.TurnMetadata, metadata));

            using var metadataDocument = JsonDocument.Parse(metadata);
            var (newestUserMessage, responseText, extracts) = BuildExtracts(
                metadataDocument.RootElement, requestBytes, responseBytes, responseTooLarge, line.Bodies.ContainsKey(SessionBodyKind.ClientResponse));
            bodies.Add(new SessionBodyInput(SessionBodyKind.Extracts, extracts));

            var turn = new SessionTurnInput(
                localId,
                created,
                bodies,
                SessionTurnOrigin.Imported,
                new SessionTurnImport(importId, line.ArchiveTurnId, line.Digest, createsSession));

            var row = store.AppendTurn(line.ArchiveSessionId, line.ClientSessionId, turn);

            if (_sink is not null)
            {
                try
                {
                    await _sink.TurnStoredAsync(
                        new ImportedTurn(
                            line.ArchiveSessionId, localId, line.ClientSessionId, row.TurnSequence, created,
                            metadataDocument.RootElement.Clone(), newestUserMessage, responseText),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The turn is stored; only its learning row is missing. Logged, never fatal to the import.
                    _logger.LogWarning(ex, "An imported turn's transcript row could not be written.");
                }
            }

            return stored;
        }
        finally
        {
            foreach (var spool in spools) spool.Dispose();
        }
    }

    /// <summary>Notes a rejected turn in the log and yields the rejection marker.</summary>
    /// <param name="line">The rejected turn.</param>
    /// <returns><see langword="null"/>, the marker <see cref="StoreTurnAsync"/> returns for a rejection.</returns>
    private long? Reject(TurnLine line)
    {
        _logger.LogWarning("An imported turn failed verification and was left out: {ArchiveTurnId}.", line.ArchiveTurnId);
        return null;
    }

    /// <summary>
    /// Reads a body into memory through the length guard and checks its hash.
    /// </summary>
    /// <param name="entry">The body's entry.</param>
    /// <param name="body">What the line declares for it.</param>
    /// <param name="buffer">A reusable copy buffer.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The body's bytes, or <see langword="null"/> when it inflates past its declared length, falls short, or does not match its hash.</returns>
    private static byte[]? ReadVerified(ZipArchiveEntry entry, LineBody body, byte[] buffer, CancellationToken ct)
    {
        var result = new byte[body.Length];
        using var stream = entry.Open();
        int filled = 0;
        while (filled < result.Length)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(result, filled, result.Length - filled);
            if (read == 0) return null;
            filled += read;
        }

        // One more read: a body that inflates past what the line declared is a compression bomb or a lying line.
        if (stream.Read(buffer, 0, 1) != 0) return null;

        return SHA256.HashData(result).AsSpan().SequenceEqual(body.Sha256) ? result : null;
    }

    /// <summary>
    /// Streams a large body into a spool through the length guard, checking its hash as it goes, so memory stays
    /// bounded however large the body is. The spool obscures secrets on the way in.
    /// </summary>
    /// <param name="store">The store, whose folder holds the spool.</param>
    /// <param name="entry">The body's entry.</param>
    /// <param name="body">What the line declares for it.</param>
    /// <param name="buffer">A reusable copy buffer.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>A completed spool, or <see langword="null"/> when the body is longer or shorter than declared or does not match its hash.</returns>
    /// <exception cref="IOException">When the spool cannot be written (the disk is full).</exception>
    private static SessionBodySpool? SpoolVerified(
        SessionStore store, ZipArchiveEntry entry, LineBody body, byte[] buffer, CancellationToken ct)
    {
        var spool = SessionBodySpool.Create(store.Folder);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = entry.Open();
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += read;
                if (total > body.Length)
                {
                    spool.Dispose();
                    return null;
                }

                hash.AppendData(buffer, 0, read);
                if (!spool.TryWrite(buffer.AsSpan(0, read))) throw new IOException("The import could not spool a large body to disk.");
            }

            if (total != body.Length || !hash.GetHashAndReset().AsSpan().SequenceEqual(body.Sha256))
            {
                spool.Dispose();
                return null;
            }

            if (!spool.TryComplete()) throw new IOException("The import could not finish spooling a large body to disk.");
            return spool;
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the metadata snapshot stored with an imported turn: the zip's snapshot with the import's own fields
    /// added, so every key the exporter wrote survives and a future one is not lost.
    /// </summary>
    /// <param name="line">The zip's turn.</param>
    /// <param name="created">The timestamp the turn is stored with.</param>
    /// <param name="shifted">Whether <paramref name="created"/> differs from the zip's timestamp.</param>
    /// <param name="importId">The import that stores the turn.</param>
    /// <param name="importedAt">When the import started.</param>
    /// <returns>The snapshot as UTF-8 JSON.</returns>
    private static byte[] BuildMetadata(TurnLine line, DateTimeOffset created, bool shifted, Guid importId, DateTimeOffset importedAt)
    {
        var snapshot = line.MetadataJson is not null && JsonNode.Parse(line.MetadataJson) is JsonObject existing
            ? existing
            : new JsonObject
            {
                ["session_id"] = line.ClientSessionId,
                ["turn_number"] = line.TurnSequence,
            };

        snapshot["created_at_utc"] = created.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (shifted)
        {
            snapshot["original_created_at_utc"] = line.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        snapshot["origin"] = SessionTurnOrigin.Imported;
        snapshot["imported_at_utc"] = importedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        snapshot["import_id"] = importId.ToString("D");
        snapshot["source_archive_turn_id"] = line.ArchiveTurnId.ToString("D");
        return JsonSerializer.SerializeToUtf8Bytes(snapshot);
    }

    /// <summary>
    /// Derives the text extracts the zip does not carry from the stored bodies, the way capture does: the newest
    /// user message from the request, the reply from the response. A body too large to hold in memory yields no
    /// text from it, and the reply is flagged as not extracted rather than stored as if whole.
    /// </summary>
    /// <param name="metadata">The turn's metadata, for the provider and streaming flag.</param>
    /// <param name="requestBytes">The client request, or <see langword="null"/> when absent or too large.</param>
    /// <param name="responseBytes">The client response, or <see langword="null"/> when absent or too large.</param>
    /// <param name="responseTooLarge">Whether the response was too large to extract from.</param>
    /// <param name="responseExpected">Whether the turn has a client response at all.</param>
    /// <returns>The two texts and the Extracts JSON.</returns>
    private (string? NewestUserMessage, string? ResponseText, byte[] Json) BuildExtracts(
        JsonElement metadata, byte[]? requestBytes, byte[]? responseBytes, bool responseTooLarge, bool responseExpected)
    {
        string? newest = null;
        if (requestBytes is not null)
        {
            try
            {
                newest = RequestTextExtractor.ExtractNewestUserMessage(JsonNode.Parse(requestBytes) as JsonObject);
            }
            catch (JsonException)
            {
                // A request that is not JSON has no message to extract.
            }
        }

        string? reply = null;
        if (responseBytes is not null)
        {
            var provider = metadata.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var streaming = metadata.TryGetProperty("streaming", out var s) && s.ValueKind == JsonValueKind.True;
            var candidates = new List<string>(ResponseShapeFallbacks.Length + 1);
            if (!string.IsNullOrEmpty(provider)) candidates.Add(provider);
            candidates.AddRange(ResponseShapeFallbacks);
            foreach (var candidate in candidates)
            {
                if (_responseTextExtractor.TryExtractText(candidate, streaming, responseBytes, out var text))
                {
                    reply = text;
                    break;
                }
            }
        }

        var truncated = responseTooLarge || (responseExpected && responseBytes is null && !responseTooLarge);
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["newest_user_message"] = newest,
            ["response_text"] = reply,
            ["response_text_truncated"] = truncated,
        });
        return (newest, reply, json);
    }

    /// <summary>
    /// Yields the lines of <c>turns.jsonl</c> as parsed turns. A line that cannot be read yields a rejection (a
    /// <see langword="null"/> turn) and the enumeration continues, so one bad line never aborts the rest.
    /// </summary>
    /// <param name="turnsEntry">The entry.</param>
    /// <param name="entries">The archive's entries, to check body paths against.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>One item per non-blank line.</returns>
    private static IEnumerable<ParsedLine> ReadLines(
        ZipArchiveEntry turnsEntry, Dictionary<string, ZipArchiveEntry> entries, CancellationToken ct)
    {
        using var stream = turnsEntry.Open();
        var seen = new HashSet<Guid>();
        foreach (var bytes in SplitLines(stream, ct))
        {
            var line = ParseLine(bytes, entries);
            if (line is not null && !seen.Add(line.ArchiveTurnId))
            {
                // The same turn twice in one zip: the second adds nothing.
                line.Dispose();
                line = null;
            }

            yield return new ParsedLine(line);
        }
    }

    /// <summary>Reports whether a line holds anything but spaces, tabs and carriage returns.</summary>
    /// <param name="line">The line's bytes.</param>
    /// <returns><see langword="true"/> when the line is not blank.</returns>
    private static bool HasContent(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
        {
            if (b is not ((byte)' ' or (byte)'\r' or (byte)'\t')) return true;
        }

        return false;
    }

    /// <summary>Splits a stream into non-blank lines, refusing a line longer than <see cref="MaxLineBytes"/>.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Each line's bytes, without its line break.</returns>
    /// <exception cref="ConversationImportException">When a line exceeds the limit.</exception>
    private static IEnumerable<byte[]> SplitLines(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferBytes];
        using var current = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;

                current.Write(buffer, start, i - start);
                start = i + 1;
                var line = current.ToArray();
                current.SetLength(0);
                if (HasContent(line)) yield return line;
            }

            current.Write(buffer, start, read - start);
            if (current.Length > MaxLineBytes) throw Invalid("turns.jsonl has a line longer than this router reads.");
        }

        if (current.Length > 0)
        {
            var last = current.ToArray();
            if (HasContent(last)) yield return last;
        }
    }

    /// <summary>
    /// Parses one <c>turns.jsonl</c> line and checks it: the required fields are present, unknown fields are
    /// ignored, and every body path stays under the turn's own directory and names an entry whose size matches.
    /// </summary>
    /// <param name="bytes">The line.</param>
    /// <param name="entries">The archive's entries.</param>
    /// <returns>The turn, or <see langword="null"/> when the line must be rejected.</returns>
    private static TurnLine? ParseLine(byte[] bytes, Dictionary<string, ZipArchiveEntry> entries)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }

        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGuid(root, "archive_turn_id", out var turnId)
                || !TryGuid(root, "archive_session_id", out var sessionId)
                || !root.TryGetProperty("client_session_id", out var client) || client.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(client.GetString())
                || !root.TryGetProperty("turn_sequence", out var sequence) || !sequence.TryGetInt32(out var turnSequence) || turnSequence < 0
                || !root.TryGetProperty("created_at_utc", out var createdText) || createdText.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(createdText.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
            {
                document.Dispose();
                return null;
            }

            var bodies = new Dictionary<SessionBodyKind, LineBody>();
            if (root.TryGetProperty("bodies", out var bodiesElement) && bodiesElement.ValueKind == JsonValueKind.Object)
            {
                var prefix = $"conversations/{sessionId:D}/turns/";
                foreach (var kind in ExchangeKinds)
                {
                    if (!bodiesElement.TryGetProperty(PropertyName(kind), out var bodyElement)) continue;
                    if (bodyElement.ValueKind != JsonValueKind.Object)
                    {
                        document.Dispose();
                        return null;
                    }

                    if (bodyElement.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.True)
                    {
                        bodies[kind] = new LineBody(null, null, 0, Missing: true);
                        continue;
                    }

                    if (!bodyElement.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                        || path.GetString() is not { } pathText || !pathText.StartsWith(prefix, StringComparison.Ordinal)
                        || !IsSafeEntryName(pathText)
                        || !TryHex(bodyElement, "sha256", out var sha)
                        || !bodyElement.TryGetProperty("length", out var lengthElement) || !lengthElement.TryGetInt64(out var length) || length < 0
                        || !entries.TryGetValue(pathText, out var entry) || entry.Length != length)
                    {
                        document.Dispose();
                        return null;
                    }

                    bodies[kind] = new LineBody(pathText, sha, length, Missing: false);
                }
            }

            var metadataJson = root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                ? metadata.GetRawText()
                : null;
            var digest = SourceDigest(
                turnId,
                bodies.Where(pair => !pair.Value.Missing).ToDictionary(pair => pair.Key, pair => pair.Value.Sha256!));
            return new TurnLine(
                document, turnId, sessionId, client.GetString()!, turnSequence, created.ToUniversalTime(), metadataJson, bodies, digest);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>Names an exchange kind as a <c>turns.jsonl</c> body property.</summary>
    /// <param name="kind">The exchange body kind.</param>
    /// <returns>The property name under <c>bodies</c>.</returns>
    private static string PropertyName(SessionBodyKind kind) => kind switch
    {
        SessionBodyKind.ClientRequest => "request",
        SessionBodyKind.ClientResponse => "response",
        SessionBodyKind.ProviderRequest => "provider_request",
        SessionBodyKind.ProviderResponse => "provider_response",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an exchange body."),
    };

    /// <summary>Reads a GUID-valued string property.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The parsed GUID.</param>
    /// <returns><see langword="true"/> when the property is a GUID string.</returns>
    private static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
               && Guid.TryParse(property.GetString(), out value);
    }

    /// <summary>Reads a 32-byte SHA-256 given as 64 hex digits.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The decoded hash.</param>
    /// <returns><see langword="true"/> when the property is a valid SHA-256 hex string.</returns>
    private static bool TryHex(JsonElement element, string name, out byte[] value)
    {
        value = [];
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;

        var text = property.GetString();
        if (text is not { Length: HashLength * 2 }) return false;

        try
        {
            value = Convert.FromHexString(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Copies a stream into another, refusing more than a limit.</summary>
    /// <param name="source">The stream to read.</param>
    /// <param name="destination">The stream to write.</param>
    /// <param name="limit">The most bytes to accept.</param>
    /// <param name="ct">Cancels the copy.</param>
    /// <exception cref="ConversationImportException">When the source exceeds <paramref name="limit"/>.</exception>
    private static void CopyBounded(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += read;
            if (total > limit) throw Invalid("An archive entry is larger than this router reads.");
            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>Hashes a file from the start, for the import record.</summary>
    /// <param name="file">The open archive file.</param>
    /// <param name="ct">Cancels the hash.</param>
    /// <returns>The lowercase hex SHA-256.</returns>
    private static string HashFile(FileStream file, CancellationToken ct)
    {
        file.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Builds the exception for an archive that breaks the format.</summary>
    /// <param name="message">A message that names the rule without echoing archive content.</param>
    /// <returns>The exception to throw.</returns>
    private static ConversationImportException Invalid(string message) =>
        new(ConversationImportFailure.InvalidArchive, message);

    /// <summary>What a zip turn is to the local history.</summary>
    private enum Disposition
    {
        /// <summary>The turn is not in the history.</summary>
        New,

        /// <summary>The turn is in the history with the same content.</summary>
        AlreadyPresent,

        /// <summary>The turn is in the history with different content.</summary>
        Conflict,
    }

    /// <summary>The manifest values an import needs.</summary>
    /// <param name="TurnsSha256">The declared hash of <c>turns.jsonl</c>.</param>
    /// <param name="TurnsLength">The declared length of <c>turns.jsonl</c>.</param>
    /// <param name="DeclaredBytes">The declared total of exchange-body bytes, or zero when absent.</param>
    /// <param name="SessionChecksums">Each session's declared checksum, with <see langword="null"/> for one the exporter could not compute.</param>
    private sealed record ImportManifest(
        byte[] TurnsSha256, long TurnsLength, long DeclaredBytes, Dictionary<Guid, byte[]?> SessionChecksums);

    /// <summary>What the first pass found.</summary>
    /// <param name="SkippedSessions">Sessions whose checksum equals the manifest's.</param>
    /// <param name="NewSessions">Sessions the history does not have.</param>
    /// <param name="BytesToStore">The sum of declared body lengths the second pass will store.</param>
    /// <param name="NewestInNewSessions">The newest timestamp among the turns to store in new sessions, which <see cref="ImportTimestampMode.ImportTime"/> moves to the import time.</param>
    private sealed record ImportPlan(
        HashSet<Guid> SkippedSessions, HashSet<Guid> NewSessions, long BytesToStore, DateTimeOffset? NewestInNewSessions);

    /// <summary>One body as a line declares it.</summary>
    /// <param name="Path">The entry path, or <see langword="null"/> for a missing body.</param>
    /// <param name="Sha256">The declared hash, or <see langword="null"/> for a missing body.</param>
    /// <param name="Length">The declared length.</param>
    /// <param name="Missing">Whether the exporter recorded the body as missing.</param>
    private sealed record LineBody(string? Path, byte[]? Sha256, long Length, bool Missing);

    /// <summary>One item of <c>turns.jsonl</c>: a turn, or <see langword="null"/> for a line that was rejected.</summary>
    /// <param name="Line">The turn, owned by the caller.</param>
    private readonly record struct ParsedLine(TurnLine? Line);

    /// <summary>One parsed, checked line of <c>turns.jsonl</c>. Disposing it releases the parsed document.</summary>
    private sealed class TurnLine : IDisposable
    {
        private readonly JsonDocument _document;

        /// <summary>Initializes a new instance of the <see cref="TurnLine"/> class.</summary>
        /// <param name="document">The parsed line, released with this object.</param>
        /// <param name="archiveTurnId">The turn's id in the zip.</param>
        /// <param name="archiveSessionId">The session's id in the zip.</param>
        /// <param name="clientSessionId">The client's session id.</param>
        /// <param name="turnSequence">The turn's position in its source session.</param>
        /// <param name="createdAtUtc">The turn's original timestamp.</param>
        /// <param name="metadataJson">The metadata snapshot as JSON text, or <see langword="null"/> when the line has none.</param>
        /// <param name="bodies">The exchange bodies the line declares.</param>
        /// <param name="digest">The turn's source digest.</param>
        public TurnLine(
            JsonDocument document,
            Guid archiveTurnId,
            Guid archiveSessionId,
            string clientSessionId,
            int turnSequence,
            DateTimeOffset createdAtUtc,
            string? metadataJson,
            Dictionary<SessionBodyKind, LineBody> bodies,
            byte[] digest)
        {
            _document = document;
            ArchiveTurnId = archiveTurnId;
            ArchiveSessionId = archiveSessionId;
            ClientSessionId = clientSessionId;
            TurnSequence = turnSequence;
            CreatedAtUtc = createdAtUtc;
            MetadataJson = metadataJson;
            Bodies = bodies;
            Digest = digest;
        }

        /// <summary>Gets the turn's id in the zip.</summary>
        public Guid ArchiveTurnId { get; }

        /// <summary>Gets the session's id in the zip.</summary>
        public Guid ArchiveSessionId { get; }

        /// <summary>Gets the client's session id.</summary>
        public string ClientSessionId { get; }

        /// <summary>Gets the turn's position in its source session.</summary>
        public int TurnSequence { get; }

        /// <summary>Gets the turn's original timestamp.</summary>
        public DateTimeOffset CreatedAtUtc { get; }

        /// <summary>Gets the metadata snapshot as JSON text, or <see langword="null"/>.</summary>
        public string? MetadataJson { get; }

        /// <summary>Gets the exchange bodies the line declares.</summary>
        public Dictionary<SessionBodyKind, LineBody> Bodies { get; }

        /// <summary>Gets the turn's source digest.</summary>
        public byte[] Digest { get; }

        /// <summary>Gets the sum of the declared lengths of the turn's stored bodies.</summary>
        public long DeclaredBytes => Bodies.Values.Where(body => !body.Missing).Sum(body => body.Length);

        /// <inheritdoc/>
        public void Dispose() => _document.Dispose();
    }

    /// <summary>
    /// The body hashes of the session last looked at, read once for as long as the import stays in that session.
    /// A session captured before hashes existed is backfilled first.
    /// </summary>
    private sealed class LocalHashCache(SessionStore store)
    {
        private Guid _sessionId;
        private Dictionary<(Guid, SessionBodyKind), SessionBodyHashRow>? _hashes;

        /// <summary>Gets a session's body hashes by turn and kind.</summary>
        /// <param name="archiveSessionId">The session.</param>
        /// <returns>The hashes, or <see langword="null"/> when the session cannot be hashed.</returns>
        public Dictionary<(Guid, SessionBodyKind), SessionBodyHashRow>? For(Guid archiveSessionId)
        {
            if (_hashes is not null && _sessionId == archiveSessionId) return _hashes;

            _sessionId = archiveSessionId;
            _hashes = store.HasBodyHashes(archiveSessionId) || store.BackfillBodyHashes(archiveSessionId)
                ? store.ListBodyHashes(archiveSessionId).ToDictionary(row => (row.ArchiveTurnId, row.Kind))
                : null;
            return _hashes;
        }
    }
}
