using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Logging;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Tests.Sessions.Export;

/// <summary>
/// Pins the conversation export of #165 phase 3 (PR 3a): the zip layout and its hashes, byte-exact bodies,
/// absent secrets, filters, the complete-or-absent guarantee, the destination rules, single flight, backfill,
/// verification failures, and a session that disappears mid-export.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class ConversationExportWriterTests : IDisposable
{
    private static readonly string[] PlantedSecrets =
    [
        "sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789",
        "ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789",
        "AKIAIOSFODNN7EXAMPLE",
    ];

    private readonly SessionTestStore _fixture = new();
    private readonly string _outputDirectory = Path.Combine(
        TestScratchDirectory.RunRoot, "export-out-" + Guid.NewGuid().ToString("N"));

    private readonly ConversationExportWriter _writer = new();

    /// <summary>Creates the folder the zips are written to, outside the store's data directory.</summary>
    public ConversationExportWriterTests()
    {
        Directory.CreateDirectory(_outputDirectory);
    }

    private SessionStore Store => _fixture.Store;

    /// <inheritdoc/>
    public void Dispose()
    {
        _fixture.Dispose();
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Scratch cleanup is also done by TestTempDirectorySweeper.
        }
    }

    /// <summary>The corpus exports to the documented layout, and every hash agrees across the three places that carry it.</summary>
    [Fact]
    public async Task Export_WritesTheDocumentedLayout_AndHashesAgree()
    {
        var corpus = Seed();
        var path = OutputPath();

        var result = await _writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None);

        Assert.Equal(path, result.Path);
        Assert.False(File.Exists(path + ".partial"));
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadJson(zip, "manifest.json");
        Assert.Equal(1, manifest.GetProperty("schema_version").GetInt32());
        Assert.False(manifest.GetProperty("incomplete").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("router_version").GetString()));
        Assert.Equal(JsonValueKind.String, manifest.GetProperty("exported_at_utc").ValueKind);

        var counts = manifest.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("conversations").GetInt32());
        Assert.Equal(corpus.TurnCount, counts.GetProperty("turns").GetInt32());
        Assert.Equal(1, counts.GetProperty("missing_bodies").GetInt32());

        // turns.jsonl hash matches the manifest.
        var turnsBytes = ReadBytes(zip, "turns.jsonl");
        var turnsEntry = manifest.GetProperty("turns_jsonl");
        Assert.Equal(Hex(SHA256.HashData(turnsBytes)), turnsEntry.GetProperty("sha256").GetString());
        Assert.Equal(turnsBytes.Length, turnsEntry.GetProperty("length").GetInt64());

        // Every body entry: zip bytes, turns.jsonl and the manifest give one hash.
        var manifestBodies = manifest.GetProperty("bodies").EnumerateArray()
            .ToDictionary(b => b.GetProperty("path").GetString()!, b => b.GetProperty("sha256").GetString()!);
        var lines = Lines(turnsBytes);
        Assert.Equal(corpus.TurnCount, lines.Count);
        var bodyPaths = new List<string>();
        foreach (var line in lines)
        {
            foreach (var body in line.GetProperty("bodies").EnumerateObject())
            {
                if (body.Value.GetProperty("missing").GetBoolean()) continue;
                var bodyPath = body.Value.GetProperty("path").GetString()!;
                bodyPaths.Add(bodyPath);
                var bytes = ReadBytes(zip, bodyPath);
                Assert.Equal(Hex(SHA256.HashData(bytes)), body.Value.GetProperty("sha256").GetString());
                Assert.Equal(body.Value.GetProperty("sha256").GetString(), manifestBodies[bodyPath]);
                Assert.Equal(bytes.Length, body.Value.GetProperty("length").GetInt64());
            }
        }

        Assert.Equal(manifestBodies.Keys.Order(), bodyPaths.Order());

        // The zip holds exactly manifest.json, turns.jsonl and the body files.
        Assert.Equal(
            bodyPaths.Append("manifest.json").Append("turns.jsonl").Order(),
            zip.Entries.Select(e => e.FullName).Order());

        // Session directories are archive ids, so the colliding client id gives two directories.
        var directories = bodyPaths.Select(p => p.Split('/')[1]).Distinct().Order().ToList();
        Assert.Equal(corpus.SessionIds.Select(id => id.ToString("D")).Order(), directories);
        Assert.All(bodyPaths, p => Assert.Matches(@"^conversations/[0-9a-f-]{36}/turns/\d{4}\.(request|response|provider-request|provider-response)\.json$", p));

        // Per-session checksum is the store's.
        foreach (var session in manifest.GetProperty("sessions").EnumerateArray())
        {
            var id = Guid.Parse(session.GetProperty("archive_session_id").GetString()!);
            Assert.Equal(Hex(Store.GetSessionSha256(id)!), session.GetProperty("session_sha256").GetString());
        }
    }

    /// <summary>turns.jsonl is ordered by client session, archive session and turn, with metadata nested and secrets_obscured null.</summary>
    [Fact]
    public async Task Export_TurnsJsonl_OrdersTurnsAndNestsMetadata()
    {
        var corpus = Seed();
        var path = OutputPath();

        await _writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None);

        using var zip = ZipFile.OpenRead(path);
        var lines = Lines(ReadBytes(zip, "turns.jsonl"));
        var keys = lines.Select(l => (
            l.GetProperty("client_session_id").GetString()!,
            l.GetProperty("archive_session_id").GetGuid(),
            l.GetProperty("turn_sequence").GetInt32())).ToList();
        Assert.Equal(keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2).ThenBy(k => k.Item3), keys);

        var first = lines.First(l => l.GetProperty("archive_turn_id").GetGuid() == corpus.TranslatedTurnId);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("secrets_obscured").ValueKind);
        Assert.Equal("full-body", first.GetProperty("content_fidelity").GetString());
        Assert.Equal("captured", first.GetProperty("origin").GetString());
        Assert.Equal("claude-code", first.GetProperty("metadata").GetProperty("harness").GetString());
        Assert.True(first.GetProperty("metadata").GetProperty("translated").GetBoolean());
        Assert.Equal(4, first.GetProperty("bodies").EnumerateObject().Count());
        Assert.True(first.GetProperty("bodies").TryGetProperty("provider_response", out _));
    }

    /// <summary>A missing body is an absent file with a content_fidelity that says so, never an empty file.</summary>
    [Fact]
    public async Task Export_MissingBody_HasNoFile_AndSaysSo()
    {
        var corpus = Seed();
        var path = OutputPath();

        var result = await _writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None);

        Assert.Equal(1, result.MissingBodies);
        using var zip = ZipFile.OpenRead(path);
        var line = Lines(ReadBytes(zip, "turns.jsonl"))
            .Single(l => l.GetProperty("archive_turn_id").GetGuid() == corpus.MissingTurnId);
        Assert.Equal("missing-body", line.GetProperty("content_fidelity").GetString());
        var response = line.GetProperty("bodies").GetProperty("response");
        Assert.True(response.GetProperty("missing").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("path").ValueKind);
        var folder = $"conversations/{corpus.MissingSessionId:D}/turns/";
        Assert.Null(zip.GetEntry(folder + "0000.response.json"));
        Assert.NotNull(zip.GetEntry(folder + "0000.request.json"));
    }

    /// <summary>Whitespace, non-ASCII, markup characters and invalid UTF-8 export byte for byte as stored.</summary>
    [Fact]
    public async Task Export_BodiesAreByteIdenticalToStoredPlaintext()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-bytes");
        var request = Encoding.UTF8.GetBytes("{ \"a\" :\t\"héllo wörld ✓ <>&'+\"\r\n }\n  ").Concat(new byte[] { 0xFF, 0xFE, 0xC0 }).ToArray();
        var turn = Turn(request, "data: {\"x\":1}\n\n"u8.ToArray());
        Store.AppendTurn(sessionId, "client-bytes", turn);
        var path = OutputPath();

        await _writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None);

        using var zip = ZipFile.OpenRead(path);
        var stored = Store.ReadBodies(sessionId);
        var prefix = $"conversations/{sessionId:D}/turns/0000.";
        Assert.Equal(stored.Single(f => f.Kind == SessionBodyKind.ClientRequest).Plaintext, ReadBytes(zip, prefix + "request.json"));
        Assert.Equal(stored.Single(f => f.Kind == SessionBodyKind.ClientResponse).Plaintext, ReadBytes(zip, prefix + "response.json"));
        Assert.Equal(request, ReadBytes(zip, prefix + "request.json"));
    }

    /// <summary>Every planted secret shape is absent from every entry of the zip.</summary>
    [Fact]
    public async Task Export_PlantedSecrets_AreAbsentFromEveryEntry()
    {
        Assert.All(PlantedSecrets, secret => Assert.NotEqual(secret, SecretObscurer.Obscure(secret)));
        var sessionId = Store.ResolveArchiveSessionId("client-secrets");
        var withSecrets = string.Join(" ", PlantedSecrets.Select(s => $"key={s}"));
        Store.AppendTurn(sessionId, "client-secrets", Turn(
            Encoding.UTF8.GetBytes($$"""{"content":"{{withSecrets}}"}"""),
            Encoding.UTF8.GetBytes($"reply {withSecrets}"),
            metadata: "{\"harness\":\"claude-code\"}"));
        var path = OutputPath();

        await _writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None);

        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            var text = Encoding.UTF8.GetString(ReadBytes(zip, entry.FullName));
            foreach (var secret in PlantedSecrets) Assert.DoesNotContain(secret, text);
        }

        Assert.Contains(zip.Entries, e => e.FullName.EndsWith(".request.json", StringComparison.Ordinal));
    }

    /// <summary>A cancelled export leaves neither the zip nor its partial file.</summary>
    [Fact]
    public async Task Export_Cancelled_LeavesNoZipAndNoPartial()
    {
        Seed();
        var path = OutputPath();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _writer.WriteAsync(Store, new ConversationExportFilter(), path, progress, cts.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_outputDirectory));
        Assert.Empty(Directory.EnumerateFiles(_fixture.Folder, "*.tmp"));
    }

    /// <summary>A cancellation before the export starts writes nothing.</summary>
    [Fact]
    public async Task Export_CancelledBeforeStart_WritesNothing()
    {
        Seed();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _writer.WriteAsync(Store, new ConversationExportFilter(), OutputPath(), null, cts.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_outputDirectory));
    }

    /// <summary>Progress is reported after each turn with a running count.</summary>
    [Fact]
    public async Task Export_ReportsProgressPerTurn()
    {
        var corpus = Seed();
        var reports = new List<ConversationExportProgress>();

        await _writer.WriteAsync(
            Store, new ConversationExportFilter(), OutputPath(), new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(corpus.TurnCount, reports.Count);
        Assert.Equal(Enumerable.Range(1, corpus.TurnCount), reports.Select(r => r.TurnsWritten));
        Assert.True(reports[^1].BytesWritten > 0);
    }

    /// <summary>Destinations that are relative, existing, or under the data directory are refused before anything is written.</summary>
    [Fact]
    public async Task Export_RefusesBadDestinations()
    {
        Seed();
        var existing = OutputPath();
        await File.WriteAllTextAsync(existing, "keep");

        await AssertRefused(Path.Combine("relative", "out.zip"), ConversationExportFailure.InvalidDestination);
        await AssertRefused(existing, ConversationExportFailure.InvalidDestination);
        await AssertRefused(Path.Combine(_fixture.Root, "out.zip"), ConversationExportFailure.InvalidDestination);
        await AssertRefused(Path.Combine(_fixture.Folder, "out.zip"), ConversationExportFailure.InvalidDestination);
        await AssertRefused(Path.Combine(_outputDirectory, "no-such-folder", "out.zip"), ConversationExportFailure.InvalidDestination);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));

        async Task AssertRefused(string destination, ConversationExportFailure reason)
        {
            var ex = await Assert.ThrowsAsync<ConversationExportException>(
                () => _writer.WriteAsync(Store, new ConversationExportFilter(), destination, null, CancellationToken.None));
            Assert.Equal(reason, ex.Reason);
        }
    }

    /// <summary>An export is refused when the volume cannot hold the bodies plus the reserve.</summary>
    [Fact]
    public async Task Export_RefusesWhenTheVolumeIsTooFull()
    {
        Seed();
        var writer = new ConversationExportWriter(volumeSpace: _ => (FreeBytes: 1024, TotalBytes: 1L << 40));
        var path = OutputPath();

        var ex = await Assert.ThrowsAsync<ConversationExportException>(
            () => writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None));

        Assert.Equal(ConversationExportFailure.InsufficientDiskSpace, ex.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outputDirectory));
    }

    /// <summary>A second export while one runs is refused, and the first still completes.</summary>
    [Fact]
    public async Task Export_SecondExportWhileRunning_IsRefused()
    {
        Seed();
        ConversationExportException? refused = null;
        var progress = new SyncProgress(_ =>
        {
            if (refused is not null) return;
            var second = _writer.WriteAsync(Store, new ConversationExportFilter(), OutputPath(), null, CancellationToken.None);
            refused = Assert.IsType<ConversationExportException>(
                Assert.ThrowsAny<AggregateException>(() => second.Wait(TimeSpan.FromSeconds(4))).InnerException);
        });
        var path = OutputPath();

        await _writer.WriteAsync(Store, new ConversationExportFilter(), path, progress, CancellationToken.None);

        Assert.Equal(ConversationExportFailure.InProgress, refused!.Reason);
        Assert.True(File.Exists(path));

        // The slot is free again once the first export ends.
        await _writer.WriteAsync(Store, new ConversationExportFilter(), OutputPath(), null, CancellationToken.None);
    }

    /// <summary>The date window, session id (both kinds), harness, provider and model each select the right turns, and the manifest records them.</summary>
    [Fact]
    public async Task Export_Filters_SelectTheRightTurns_AndAreRecorded()
    {
        var corpus = Seed();

        var byClient = await ExportedTurnIds(new ConversationExportFilter(SessionId: "client-a"));
        Assert.Equal(corpus.ClientATurnIds.Order(), byClient.Order());

        var byArchive = await ExportedTurnIds(new ConversationExportFilter(SessionId: corpus.SessionIds[0].ToString("D")));
        Assert.All(byArchive, id => Assert.Contains(id, corpus.TurnIdsBySession[corpus.SessionIds[0]]));
        Assert.Equal(corpus.TurnIdsBySession[corpus.SessionIds[0]].Order(), byArchive.Order());

        var window = await ExportedTurnIds(new ConversationExportFilter(
            From: corpus.Epoch.AddHours(1), To: corpus.Epoch.AddHours(2)));
        Assert.Equal(corpus.TurnIdsAtHours([1, 2]).Order(), window.Order());

        Assert.Equal([corpus.TranslatedTurnId], await ExportedTurnIds(new ConversationExportFilter(Harness: "CLAUDE-CODE")));
        Assert.Equal(corpus.CodexTurnIds.Order(), (await ExportedTurnIds(new ConversationExportFilter(Provider: "openai"))).Order());
        Assert.Equal(corpus.CodexTurnIds.Order(), (await ExportedTurnIds(new ConversationExportFilter(Model: "gpt-x"))).Order());

        var path = OutputPath();
        await _writer.WriteAsync(Store, new ConversationExportFilter(Harness: "codex", Model: "gpt-x"), path, null, CancellationToken.None);
        using var zip = ZipFile.OpenRead(path);
        var filter = ReadJson(zip, "manifest.json").GetProperty("filter");
        Assert.Equal("codex", filter.GetProperty("harness").GetString());
        Assert.Equal("gpt-x", filter.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, filter.GetProperty("session_id").ValueKind);
    }

    /// <summary>A body whose stored hash disagrees is left out and listed as corrupt, while the rest of the export proceeds.</summary>
    [Fact]
    public async Task Export_HashMismatch_FailsThatTurnOnly()
    {
        var corpus = Seed();
        _fixture.Execute(
            $"UPDATE session_bodies SET sha256 = zeroblob(32) WHERE archive_turn_id = '{corpus.TranslatedTurnId:D}' AND kind = 2;");
        var path = OutputPath();

        var result = await _writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None);

        Assert.Equal(1, result.CorruptTurns);
        Assert.Equal(corpus.TurnCount - 1, result.Turns);
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadJson(zip, "manifest.json");
        var corrupt = Assert.Single(manifest.GetProperty("corrupt_turns").EnumerateArray());
        Assert.Equal(corpus.TranslatedTurnId, corrupt.GetProperty("archive_turn_id").GetGuid());
        Assert.Equal("hash_mismatch", corrupt.GetProperty("reason").GetString());
        Assert.DoesNotContain(
            Lines(ReadBytes(zip, "turns.jsonl")), l => l.GetProperty("archive_turn_id").GetGuid() == corpus.TranslatedTurnId);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains($"/{corpus.TranslatedTurnSequence:D4}.", StringComparison.Ordinal)
                                                && e.FullName.Contains(corpus.TranslatedSessionId.ToString("D"), StringComparison.Ordinal));
    }

    /// <summary>A tampered chunk fails that turn only; the session's other turns still export.</summary>
    [Fact]
    public async Task Export_TamperedChunk_FailsThatTurnOnly()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-tamper");
        var first = Turn("first-request"u8.ToArray(), "first-response"u8.ToArray());
        var second = Turn("second-request"u8.ToArray(), "second-response"u8.ToArray());
        Store.AppendTurn(sessionId, "client-tamper", first);
        Store.AppendTurn(sessionId, "client-tamper", second);
        var file = _fixture.FileOf(sessionId);
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[SessionFile.FirstFrameOffset + 22 + 33] ^= 0xFF;
        await File.WriteAllBytesAsync(file, bytes);
        var path = OutputPath();

        var result = await _writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None);

        Assert.Equal(1, result.Turns);
        Assert.Equal(1, result.CorruptTurns);
        using var zip = ZipFile.OpenRead(path);
        var line = Assert.Single(Lines(ReadBytes(zip, "turns.jsonl")));
        Assert.Equal(second.ArchiveTurnId, line.GetProperty("archive_turn_id").GetGuid());
        Assert.Equal(
            "second-request",
            Encoding.UTF8.GetString(ReadBytes(zip, $"conversations/{sessionId:D}/turns/0001.request.json")));
        Assert.Equal("frame_unreadable", ReadJson(zip, "manifest.json").GetProperty("corrupt_turns")[0].GetProperty("reason").GetString());
    }

    /// <summary>A session captured before hashes existed is backfilled on export and exports as full-body with the same checksum.</summary>
    [Fact]
    public async Task Export_BackfillsASessionWithoutHashes()
    {
        var corpus = Seed();
        var expected = Store.GetSessionSha256(corpus.TranslatedSessionId);
        _fixture.Execute("DELETE FROM session_bodies;");
        _fixture.Execute("UPDATE session_files SET session_sha256 = NULL;");
        var path = OutputPath();

        var result = await _writer.WriteAsync(Store, new ConversationExportFilter(), path, null, CancellationToken.None);

        Assert.Equal(corpus.TurnCount, result.Turns);
        Assert.Equal(0, result.CorruptTurns);
        using var zip = ZipFile.OpenRead(path);
        var lines = Lines(ReadBytes(zip, "turns.jsonl"));
        Assert.All(lines.Where(l => l.GetProperty("archive_turn_id").GetGuid() != corpus.MissingTurnId),
            l => Assert.Equal("full-body", l.GetProperty("content_fidelity").GetString()));
        var session = ReadJson(zip, "manifest.json").GetProperty("sessions").EnumerateArray()
            .Single(s => s.GetProperty("archive_session_id").GetGuid() == corpus.TranslatedSessionId);
        Assert.Equal(Hex(expected!), session.GetProperty("session_sha256").GetString());
    }

    /// <summary>A session deleted mid-export is flagged incomplete, and what was already written still verifies.</summary>
    [Fact]
    public async Task Export_SessionDeletedMidway_IsFlaggedIncomplete()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-gone");
        for (var i = 0; i < 3; i++)
        {
            Store.AppendTurn(sessionId, "client-gone", Turn(Encoding.UTF8.GetBytes($"request-{i}"), Encoding.UTF8.GetBytes($"response-{i}")));
        }

        var path = OutputPath();
        var deleted = false;
        var progress = new SyncProgress(_ =>
        {
            if (deleted) return;
            deleted = true;
            Store.DeleteSessions([sessionId]);
        });

        var result = await _writer.WriteAsync(Store, new ConversationExportFilter(), path, progress, CancellationToken.None);

        Assert.Equal(1, result.Turns);
        Assert.Equal(1, result.IncompleteSessions);
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadJson(zip, "manifest.json");
        Assert.True(manifest.GetProperty("incomplete").GetBoolean());
        var bodies = manifest.GetProperty("bodies").EnumerateArray().ToList();
        Assert.NotEmpty(bodies);
        foreach (var body in bodies)
        {
            Assert.Equal(
                Hex(SHA256.HashData(ReadBytes(zip, body.GetProperty("path").GetString()!))), body.GetProperty("sha256").GetString());
        }
    }

    /// <summary>An append to a session after the export was planned neither blocks the export nor appears in it.</summary>
    [Fact]
    public async Task Export_AppendDuringExport_IsNotInTheSnapshot()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-live");
        Store.AppendTurn(sessionId, "client-live", Turn("one"u8.ToArray(), "uno"u8.ToArray()));
        Store.AppendTurn(sessionId, "client-live", Turn("two"u8.ToArray(), "dos"u8.ToArray()));
        var late = Turn("late"u8.ToArray(), "tarde"u8.ToArray());
        var appended = false;
        var progress = new SyncProgress(_ =>
        {
            if (appended) return;
            appended = true;
            Store.AppendTurn(sessionId, "client-live", late);
        });
        var path = OutputPath();

        var result = await _writer.WriteAsync(Store, new ConversationExportFilter(), path, progress, CancellationToken.None);

        Assert.Equal(2, result.Turns);
        using var zip = ZipFile.OpenRead(path);
        Assert.DoesNotContain(
            Lines(ReadBytes(zip, "turns.jsonl")), l => l.GetProperty("archive_turn_id").GetGuid() == late.ArchiveTurnId);
        Assert.Equal(3, Store.ListTurns(sessionId).Count);
    }

    private async Task<List<Guid>> ExportedTurnIds(ConversationExportFilter filter)
    {
        var path = OutputPath();
        await _writer.WriteAsync(Store, filter, path, null, CancellationToken.None);
        using var zip = ZipFile.OpenRead(path);
        return Lines(ReadBytes(zip, "turns.jsonl")).Select(l => l.GetProperty("archive_turn_id").GetGuid()).ToList();
    }

    private string OutputPath() => Path.Combine(_outputDirectory, Guid.NewGuid().ToString("N") + ".zip");

    private static byte[] ReadBytes(ZipArchive zip, string name)
    {
        using var stream = (zip.GetEntry(name) ?? throw new InvalidOperationException($"No entry {name}.")).Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static JsonElement ReadJson(ZipArchive zip, string name) => JsonDocument.Parse(ReadBytes(zip, name)).RootElement.Clone();

    private static List<JsonElement> Lines(byte[] jsonl) => Encoding.UTF8.GetString(jsonl)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.Clone())
        .ToList();

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static SessionTurnInput Turn(byte[] request, byte[] response, string? metadata = null) =>
        Turn(SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow, request, response, metadata);

    private static SessionTurnInput Turn(
        Guid turnId, DateTimeOffset createdAt, byte[] request, byte[] response, string? metadata = null)
    {
        var bodies = new List<SessionBodyInput>
        {
            new(SessionBodyKind.ClientRequest, request),
            new(SessionBodyKind.ClientResponse, response),
        };
        if (metadata is not null) bodies.Add(new SessionBodyInput(SessionBodyKind.TurnMetadata, Encoding.UTF8.GetBytes(metadata)));
        bodies.Add(new SessionBodyInput(SessionBodyKind.Extracts, """{"newest_user_message":"hi","response_text":"yo"}"""u8.ToArray()));
        return new SessionTurnInput(turnId, createdAt, bodies);
    }

    /// <summary>
    /// Seeds three sessions, two of which share the client id <c>client-a</c>: a translated turn, plain
    /// turns with different harness, provider and model, and one turn whose response was never captured.
    /// </summary>
    private Corpus Seed()
    {
        var epoch = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var turnIdsBySession = new Dictionary<Guid, List<Guid>>();
        var turnIdsByHour = new Dictionary<int, List<Guid>>();
        var sessionIds = new List<Guid>();

        Guid Add(string client, Guid sessionId, int hour, byte[] request, byte[]? response, string harness,
            string provider, string model, bool translated = false)
        {
            if (!turnIdsBySession.ContainsKey(sessionId))
            {
                turnIdsBySession[sessionId] = [];
                sessionIds.Add(sessionId);
            }

            var turnId = SessionArchiveIds.NewArchiveTurnId();
            var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["session_id"] = client,
                ["turn_number"] = turnIdsBySession[sessionId].Count + 1,
                ["harness"] = harness,
                ["provider"] = provider,
                ["requested_model"] = "auto",
                ["routed_model"] = model,
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

            bodies.Add(new SessionBodyInput(SessionBodyKind.TurnMetadata, Encoding.UTF8.GetBytes(metadata)));
            bodies.Add(new SessionBodyInput(SessionBodyKind.Extracts, """{"newest_user_message":"hi"}"""u8.ToArray()));
            Store.AppendTurn(sessionId, client, new SessionTurnInput(turnId, epoch.AddHours(hour), bodies));
            turnIdsBySession[sessionId].Add(turnId);
            if (!turnIdsByHour.TryGetValue(hour, out var atHour)) turnIdsByHour[hour] = atHour = [];
            atHour.Add(turnId);
            return turnId;
        }

        var a1 = SessionArchiveIds.NewArchiveSessionId();
        var a2 = SessionArchiveIds.NewArchiveSessionId();
        var b1 = SessionArchiveIds.NewArchiveSessionId();
        var translatedTurn = Add("client-a", a1, 0, """{"messages":[1]}"""u8.ToArray(), """{"ok":true}"""u8.ToArray(),
            "claude-code", "anthropic", "claude-x", translated: true);
        var codexOne = Add("client-a", a1, 1, "{\"n\":2}"u8.ToArray(), "{\"n\":2}"u8.ToArray(), "codex", "openai", "gpt-x");
        var codexTwo = Add("client-a", a2, 2, "{\"n\":3}"u8.ToArray(), "{\"n\":3}"u8.ToArray(), "codex", "openai", "gpt-x");
        var missing = Add("client-b", b1, 3, "{\"n\":4}"u8.ToArray(), null, "gemini-cli", "google", "gemini-x");

        return new Corpus(
            epoch, sessionIds, turnIdsBySession.ToDictionary(p => p.Key, p => (IReadOnlyList<Guid>)p.Value),
            turnIdsByHour, translatedTurn, a1, 0, missing, b1, [codexOne, codexTwo],
            turnIdsBySession.Where(p => p.Key == a1 || p.Key == a2).SelectMany(p => p.Value).ToList());
    }

    private sealed record Corpus(
        DateTimeOffset Epoch,
        IReadOnlyList<Guid> SessionIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> TurnIdsBySession,
        Dictionary<int, List<Guid>> TurnIdsByHour,
        Guid TranslatedTurnId,
        Guid TranslatedSessionId,
        int TranslatedTurnSequence,
        Guid MissingTurnId,
        Guid MissingSessionId,
        IReadOnlyList<Guid> CodexTurnIds,
        IReadOnlyList<Guid> ClientATurnIds)
    {
        public int TurnCount => TurnIdsBySession.Values.Sum(turns => turns.Count);

        public IEnumerable<Guid> TurnIdsAtHours(int[] hours) => hours.SelectMany(h => TurnIdsByHour[h]);
    }

    private sealed class SyncProgress(Action<ConversationExportProgress> handler) : IProgress<ConversationExportProgress>
    {
        public void Report(ConversationExportProgress value) => handler(value);
    }
}
