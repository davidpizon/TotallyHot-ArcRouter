using System.Security.Cryptography;
using System.Text;
using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins the body hashes and the session checksum of #165 phase 3 (PR 3a): the hash is of the stored
/// plaintext after obscuring, both capture paths agree, the checksum covers only the exchange bodies, a
/// session captured without hashes backfills to the same checksum, and a turn can be read on its own.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class SessionBodyHashTests : IDisposable
{
    private const string AnthropicKey = "sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

    private readonly SessionTestStore _fixture = new();

    private SessionStore Store => _fixture.Store;

    /// <inheritdoc/>
    public void Dispose() => _fixture.Dispose();

    /// <summary>An in-memory body's hash row is the SHA-256 of what the file stores, not of the raw input.</summary>
    [Fact]
    public void AppendTurn_InMemoryBodies_HashRowsMatchStoredPlaintext()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-1");
        var request = Encoding.UTF8.GetBytes($$"""{"content":"hello {{AnthropicKey}}"}""");
        var turn = Turn(request, "response"u8.ToArray());

        Store.AppendTurn(sessionId, "client-1", turn);

        var frames = Store.ReadBodies(sessionId);
        var rows = Store.ListBodyHashes(sessionId);
        Assert.Equal(frames.Count, rows.Count);
        foreach (var frame in frames)
        {
            var row = rows.Single(r => r.Kind == frame.Kind);
            Assert.Equal(SHA256.HashData(frame.Plaintext!), row.Sha256);
            Assert.Equal(frame.Plaintext!.Length, row.Length);
            Assert.False(row.Missing);
        }

        var requestRow = rows.Single(r => r.Kind == SessionBodyKind.ClientRequest);
        Assert.NotEqual(SHA256.HashData(request), requestRow.Sha256);
        Assert.True(requestRow.Obscured);
        Assert.False(rows.Single(r => r.Kind == SessionBodyKind.ClientResponse).Obscured);
    }

    /// <summary>A spooled body is hashed as the obscurer produces it, and the hash equals the stored plaintext's.</summary>
    [Fact]
    public void AppendTurn_SpooledBody_HashRowMatchesStoredPlaintext()
    {
        var body = Encoding.UTF8.GetBytes($$"""{"content":"{{new string('x', 100_000)}} {{AnthropicKey}} tail"}""");
        using var spool = SessionBodySpool.Create(Path.Combine(_fixture.Root, "spool"));
        foreach (var piece in body.Chunk(997)) Assert.True(spool.TryWrite(piece));
        Assert.True(spool.TryComplete());
        var sessionId = Store.ResolveArchiveSessionId("client-1");

        Store.AppendTurn(sessionId, "client-1", new SessionTurnInput(
            SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow,
            [new SessionBodyInput(SessionBodyKind.ClientRequest, "{}"u8.ToArray()),
             new SessionBodyInput(SessionBodyKind.ClientResponse, null, spool)]));

        var stored = Store.ReadBodies(sessionId).Single(f => f.Kind == SessionBodyKind.ClientResponse).Plaintext!;
        var row = Store.ListBodyHashes(sessionId).Single(r => r.Kind == SessionBodyKind.ClientResponse);
        Assert.Equal(SHA256.HashData(stored), row.Sha256);
        Assert.Equal(stored.Length, row.Length);
        Assert.Equal(row.Sha256, spool.PlaintextSha256);
        Assert.DoesNotContain(AnthropicKey, Encoding.UTF8.GetString(stored));
    }

    /// <summary>An abandoned spool has no hash, and its body is recorded as missing.</summary>
    [Fact]
    public void AbandonedSpool_HasNoHash_AndIsRecordedMissing()
    {
        using var spool = SessionBodySpool.Create(Path.Combine(_fixture.Root, "spool"));
        Assert.True(spool.TryWrite("partial"u8));
        spool.Abandon();
        Assert.Null(spool.PlaintextSha256);

        var sessionId = Store.ResolveArchiveSessionId("client-1");
        Store.AppendTurn(sessionId, "client-1", new SessionTurnInput(
            SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow,
            [new SessionBodyInput(SessionBodyKind.ClientRequest, "{}"u8.ToArray()),
             new SessionBodyInput(SessionBodyKind.ClientResponse, null, spool)]));

        var row = Store.ListBodyHashes(sessionId).Single(r => r.Kind == SessionBodyKind.ClientResponse);
        Assert.True(row.Missing);
        Assert.Null(row.Sha256);
        Assert.Equal(0, row.Length);
    }

    /// <summary>The checksum follows the documented algorithm, with zero hashes for kinds a turn does not have.</summary>
    [Fact]
    public void SessionSha256_FollowsTheDocumentedAlgorithm()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-1");
        var first = Turn("req-1"u8.ToArray(), "res-1"u8.ToArray());
        var second = new SessionTurnInput(
            SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow,
            [new SessionBodyInput(SessionBodyKind.ClientRequest, "req-2"u8.ToArray()),
             new SessionBodyInput(SessionBodyKind.ClientResponse, null),
             new SessionBodyInput(SessionBodyKind.ProviderRequest, "preq-2"u8.ToArray())]);
        Store.AppendTurn(sessionId, "client-1", first);
        Store.AppendTurn(sessionId, "client-1", second);

        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendExpected(expected, first.ArchiveTurnId, "req-1", "res-1", null, null);
        AppendExpected(expected, second.ArchiveTurnId, "req-2", null, "preq-2", null);

        Assert.Equal(expected.GetHashAndReset(), Store.GetSessionSha256(sessionId));
        Assert.Equal(Store.GetSessionSha256(sessionId), Store.ListSessions().Single().SessionSha256);
    }

    /// <summary>The checksum changes when a body changes, and not for a timestamp, metadata or extracts.</summary>
    [Fact]
    public void SessionSha256_ChangesWithBodies_NotWithTimestampsOrMetadata()
    {
        using var other = new SessionTestStore();
        var turnId = SessionArchiveIds.NewArchiveTurnId();
        var baseline = Capture(Store, turnId, DateTimeOffset.UtcNow, "request", "response", "{\"a\":1}", "{\"b\":1}");
        var laterWithOtherMetadata = Capture(
            other.Store, turnId, DateTimeOffset.UtcNow.AddDays(-30), "request", "response", "{\"a\":2}", "{\"b\":2}");
        Assert.Equal(baseline, laterWithOtherMetadata);

        using var changed = new SessionTestStore();
        var changedBody = Capture(changed.Store, turnId, DateTimeOffset.UtcNow, "request", "response!", "{\"a\":1}", "{\"b\":1}");
        Assert.NotEqual(baseline, changedBody);
    }

    /// <summary>A session captured before hashes existed backfills to the same hashes and checksum as a fresh capture.</summary>
    [Fact]
    public void Backfill_ProducesTheSameChecksumAsFreshCapture()
    {
        using var legacy = new SessionTestStore();
        var turnIds = new[] { SessionArchiveIds.NewArchiveTurnId(), SessionArchiveIds.NewArchiveTurnId() };
        var freshId = Store.ResolveArchiveSessionId("client-1");
        var legacyId = legacy.Store.ResolveArchiveSessionId("client-1");
        foreach (var turnId in turnIds)
        {
            var body = Encoding.UTF8.GetBytes($$"""{"content":"turn {{turnId}} {{AnthropicKey}}"}""");
            Store.AppendTurn(freshId, "client-1", Turn(turnId, body, "resp"u8.ToArray()));
            legacy.Store.AppendTurn(legacyId, "client-1", Turn(turnId, body, "resp"u8.ToArray()));
        }

        // Stage a session from before this change: no hash rows and no checksum.
        legacy.Execute("DELETE FROM session_bodies;");
        legacy.Execute("UPDATE session_files SET session_sha256 = NULL;");
        Assert.False(legacy.Store.HasBodyHashes(legacyId));
        Assert.Null(legacy.Store.GetSessionSha256(legacyId));

        Assert.True(legacy.Store.BackfillBodyHashes(legacyId));

        Assert.True(legacy.Store.HasBodyHashes(legacyId));
        Assert.Equal(Store.GetSessionSha256(freshId), legacy.Store.GetSessionSha256(legacyId));
        static string Describe(IReadOnlyList<SessionBodyHashRow> rows) => string.Join(
            ";", rows.Select(r => $"{r.Kind}:{(r.Sha256 is null ? "-" : Convert.ToHexString(r.Sha256))}:{r.Length}:{r.Missing}"));
        Assert.Equal(Describe(Store.ListBodyHashes(freshId)), Describe(legacy.Store.ListBodyHashes(legacyId)));
    }

    /// <summary>A turn appended to a session that still lacks hashes leaves its checksum unset instead of wrong.</summary>
    [Fact]
    public void AppendTurn_OnUnhashedSession_KeepsChecksumNull()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-1");
        Store.AppendTurn(sessionId, "client-1", Turn("a"u8.ToArray(), "b"u8.ToArray()));
        _fixture.Execute("DELETE FROM session_bodies;");

        Store.AppendTurn(sessionId, "client-1", Turn("c"u8.ToArray(), "d"u8.ToArray()));

        Assert.Null(Store.GetSessionSha256(sessionId));
        Assert.False(Store.HasBodyHashes(sessionId));
    }

    /// <summary>Deleting a session removes its hash rows along with its turns.</summary>
    [Fact]
    public void DeleteSessions_RemovesBodyHashRows()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-1");
        Store.AppendTurn(sessionId, "client-1", Turn("a"u8.ToArray(), "b"u8.ToArray()));
        Assert.NotEqual(0, _fixture.Scalar("SELECT COUNT(*) FROM session_bodies;"));

        Store.DeleteSessions([sessionId]);

        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM session_bodies;"));
        Assert.Empty(Store.ListBodyHashes(sessionId));
    }

    /// <summary>The index creates the new column on a database built before it existed.</summary>
    [Fact]
    public void EnsureCreated_AddsSessionSha256ToAnOlderIndex()
    {
        _fixture.Execute("ALTER TABLE session_files DROP COLUMN session_sha256;");

        _fixture.Index.EnsureCreated();

        Assert.Equal(1, _fixture.Scalar("SELECT COUNT(*) FROM pragma_table_info('session_files') WHERE name = 'session_sha256';"));
    }

    /// <summary>A multi-chunk body streams back byte-exact, including bytes that are not valid UTF-8.</summary>
    [Fact]
    public void OpenTurn_StreamsMultiChunkBodiesByteExact()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-1");
        var big = new byte[300_000];
        new Random(11).NextBytes(big);
        var turn = Turn("req"u8.ToArray(), big);
        Store.AppendTurn(sessionId, "client-1", turn);

        var read = new Dictionary<SessionBodyKind, byte[]>();
        using (var reader = Store.OpenTurn(sessionId, turn.ArchiveTurnId)!)
        {
            foreach (var frame in reader.ReadFrames())
            {
                using (frame)
                {
                    using var buffer = new MemoryStream();
                    frame.Body!.CopyTo(buffer);
                    read[frame.Kind] = buffer.ToArray();
                }
            }
        }

        var stored = Store.ReadBodies(sessionId);
        Assert.Equal(stored.Single(f => f.Kind == SessionBodyKind.ClientResponse).Plaintext, read[SessionBodyKind.ClientResponse]);
        Assert.Equal(stored.Single(f => f.Kind == SessionBodyKind.ClientRequest).Plaintext, read[SessionBodyKind.ClientRequest]);
    }

    /// <summary>A later turn opens without decrypting an earlier one, and a tampered chunk fails only its own turn.</summary>
    [Fact]
    public void OpenTurn_ReadsOneTurnOnly_AndTamperFailsOnlyThatTurn()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-1");
        var first = Turn("first-request"u8.ToArray(), "first-response"u8.ToArray());
        var second = Turn("second-request"u8.ToArray(), "second-response"u8.ToArray());
        Store.AppendTurn(sessionId, "client-1", first);
        Store.AppendTurn(sessionId, "client-1", second);

        // Flip the first ciphertext byte of the first turn's first chunk. Frame header is 22 bytes and the
        // chunk record's own header is 33 bytes.
        var path = _fixture.FileOf(sessionId);
        var bytes = File.ReadAllBytes(path);
        bytes[SessionFile.FirstFrameOffset + 22 + 33] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using (var reader = Store.OpenTurn(sessionId, second.ArchiveTurnId)!)
        {
            var texts = new List<string>();
            foreach (var frame in reader.ReadFrames())
            {
                using (frame)
                {
                    using var buffer = new MemoryStream();
                    frame.Body!.CopyTo(buffer);
                    texts.Add(Encoding.UTF8.GetString(buffer.ToArray()));
                }
            }

            Assert.Equal(["second-request", "second-response"], texts);
        }

        using var tampered = Store.OpenTurn(sessionId, first.ArchiveTurnId)!;
        Assert.ThrowsAny<CryptographicException>(() =>
        {
            foreach (var frame in tampered.ReadFrames())
            {
                using (frame) frame.Body!.CopyTo(Stream.Null);
            }
        });
    }

    /// <summary>A missing-body marker streams as a frame with no body, and an unknown turn opens as null.</summary>
    [Fact]
    public void OpenTurn_MissingBodyHasNoStream_AndUnknownTurnIsNull()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-1");
        var turn = new SessionTurnInput(
            SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow,
            [new SessionBodyInput(SessionBodyKind.ClientRequest, "req"u8.ToArray()),
             new SessionBodyInput(SessionBodyKind.ClientResponse, null)]);
        Store.AppendTurn(sessionId, "client-1", turn);

        using (var reader = Store.OpenTurn(sessionId, turn.ArchiveTurnId)!)
        {
            var frames = new List<(SessionBodyKind Kind, bool Missing)>();
            foreach (var frame in reader.ReadFrames())
            {
                using (frame) frames.Add((frame.Kind, frame.IsMissing));
            }

            Assert.Equal([(SessionBodyKind.ClientRequest, false), (SessionBodyKind.ClientResponse, true)], frames);
        }

        Assert.Null(Store.OpenTurn(sessionId, SessionArchiveIds.NewArchiveTurnId()));
        Assert.Null(Store.OpenTurn(SessionArchiveIds.NewArchiveSessionId(), turn.ArchiveTurnId));
    }

    /// <summary>TryReadBodies reads the wanted kinds for the wanted turns and nothing else.</summary>
    [Fact]
    public void TryReadBodies_ReturnsOnlyWantedKindsAndTurns()
    {
        var sessionId = Store.ResolveArchiveSessionId("client-1");
        var first = Turn("a"u8.ToArray(), "b"u8.ToArray(), metadata: "{\"harness\":\"one\"}", extracts: "{}");
        var second = Turn("c"u8.ToArray(), "d"u8.ToArray(), metadata: "{\"harness\":\"two\"}", extracts: "{}");
        Store.AppendTurn(sessionId, "client-1", first);
        Store.AppendTurn(sessionId, "client-1", second);

        var found = Store.TryReadBodies(
            sessionId, new HashSet<SessionBodyKind> { SessionBodyKind.TurnMetadata }, [second.ArchiveTurnId]);

        var entry = Assert.Single(found);
        Assert.Equal((second.ArchiveTurnId, SessionBodyKind.TurnMetadata), entry.Key);
        Assert.Equal("{\"harness\":\"two\"}", Encoding.UTF8.GetString(entry.Value));
    }

    private static byte[]? Capture(
        SessionStore store, Guid turnId, DateTimeOffset createdAt, string request, string response, string metadata, string extracts)
    {
        var sessionId = store.ResolveArchiveSessionId("client-1");
        store.AppendTurn(sessionId, "client-1", new SessionTurnInput(
            turnId, createdAt,
            [new SessionBodyInput(SessionBodyKind.ClientRequest, Encoding.UTF8.GetBytes(request)),
             new SessionBodyInput(SessionBodyKind.ClientResponse, Encoding.UTF8.GetBytes(response)),
             new SessionBodyInput(SessionBodyKind.TurnMetadata, Encoding.UTF8.GetBytes(metadata)),
             new SessionBodyInput(SessionBodyKind.Extracts, Encoding.UTF8.GetBytes(extracts))]));
        return store.GetSessionSha256(sessionId);
    }

    private static SessionTurnInput Turn(
        byte[] request, byte[] response, string? metadata = null, string? extracts = null) =>
        Turn(SessionArchiveIds.NewArchiveTurnId(), request, response, metadata, extracts);

    private static SessionTurnInput Turn(
        Guid turnId, byte[] request, byte[] response, string? metadata = null, string? extracts = null)
    {
        var bodies = new List<SessionBodyInput>
        {
            new(SessionBodyKind.ClientRequest, request),
            new(SessionBodyKind.ClientResponse, response),
        };
        if (metadata is not null) bodies.Add(new SessionBodyInput(SessionBodyKind.TurnMetadata, Encoding.UTF8.GetBytes(metadata)));
        if (extracts is not null) bodies.Add(new SessionBodyInput(SessionBodyKind.Extracts, Encoding.UTF8.GetBytes(extracts)));
        return new SessionTurnInput(turnId, DateTimeOffset.UtcNow, bodies);
    }

    private static void AppendExpected(
        IncrementalHash hash, Guid turnId, string? request, string? response, string? providerRequest, string? providerResponse)
    {
        hash.AppendData(turnId.ToByteArray());
        var bodies = new[] { request, response, providerRequest, providerResponse };
        for (var i = 0; i < bodies.Length; i++)
        {
            hash.AppendData([(byte)(i + 1)]);
            hash.AppendData(bodies[i] is null ? new byte[32] : SHA256.HashData(Encoding.UTF8.GetBytes(bodies[i]!)));
        }
    }
}
