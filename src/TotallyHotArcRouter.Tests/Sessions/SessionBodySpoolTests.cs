using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Logging;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins the bounded capture of ADR-0019 for #165 phase 1: a body streamed through the spool commits to a
/// session file byte-exact (apart from obscured secrets), a body that cannot be captured completely is
/// recorded as missing instead of a prefix, and no plaintext reaches the spool file.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class SessionBodySpoolTests : IDisposable
{
    private const string Canary = "CANARY-spooled-body-text-77ab";
    private const string AnthropicKey = "sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot, "session-spool-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly SessionStore _store;

    /// <summary>Builds a store over a scratch database and folder with an in-memory master key.</summary>
    public SessionBodySpoolTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Path.Combine(_root, "sessions");
        var database = new TranscriptDatabase(Options.Create(
            new StorageOptions { TranscriptDatabasePath = Path.Combine(_root, "transcripts.db") }));
        var index = new SessionIndex(database);
        index.EnsureCreated();
        _store = new SessionStore(index, new InMemoryMasterKeyStore(), _folder);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch cleanup is also done by TestTempDirectorySweeper.
        }
    }

    /// <summary>A spooled body commits to the session file and reads back equal to the one-shot obscured body.</summary>
    [Fact]
    public void Commit_SpooledBodyRoundTripsAndObscuresSecrets()
    {
        var body = Encoding.UTF8.GetBytes($$"""{"messages":[{"role":"user","content":"{{Canary}} {{AnthropicKey}}"}]}""");
        using var spool = SpoolOf(body, chunk: 7);

        var frames = Commit(spool, request: "{}"u8.ToArray());

        Assert.Equal(Encoding.UTF8.GetBytes(SecretObscurer.Obscure(Encoding.UTF8.GetString(body))), frames[1].Plaintext);
        Assert.DoesNotContain(AnthropicKey, Encoding.UTF8.GetString(frames[1].Plaintext!));
        Assert.Contains(Canary, Encoding.UTF8.GetString(frames[1].Plaintext!));
    }

    /// <summary>A response far over the 4 MiB telemetry cap is stored whole, including bytes that are not UTF-8.</summary>
    [Fact]
    public void Commit_ResponseOverFourMebibytes_IsStoredWhole()
    {
        var body = new byte[6 * 1024 * 1024];
        new Random(165).NextBytes(body);
        using var spool = SpoolOf(body, chunk: 100_000);

        var frames = Commit(spool, request: "{}"u8.ToArray());

        Assert.Equal(body.Length, frames[1].Plaintext!.Length);
        Assert.True(body.AsSpan().SequenceEqual(frames[1].Plaintext));
    }

    /// <summary>The spool file never holds the body's plaintext, and is removed when its owner disposes it.</summary>
    [Fact]
    public void Spool_FileHoldsNoPlaintext_AndIsDeletedOnDispose()
    {
        var body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Canary + " ", 5_000)));
        string path;
        using (var spool = SessionBodySpool.Create(_folder))
        {
            Assert.True(spool.TryWrite(body));
            Assert.True(spool.TryComplete());
            path = Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension).Single();

            var onDisk = File.ReadAllBytes(path);
            Assert.True(onDisk.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Canary[..12])) < 0);
        }

        Assert.False(File.Exists(path));
    }

    /// <summary>A match that outgrows the obscurer window abandons the spool: the file is gone and the body is recorded missing.</summary>
    [Fact]
    public void Spool_WindowOverflow_AbandonsAndCommitsAsMissing()
    {
        using var spool = SessionBodySpool.Create(_folder, windowChars: 256);
        Assert.True(spool.TryWrite("-----BEGIN RSA PRIVATE KEY-----\n"u8));
        var accepted = true;
        for (var i = 0; i < 50 && accepted; i++) accepted = spool.TryWrite(Encoding.UTF8.GetBytes(new string('M', 63) + "\n"));

        Assert.False(accepted);
        Assert.True(spool.IsAbandoned);
        Assert.False(spool.TryComplete());
        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));

        var frames = Commit(spool, request: "{}"u8.ToArray());
        Assert.Null(frames[1].Plaintext);
    }

    /// <summary>Free disk space below the reserve abandons the capture instead of filling the disk.</summary>
    [Fact]
    public void Spool_BelowDiskReserve_Abandons()
    {
        using var spool = SessionBodySpool.Create(_folder, minFreeBytes: 1_000, freeSpace: _ => 10);
        var body = new byte[300_000];
        new Random(1).NextBytes(body);

        var written = spool.TryWrite(body) && spool.TryComplete();

        Assert.False(written);
        Assert.True(spool.IsAbandoned);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));
    }

    /// <summary>A spool that was never finished is recorded as missing, not as a prefix of the body.</summary>
    [Fact]
    public void Commit_UnfinishedSpool_IsRecordedAsMissing()
    {
        using var spool = SessionBodySpool.Create(_folder);
        Assert.True(spool.TryWrite("partial body"u8));

        var frames = Commit(spool, request: "{}"u8.ToArray());

        Assert.Null(frames[1].Plaintext);
    }

    /// <summary>A body given both in memory and as a spool is refused before anything is written.</summary>
    [Fact]
    public void AppendTurn_BodyAndSpoolTogether_IsRejected()
    {
        using var spool = SpoolOf("x"u8.ToArray(), chunk: 1);
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        var turn = new SessionTurnInput(
            SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow,
            [new SessionBodyInput(SessionBodyKind.ClientRequest, "x"u8.ToArray(), spool)]);

        Assert.Throws<ArgumentException>(() => _store.AppendTurn(sessionId, "client-1", turn));
    }

    /// <summary>An empty body is a legitimately empty body, distinct from a missing one.</summary>
    [Fact]
    public void Commit_EmptySpool_ReadsBackEmptyNotMissing()
    {
        using var spool = SessionBodySpool.Create(_folder);
        Assert.True(spool.TryComplete());

        var frames = Commit(spool, request: "{}"u8.ToArray());

        Assert.NotNull(frames[1].Plaintext);
        Assert.Empty(frames[1].Plaintext!);
    }

    /// <summary>A flipped byte in a spool chunk fails authentication at commit and nothing is committed for the turn.</summary>
    [Fact]
    public void Commit_TamperedSpool_FailsAndCommitsNothing()
    {
        var body = new byte[200_000];
        new Random(7).NextBytes(body);
        using var spool = SpoolOf(body, chunk: 50_000);
        var path = Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension).Single();
        var bytes = File.ReadAllBytes(path);
        bytes[200] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var sessionId = _store.ResolveArchiveSessionId("client-1");
        var turn = TurnWith(spool, "{}"u8.ToArray());

        Assert.ThrowsAny<CryptographicException>(() => _store.AppendTurn(sessionId, "client-1", turn));
        Assert.Empty(_store.ListTurns(sessionId));
    }

    /// <summary>The same spooled capture can seed two sessions' worth of turns in order.</summary>
    [Fact]
    public void Commit_TwoSpooledTurns_KeepOrderAndPositions()
    {
        using var first = SpoolOf("first"u8.ToArray(), chunk: 2);
        using var second = SpoolOf("second"u8.ToArray(), chunk: 2);
        var sessionId = _store.ResolveArchiveSessionId("client-1");

        _store.AppendTurn(sessionId, "client-1", TurnWith(first, "r1"u8.ToArray()));
        _store.AppendTurn(sessionId, "client-1", TurnWith(second, "r2"u8.ToArray()));

        Assert.Equal(["r1", "first", "r2", "second"], _store.ReadBodies(sessionId).Select(f => Encoding.UTF8.GetString(f.Plaintext!)));
    }

    private SessionBodySpool SpoolOf(byte[] body, int chunk)
    {
        var spool = SessionBodySpool.Create(_folder);
        foreach (var piece in body.Chunk(chunk)) Assert.True(spool.TryWrite(piece));
        Assert.True(spool.TryComplete());
        return spool;
    }

    private static SessionTurnInput TurnWith(SessionBodySpool response, byte[] request) =>
        new(SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow,
        [
            new SessionBodyInput(SessionBodyKind.ClientRequest, request),
            new SessionBodyInput(SessionBodyKind.ClientResponse, null, response),
        ]);

    private IReadOnlyList<SessionBodyFrame> Commit(SessionBodySpool response, byte[] request)
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", TurnWith(response, request));
        return _store.ReadBodies(sessionId);
    }
}
