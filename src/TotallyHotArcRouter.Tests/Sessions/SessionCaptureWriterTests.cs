using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins the capture-only writer: turns reach the store off the caller's thread, spools are always disposed,
/// a failing write never escapes, and shutdown drains the queue.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class SessionCaptureWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot, "session-writer-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly SessionIndex _index;
    private readonly SessionStore _store;

    /// <summary>Builds a store over a scratch database and folder with an in-memory master key.</summary>
    public SessionCaptureWriterTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Path.Combine(_root, "sessions");
        var database = new TranscriptDatabase(Options.Create(
            new StorageOptions { TranscriptDatabasePath = Path.Combine(_root, "transcripts.db") }));
        _index = new SessionIndex(database);
        _index.EnsureCreated();
        _store = new SessionStore(_index, new InMemoryMasterKeyStore(), _folder);
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

    /// <summary>A queued turn is written, readable, and its spool file is gone afterwards.</summary>
    [Fact]
    public async Task Enqueue_WritesTurnAndDisposesSpool()
    {
        using var writer = NewWriter();
        await writer.StartAsync(CancellationToken.None);
        var spool = SessionBodySpool.Create(_folder);
        Assert.True(spool.TryWrite("response text"u8));
        Assert.True(spool.TryComplete());

        Assert.True(await writer.EnqueueAsync(new SessionCaptureItem("client-1", Turn(spool))));
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(5)));

        var sessionId = _store.ResolveArchiveSessionId("client-1");
        Assert.Equal(["request", "response text"], _store.ReadBodies(sessionId).Select(f => Encoding.UTF8.GetString(f.Plaintext!)));
        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>A write that fails is swallowed and logged, the spool is still disposed, and the next turn is written.</summary>
    [Fact]
    public async Task Enqueue_FailingWrite_DoesNotStopTheWriter()
    {
        using var writer = NewWriter();
        await writer.StartAsync(CancellationToken.None);
        var spool = SessionBodySpool.Create(_folder);
        Assert.True(spool.TryWrite("x"u8));
        Assert.True(spool.TryComplete());
        var bad = new SessionTurnInput(SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow, []);

        await writer.EnqueueAsync(new SessionCaptureItem("client-1", bad));
        await writer.EnqueueAsync(new SessionCaptureItem("client-2", Turn(spool)));

        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(_store.ReadBodies(_store.ResolveArchiveSessionId("client-2")).Where(f => f.Plaintext is { Length: > 0 }), f => f.Kind == SessionBodyKind.ClientResponse);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>Stopping drains what is queued, and a turn offered after the stop is refused with its spool disposed.</summary>
    [Fact]
    public async Task Stop_DrainsQueue_ThenRefusesNewTurns()
    {
        using var writer = NewWriter();
        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < 20; i++)
        {
            await writer.EnqueueAsync(new SessionCaptureItem("client-1", Turn(spool: null)));
        }

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(20, _store.ListTurns(_store.ResolveArchiveSessionId("client-1")).Count);
        var late = SessionBodySpool.Create(_folder);
        Assert.True(late.TryComplete());
        Assert.False(await writer.EnqueueAsync(new SessionCaptureItem("client-1", Turn(late))));
        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));
    }

    /// <summary>Startup recovery deletes a spool a crash left in the folder.</summary>
    [Fact]
    public async Task Startup_DeletesLeftoverSpool()
    {
        Directory.CreateDirectory(_folder);
        var leftover = Path.Combine(_folder, "crashed" + SessionBodySpool.FileExtension);
        await File.WriteAllBytesAsync(leftover, [1, 2, 3]);
        var service = new SessionStoreStartupService(_index, _store, NullLogger<SessionStoreStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(leftover));
    }

    private SessionCaptureWriter NewWriter() =>
        new(_store, Options.Create(new SessionCaptureOptions { ShutdownDrainTimeout = TimeSpan.FromSeconds(10) }),
            NullLogger<SessionCaptureWriter>.Instance);

    private static SessionTurnInput Turn(SessionBodySpool? spool) =>
        new(SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow,
        [
            new SessionBodyInput(SessionBodyKind.ClientRequest, "request"u8.ToArray()),
            spool is null
                ? new SessionBodyInput(SessionBodyKind.ClientResponse, "response"u8.ToArray())
                : new SessionBodyInput(SessionBodyKind.ClientResponse, null, spool),
        ]);
}
