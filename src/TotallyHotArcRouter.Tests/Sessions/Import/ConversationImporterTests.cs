using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Sessions.Export;
using TotallyHot.ArcRouter.Sessions.Import;

namespace TotallyHot.ArcRouter.Tests.Sessions.Import;

/// <summary>
/// Pins the conversation import of #165 phase 4 (PR 4a): the round trip, idempotent re-import that reads no
/// body, tamper and zip-slip rejection, the version and disk-budget refusals, both timestamp modes, partly
/// present sessions, the conflict modes, obscuring on write, large spooled bodies and single flight.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class ConversationImporterTests : IDisposable
{
    private const string PlantedSecret = "ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

    private static readonly DateTimeOffset Epoch = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Request = """{"messages":[{"role":"user","content":"hi there"}]}"""u8.ToArray();
    private static readonly byte[] Response = """{"choices":[{"message":{"role":"assistant","content":"hello"}}]}"""u8.ToArray();

    private readonly SessionTestStore _source = new();
    private readonly SessionTestStore _target = new();
    private readonly string _work = Path.Combine(TestScratchDirectory.RunRoot, "import-work-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the folder the zips are written to, outside both stores' data directories.</summary>
    public ConversationImporterTests()
    {
        Directory.CreateDirectory(_work);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Scratch cleanup is also done by TestTempDirectorySweeper.
        }
    }

    /// <summary>Importing an export into an empty store reproduces the source: bodies, checksums, origin and extracts.</summary>
    [Fact]
    public async Task Import_IntoAnEmptyStore_ReproducesTheSource()
    {
        var corpus = Seed();
        var zip = await ExportAsync();

        var result = await new ConversationImporter().ImportAsync(
            _target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(corpus.Turns.Count, result.TurnsAdded);
        Assert.Equal(corpus.Sessions.Count, result.SessionsAdded);
        Assert.Equal(0, result.TurnsRejected);
        Assert.Equal(result.TurnsAdded, result.TurnsToGrade);
        foreach (var session in corpus.Sessions)
        {
            Assert.Equal(_source.Store.GetSessionSha256(session), _target.Store.GetSessionSha256(session));
            var sourceTurns = _source.Store.ListTurns(session);
            var targetTurns = _target.Store.ListTurns(session);
            Assert.Equal(sourceTurns.Select(t => t.ArchiveTurnId), targetTurns.Select(t => t.ArchiveTurnId));
            Assert.All(targetTurns, t => Assert.Equal(SessionTurnOrigin.Imported, t.Origin));
            Assert.Equal(sourceTurns.Select(t => t.CreatedAtUtc), targetTurns.Select(t => t.CreatedAtUtc));

            var kinds = new HashSet<SessionBodyKind>
            {
                SessionBodyKind.ClientRequest, SessionBodyKind.ClientResponse,
                SessionBodyKind.ProviderRequest, SessionBodyKind.ProviderResponse,
            };
            var ids = sourceTurns.Select(t => t.ArchiveTurnId).ToList();
            var expected = _source.Store.TryReadBodies(session, kinds, ids);
            var actual = _target.Store.TryReadBodies(session, kinds, ids);
            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            foreach (var key in expected.Keys) Assert.Equal(expected[key], actual[key]);
        }

        // The extracts the zip does not carry are regenerated from the stored bodies.
        var first = corpus.Turns[0];
        var extracts = _target.Store.TryReadExtracts(first.Session, first.TurnId);
        Assert.Equal("hi there", extracts?.NewestUserMessage);
        Assert.Equal("hello", extracts?.ResponseText);
    }

    /// <summary>A second import adds nothing, and a body that would fail verification is never opened for a session it skips.</summary>
    [Fact]
    public async Task Import_Twice_AddsNothing_AndReadsNoBodyOfASkippedSession()
    {
        Seed();
        var zip = await ExportAsync();
        var importer = new ConversationImporter();
        await importer.ImportAsync(_target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None);
        var rowsBefore = _target.Scalar("SELECT COUNT(*) FROM session_turns");

        // Corrupt a body without fixing any hash (same length, so only a read can notice): reading it would
        // reject its turn.
        var corrupted = Path.Combine(_work, "corrupted.zip");
        var bodyPath = FirstBodyPath(zip);
        var flipped = ReadAll(zip)[bodyPath];
        flipped[^1] ^= 0x01;
        ReplaceBody(zip, corrupted, bodyPath, flipped, fixHashes: false);

        var again = await importer.ImportAsync(
            _target.Store, corrupted, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(0, again.TurnsAdded);
        Assert.Equal(0, again.TurnsRejected);
        Assert.Equal(0, again.BodyBytes);
        Assert.True(again.SessionsSkipped > 0);
        Assert.Equal(rowsBefore, _target.Scalar("SELECT COUNT(*) FROM session_turns"));
    }

    /// <summary>A tampered body rejects its own turn only; the rest of the archive still imports.</summary>
    [Fact]
    public async Task Import_ATamperedBody_RejectsThatTurnOnly()
    {
        var corpus = Seed();
        var zip = await ExportAsync();
        var tampered = Path.Combine(_work, "tampered.zip");
        var victim = corpus.Turns[0];
        ReplaceBody(zip, tampered, BodyPathOf(zip, victim.TurnId, "request"), "{\"tampered\":true}"u8.ToArray(), fixHashes: false);

        var result = await new ConversationImporter().ImportAsync(
            _target.Store, tampered, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(1, result.TurnsRejected);
        Assert.Equal(corpus.Turns.Count - 1, result.TurnsAdded);
        Assert.Null(_target.Store.TryGetTurn(victim.TurnId));
    }

    /// <summary>An entry name that climbs out of the archive fails the whole import before anything is stored.</summary>
    [Fact]
    public async Task Import_AZipSlipEntryName_IsRefused()
    {
        var path = Path.Combine(_work, "slip.zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("../evil.txt").Open();
            entry.Write("x"u8);
        }

        var ex = await Assert.ThrowsAsync<ConversationImportException>(() => new ConversationImporter().ImportAsync(
            _target.Store, path, new ConversationImportOptions(), null, CancellationToken.None));

        Assert.Equal(ConversationImportFailure.InvalidArchive, ex.Reason);
        Assert.Equal(0, _target.Scalar("SELECT COUNT(*) FROM session_files"));
    }

    /// <summary>A schema version this router does not read is refused with a message that names the version.</summary>
    [Fact]
    public async Task Import_AnotherSchemaVersion_IsRefusedByName()
    {
        Seed();
        var zip = await ExportAsync();
        var edited = Path.Combine(_work, "v2.zip");
        EditManifest(zip, edited, manifest => manifest["schema_version"] = 2);

        var ex = await Assert.ThrowsAsync<ConversationImportException>(() => new ConversationImporter().ImportAsync(
            _target.Store, edited, new ConversationImportOptions(), null, CancellationToken.None));

        Assert.Equal(ConversationImportFailure.UnsupportedVersion, ex.Reason);
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A turns.jsonl whose hash differs from the manifest's is refused.</summary>
    [Fact]
    public async Task Import_ATurnsFileThatDoesNotMatchTheManifest_IsRefused()
    {
        Seed();
        var zip = await ExportAsync();
        var edited = Path.Combine(_work, "badhash.zip");
        EditManifest(zip, edited, manifest => manifest["turns_jsonl"]!["sha256"] = new string('0', 64));

        var ex = await Assert.ThrowsAsync<ConversationImportException>(() => new ConversationImporter().ImportAsync(
            _target.Store, edited, new ConversationImportOptions(), null, CancellationToken.None));

        Assert.Equal(ConversationImportFailure.InvalidArchive, ex.Reason);
    }

    /// <summary>An import that would not leave the reserve free is refused before it writes anything.</summary>
    [Fact]
    public async Task Import_WithTooLittleFreeSpace_IsRefusedBeforeAnythingIsWritten()
    {
        Seed();
        var zip = await ExportAsync();
        var importer = new ConversationImporter(volumeSpace: _ => (FreeBytes: 1024, TotalBytes: 1L << 40));

        var ex = await Assert.ThrowsAsync<ConversationImportException>(() => importer.ImportAsync(
            _target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None));

        Assert.Equal(ConversationImportFailure.InsufficientDiskSpace, ex.Reason);
        Assert.Equal(0, _target.Scalar("SELECT COUNT(*) FROM session_files"));
        Assert.Equal(0, _target.Scalar("SELECT COUNT(*) FROM session_imports"));
    }

    /// <summary>A session that is partly present gains exactly its missing turns, and a turn present under another import's obscuring is still found.</summary>
    [Fact]
    public async Task Import_APartlyPresentSession_GainsExactlyItsMissingTurns()
    {
        var corpus = Seed();
        var partial = await ExportAsync(new ConversationExportFilter(From: Epoch, To: Epoch.AddHours(1)), "partial.zip");
        var full = await ExportAsync(path: "full.zip");
        var importer = new ConversationImporter();
        var first = await importer.ImportAsync(_target.Store, partial, new ConversationImportOptions(), null, CancellationToken.None);

        var second = await importer.ImportAsync(_target.Store, full, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(corpus.Turns.Count - first.TurnsAdded, second.TurnsAdded);
        Assert.Equal(first.TurnsAdded, second.TurnsAlreadyPresent);
        Assert.Equal(corpus.Turns.Count, _target.Scalar("SELECT COUNT(*) FROM session_turns"));
    }

    /// <summary>Under <c>import-time</c> the newest imported turn lands at the import time and order and spacing are unchanged.</summary>
    [Fact]
    public async Task Import_ImportTime_ShiftsEveryTimestampByOneOffset_AndKeepsTheOriginal()
    {
        var corpus = Seed();
        var zip = await ExportAsync();
        var now = Epoch.AddYears(1);
        var importer = new ConversationImporter(timeProvider: new FixedTime(now));

        await importer.ImportAsync(
            _target.Store, zip, new ConversationImportOptions(ImportTimestampMode.ImportTime), null, CancellationToken.None);

        var source = corpus.Sessions.SelectMany(s => _source.Store.ListTurns(s)).OrderBy(t => t.CreatedAtUtc).ToList();
        var target = source.Select(t => _target.Store.TryGetTurn(t.ArchiveTurnId)!).ToList();
        var offset = now - source.Max(t => t.CreatedAtUtc);
        for (var i = 0; i < source.Count; i++)
        {
            Assert.Equal(source[i].CreatedAtUtc + offset, target[i].CreatedAtUtc);
        }

        Assert.Equal(now, target.Max(t => t.CreatedAtUtc));
        var metadata = _target.Store.TryReadBodies(
            target[0].ArchiveSessionId, new HashSet<SessionBodyKind> { SessionBodyKind.TurnMetadata }, [target[0].ArchiveTurnId]);
        using var document = JsonDocument.Parse(metadata[(target[0].ArchiveTurnId, SessionBodyKind.TurnMetadata)]);
        Assert.Equal(
            source[0].CreatedAtUtc, document.RootElement.GetProperty("original_created_at_utc").GetDateTimeOffset());

        // The checksum covers content only, so shifting timestamps does not change it.
        foreach (var session in corpus.Sessions) Assert.Equal(_source.Store.GetSessionSha256(session), _target.Store.GetSessionSha256(session));
    }

    /// <summary>A secret planted in a hand-edited zip is obscured on write, and importing the same zip again is still a no-op.</summary>
    [Fact]
    public async Task Import_ObscuresSecretsOnWrite_AndStillRecognisesTheTurnLater()
    {
        var corpus = Seed();
        var zip = await ExportAsync();
        var victim = corpus.Turns[0];
        var planted = Path.Combine(_work, "planted.zip");
        var dirty = Encoding.UTF8.GetBytes($$"""{"messages":[{"role":"user","content":"key {{PlantedSecret}} done"}]}""");
        ReplaceBody(zip, planted, BodyPathOf(zip, victim.TurnId, "request"), dirty, fixHashes: true);
        var importer = new ConversationImporter();

        var first = await importer.ImportAsync(_target.Store, planted, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(0, first.TurnsRejected);
        var stored = _target.Store.TryReadBodies(
            victim.Session, new HashSet<SessionBodyKind> { SessionBodyKind.ClientRequest }, [victim.TurnId]);
        Assert.DoesNotContain(PlantedSecret, Encoding.UTF8.GetString(stored[(victim.TurnId, SessionBodyKind.ClientRequest)]), StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedSecret, File.ReadAllText(_target.FileOf(victim.Session), Encoding.Latin1), StringComparison.Ordinal);

        var second = await importer.ImportAsync(_target.Store, planted, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(0, second.TurnsAdded);
        Assert.Equal(0, second.TurnsRejected);
    }

    /// <summary>A body larger than the in-memory limit is spooled, verified and stored whole.</summary>
    [Fact]
    public async Task Import_ALargeBody_IsSpooledAndStoredWhole()
    {
        var session = SessionArchiveIds.NewArchiveSessionId();
        var turn = SessionArchiveIds.NewArchiveTurnId();
        var big = Encoding.UTF8.GetBytes("{\"data\":\"" + string.Concat(Enumerable.Repeat("hello world, ", 450_000)) + "\"}");
        Assert.True(big.Length > ConversationImporter.MaxInMemoryBodyBytes);
        _source.Store.AppendTurn(session, "client-big", Turn(turn, Epoch, Request, big));
        var zip = await ExportAsync();

        var result = await new ConversationImporter().ImportAsync(
            _target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(1, result.TurnsAdded);
        var stored = _target.Store.TryReadBodies(session, new HashSet<SessionBodyKind> { SessionBodyKind.ClientResponse }, [turn]);
        Assert.Equal(big, stored[(turn, SessionBodyKind.ClientResponse)]);
        Assert.Empty(Directory.GetFiles(_target.Folder, "*" + SessionBodySpool.FileExtension));
    }

    /// <summary>With the local copy kept (the default) a conflicting turn is left alone and counted.</summary>
    [Fact]
    public async Task Conflict_KeepLocalCopy_LeavesTheLocalTurn()
    {
        var corpus = Seed();
        var clash = SeedConflictInTarget(corpus.Turns[3]);
        var zip = await ExportAsync();

        var result = await new ConversationImporter().ImportAsync(
            _target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(1, result.TurnsConflictKept);
        var kept = _target.Store.TryReadBodies(
            clash.Session, new HashSet<SessionBodyKind> { SessionBodyKind.ClientResponse }, [clash.TurnId]);
        Assert.Equal("{\"local\":true}"u8.ToArray(), kept[(clash.TurnId, SessionBodyKind.ClientResponse)]);
    }

    /// <summary><c>keep-both</c> stores the zip's version beside the local one, and a second import does not add a third.</summary>
    [Fact]
    public async Task Conflict_KeepBoth_StoresBesideTheLocalTurn_AndIsIdempotent()
    {
        var corpus = Seed();
        var clash = SeedConflictInTarget(corpus.Turns[3]);
        var zip = await ExportAsync();
        var options = new ConversationImportOptions(OnConflict: ImportConflictMode.KeepBoth, KeepLocalCopy: false);
        var importer = new ConversationImporter();

        var first = await importer.ImportAsync(_target.Store, zip, options, null, CancellationToken.None);
        var liveAfterFirst = _target.Store.ListTurns(clash.Session).Count;
        var second = await importer.ImportAsync(_target.Store, zip, options, null, CancellationToken.None);

        Assert.Equal(0, first.TurnsConflictKept);
        Assert.Equal(2, liveAfterFirst);
        Assert.Equal(0, second.TurnsAdded);
        Assert.Equal(liveAfterFirst, _target.Store.ListTurns(clash.Session).Count);
        Assert.Equal(1, _target.Scalar(
            $"SELECT COUNT(*) FROM session_import_turns WHERE source_archive_turn_id = '{clash.TurnId:D}'"));
    }

    /// <summary><c>overwrite</c> replaces the local turn: the replacement is live, the old one leaves every read, and the checksum follows.</summary>
    [Fact]
    public async Task Conflict_Overwrite_RetiresTheLocalTurn_AndFollowsTheSource()
    {
        var corpus = Seed();
        var clash = SeedConflictInTarget(corpus.Turns[3]);
        var zip = await ExportAsync();
        var options = new ConversationImportOptions(OnConflict: ImportConflictMode.Overwrite, KeepLocalCopy: false);
        var importer = new ConversationImporter();
        var before = _target.Store.GetSessionSha256(clash.Session);

        await importer.ImportAsync(_target.Store, zip, options, null, CancellationToken.None);

        var live = _target.Store.ListTurns(clash.Session);
        Assert.DoesNotContain(live, t => t.ArchiveTurnId == clash.TurnId);
        Assert.Single(live);
        var replacement = live[0].ArchiveTurnId;
        var bodies = _target.Store.TryReadBodies(
            clash.Session, new HashSet<SessionBodyKind> { SessionBodyKind.ClientResponse }, [replacement]);
        Assert.Equal(Response, bodies[(replacement, SessionBodyKind.ClientResponse)]);
        Assert.NotEqual(before, _target.Store.GetSessionSha256(clash.Session));

        var again = await importer.ImportAsync(_target.Store, zip, options, null, CancellationToken.None);
        Assert.Equal(0, again.TurnsAdded);
        Assert.Single(_target.Store.ListTurns(clash.Session));
    }

    /// <summary>The result says how many turns the next retention pass would delete, so an old import is not silently lost.</summary>
    [Fact]
    public async Task Import_ReportsTheTurnsTheNextRetentionPassWouldDelete()
    {
        Seed();
        var zip = await ExportAsync();

        var result = await new ConversationImporter().ImportAsync(
            _target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None, sampleSize: 2);

        // Four turns against a limit of two: the older three-turn session goes, the newest session stays.
        Assert.Equal(3, result.TurnsDueForRetention);
    }

    /// <summary>
    /// A session an import created carries a client id from another machine. Live traffic that uses the same id
    /// must start its own session, not be appended to the imported one.
    /// </summary>
    [Fact]
    public async Task LiveTrafficWithAnImportedSessionsClientId_StartsItsOwnSession()
    {
        var corpus = Seed();
        var zip = await ExportAsync();
        await new ConversationImporter().ImportAsync(
            _target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None);

        var live = _target.Store.ResolveArchiveSessionId("client-a");

        Assert.DoesNotContain(live, corpus.Sessions);
    }

    /// <summary>The sink hears about each stored turn once, with the regenerated text.</summary>
    [Fact]
    public async Task Import_TellsTheSinkAboutEachStoredTurn()
    {
        var corpus = Seed();
        var zip = await ExportAsync();
        var sink = new RecordingSink();

        await new ConversationImporter(sink).ImportAsync(
            _target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None);

        Assert.Equal(corpus.Turns.Count, sink.Turns.Count);
        Assert.Contains(sink.Turns, t => t.NewestUserMessage == "hi there" && t.ResponseText == "hello");
    }

    /// <summary>A second import while one runs is refused.</summary>
    [Fact]
    public async Task Import_WhileAnotherRuns_IsRefused()
    {
        Seed();
        var zip = await ExportAsync();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var importer = new ConversationImporter(new BlockingSink(gate.Task, entered));
        var running = importer.ImportAsync(_target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None);

        // The sink is called from inside the first import, so once it has been reached that import is certainly running.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ConversationImportException>(() => importer.ImportAsync(
            _target.Store, zip, new ConversationImportOptions(), null, CancellationToken.None));

        Assert.Equal(ConversationImportFailure.InProgress, ex.Reason);
        gate.SetResult();
        await running;
    }

    /// <summary>The entry-name rule accepts the layout the exporter writes and refuses every way out of the archive.</summary>
    [Theory]
    [InlineData("manifest.json", true)]
    [InlineData("conversations/6f1c0c3e-0000-4000-8000-000000000001/turns/0000.request.json", true)]
    [InlineData("../evil", false)]
    [InlineData("a/../../evil", false)]
    [InlineData("/abs", false)]
    [InlineData("C:/x", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    public void IsSafeEntryName_FollowsTheZipSlipRule(string name, bool expected) =>
        Assert.Equal(expected, ConversationImporter.IsSafeEntryName(name));

    private static SessionTurnInput Turn(Guid turnId, DateTimeOffset at, byte[] request, byte[] response, bool translated = false)
    {
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["session_id"] = "client",
            ["harness"] = "claude-code",
            ["provider"] = "openai",
            ["requested_model"] = "auto",
            ["routed_model"] = "gpt-x",
            ["streaming"] = false,
            ["translated"] = translated,
            ["content_encoding"] = "json",
        });
        var bodies = new List<SessionBodyInput>
        {
            new(SessionBodyKind.ClientRequest, request),
            new(SessionBodyKind.ClientResponse, response),
        };
        if (translated)
        {
            bodies.Add(new SessionBodyInput(SessionBodyKind.ProviderRequest, "{\"provider\":\"request\"}"u8.ToArray()));
            bodies.Add(new SessionBodyInput(SessionBodyKind.ProviderResponse, "{\"provider\":\"response\"}"u8.ToArray()));
        }

        bodies.Add(new SessionBodyInput(SessionBodyKind.TurnMetadata, metadata));
        bodies.Add(new SessionBodyInput(SessionBodyKind.Extracts, """{"newest_user_message":"hi"}"""u8.ToArray()));
        return new SessionTurnInput(turnId, at, bodies);
    }

    /// <summary>Seeds the source with two sessions, one translated turn and one whose response was never captured.</summary>
    private Corpus Seed()
    {
        var turns = new List<(Guid Session, Guid TurnId)>();
        var sessions = new List<Guid>();
        void Add(Guid session, string client, int hour, byte[]? response, bool translated = false)
        {
            if (!sessions.Contains(session)) sessions.Add(session);
            var turn = SessionArchiveIds.NewArchiveTurnId();
            var input = Turn(turn, Epoch.AddHours(hour), Request, response ?? [], translated);
            if (response is null)
            {
                input = input with
                {
                    Bodies = input.Bodies
                        .Select(b => b.Kind == SessionBodyKind.ClientResponse ? new SessionBodyInput(b.Kind, null) : b)
                        .ToList(),
                };
            }

            _source.Store.AppendTurn(session, client, input);
            turns.Add((session, turn));
        }

        var one = SessionArchiveIds.NewArchiveSessionId();
        var two = SessionArchiveIds.NewArchiveSessionId();
        Add(one, "client-a", 0, Response);
        Add(one, "client-a", 1, Response, translated: true);
        Add(one, "client-a", 2, null);
        Add(two, "client-a", 3, Response);
        return new Corpus(sessions, turns.Select(t => new TurnRef(t.Session, t.TurnId)).ToList());
    }

    /// <summary>Gives the target the same turn id as the source's first turn but a different response.</summary>
    private TurnRef SeedConflictInTarget(TurnRef turn)
    {
        _target.Store.AppendTurn(
            turn.Session,
            "client-a",
            Turn(turn.TurnId, Epoch, Request, "{\"local\":true}"u8.ToArray()));
        return turn;
    }

    private async Task<string> ExportAsync(ConversationExportFilter? filter = null, string path = "export.zip")
    {
        var full = Path.Combine(_work, path);
        await new ConversationExportWriter().WriteAsync(
            _source.Store, filter ?? new ConversationExportFilter(), full, null, CancellationToken.None);
        return full;
    }

    private static string FirstBodyPath(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.First(e => e.FullName.StartsWith("conversations/", StringComparison.Ordinal)).FullName;
    }

    private static string BodyPathOf(string zipPath, Guid turn, string suffix)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("turns.jsonl")!;
        using var reader = new StreamReader(entry.Open());
        foreach (var line in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var node = JsonNode.Parse(line)!;
            if (node["archive_turn_id"]!.GetValue<string>() == turn.ToString("D"))
            {
                return node["bodies"]![suffix]!["path"]!.GetValue<string>();
            }
        }

        throw new InvalidOperationException("No such turn in the archive.");
    }

    /// <summary>Rewrites a zip with one body replaced, optionally keeping turns.jsonl and the manifest consistent with it.</summary>
    private static void ReplaceBody(string source, string destination, string bodyPath, byte[] replacement, bool fixHashes)
    {
        var entries = ReadAll(source);
        var old = entries[bodyPath];
        entries[bodyPath] = replacement;
        if (fixHashes)
        {
            var oldSha = Convert.ToHexStringLower(SHA256.HashData(old));
            var newSha = Convert.ToHexStringLower(SHA256.HashData(replacement));
            var lines = Encoding.UTF8.GetString(entries["turns.jsonl"]).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var node = JsonNode.Parse(line)!;
                    foreach (var (_, body) in node["bodies"]!.AsObject())
                    {
                        if (body?["path"]?.GetValue<string>() != bodyPath) continue;
                        body["sha256"] = newSha;
                        body["length"] = replacement.Length;
                    }

                    return node.ToJsonString();
                });
            entries["turns.jsonl"] = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");

            var manifest = JsonNode.Parse(entries["manifest.json"])!;
            manifest["turns_jsonl"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(entries["turns.jsonl"]));
            manifest["turns_jsonl"]!["length"] = entries["turns.jsonl"].Length;
            foreach (var body in manifest["bodies"]!.AsArray())
            {
                if (body!["path"]!.GetValue<string>() != bodyPath) continue;
                body["sha256"] = newSha;
                body["length"] = replacement.Length;
            }

            manifest["counts"]!["bytes"] = manifest["counts"]!["bytes"]!.GetValue<long>() + replacement.Length - old.Length;
            _ = oldSha;
            entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }

        WriteAll(destination, entries);
    }

    private static void EditManifest(string source, string destination, Action<JsonNode> edit)
    {
        var entries = ReadAll(source);
        var manifest = JsonNode.Parse(entries["manifest.json"])!;
        edit(manifest);
        entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        WriteAll(destination, entries);
    }

    private static Dictionary<string, byte[]> ReadAll(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var result = new Dictionary<string, byte[]>();
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result[entry.FullName] = buffer.ToArray();
        }

        return result;
    }

    private static void WriteAll(string path, Dictionary<string, byte[]> entries)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(bytes);
        }
    }

    private sealed record TurnRef(Guid Session, Guid TurnId);

    private sealed record Corpus(IReadOnlyList<Guid> Sessions, IReadOnlyList<TurnRef> Turns);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSink : IImportedTurnSink
    {
        public List<ImportedTurn> Turns { get; } = [];

        public Task TurnStoredAsync(ImportedTurn turn, CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingSink(Task gate, TaskCompletionSource entered) : IImportedTurnSink
    {
        public Task TurnStoredAsync(ImportedTurn turn, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            return gate;
        }
    }
}
