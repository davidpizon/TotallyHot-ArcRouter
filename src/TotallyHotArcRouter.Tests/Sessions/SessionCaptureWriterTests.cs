using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins the capture-only writer and its startup: turns reach the store off the caller's thread, spools are
/// always disposed, a failing write never escapes, shutdown drains the queue, and a router that has captured
/// nothing opens no storage until its first turn.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class SessionCaptureWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot, "session-writer-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly string _databasePath;
    private readonly TranscriptDatabase _database;
    private readonly SessionIndex _index;
    private readonly SessionStore _store;

    /// <summary>Builds a store over a scratch database and folder with an in-memory master key.</summary>
    public SessionCaptureWriterTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Path.Combine(_root, "sessions");
        _databasePath = Path.Combine(_root, "transcripts.db");
        _database = new TranscriptDatabase(Options.Create(new StorageOptions { TranscriptDatabasePath = _databasePath }));
        _index = new SessionIndex(_database);
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
        var frames = _store.ReadBodies(_store.ResolveArchiveSessionId("client-2"));
        Assert.Equal("x", Encoding.UTF8.GetString(frames.Single(f => f.Kind == SessionBodyKind.ClientResponse).Plaintext!));
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

    /// <summary>A writer that was never started has nothing reading its queue, so stopping releases the queued spools at once.</summary>
    [Fact]
    public async Task Stop_WithoutStart_ReleasesQueuedSpoolsWithoutWaitingOutTheDrain()
    {
        using var writer = new SessionCaptureWriter(
            new Lazy<SessionStore>(() => _store),
            Options.Create(new SessionCaptureOptions { ShutdownDrainTimeout = TimeSpan.FromSeconds(30) }),
            NullLogger<SessionCaptureWriter>.Instance);
        var spool = SessionBodySpool.Create(_folder);
        Assert.True(spool.TryComplete());
        Assert.True(await writer.EnqueueAsync(new SessionCaptureItem("client-1", Turn(spool))));

        var stopped = writer.StopAsync(CancellationToken.None);

        Assert.True(await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(5))) == stopped, "Stop waited for a drain nobody was running.");
        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));
        Assert.Empty(_store.ListTurns(_store.ResolveArchiveSessionId("client-1")));
    }

    /// <summary>Startup recovery deletes a spool a crash left in the folder.</summary>
    [Fact]
    public async Task Startup_DeletesLeftoverSpool()
    {
        var leftover = Path.Combine(_folder, "crashed" + SessionBodySpool.FileExtension);
        await File.WriteAllBytesAsync(leftover, [1, 2, 3]);
        var service = NewStartup(new Lazy<SessionStore>(() =>
        {
            var store = new SessionStore(_index, new InMemoryMasterKeyStore(), _folder);
            store.RecoverOnStartup();
            return store;
        }));

        await service.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(leftover));
    }

    /// <summary>With no database and no session folder, startup opens nothing, so a fresh install creates no database.</summary>
    [Fact]
    public async Task Startup_NothingCapturedYet_DoesNotOpenTheStore()
    {
        var empty = Path.Combine(_root, "fresh");
        var database = new TranscriptDatabase(Options.Create(
            new StorageOptions { TranscriptDatabasePath = Path.Combine(empty, "transcripts.db") }));
        var opened = 0;
        var service = new SessionStoreStartupService(
            new Lazy<SessionStore>(() =>
            {
                opened++;
                return _store;
            }),
            database,
            NullLogger<SessionStoreStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(0, opened);
        Assert.False(Directory.Exists(empty));
    }

    /// <summary>An existing database or session folder means there may be something to recover, so startup opens the store.</summary>
    [Fact]
    public async Task Startup_ExistingDatabase_OpensTheStore()
    {
        var opened = 0;
        var service = NewStartup(new Lazy<SessionStore>(() =>
        {
            opened++;
            return _store;
        }));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, opened);
    }

    /// <summary>A store that cannot be opened is logged, not thrown, so capture never stops the proxy from starting.</summary>
    [Fact]
    public async Task Startup_StoreThatCannotOpen_DoesNotThrow()
    {
        var service = NewStartup(new Lazy<SessionStore>(() => throw new IOException("The sessions folder is unreadable.")));

        var exception = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    /// <summary>
    /// The real registration builds a working capture path: nothing opens at startup on a fresh data directory,
    /// the first captured turn opens (and recovers) the store, and the turn reads back.
    /// </summary>
    [Fact]
    public async Task AddSessionCapture_OpensLazilyAndStoresTheFirstTurn()
    {
        var data = Path.Combine(_root, "wired");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(new StorageOptions { TranscriptDatabasePath = Path.Combine(data, "transcripts.db") }));
        services.AddSingleton<TranscriptDatabase>();
        services.AddSingleton(new ProtectedSecretStore(Path.Combine(data, "secrets.dat")));
        services.AddSessionCapture();
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        var writer = provider.GetRequiredService<SessionCaptureWriter>();

        foreach (var service in hosted) await service.StartAsync(CancellationToken.None);

        Assert.Contains(hosted, h => h is SessionStoreStartupService);
        Assert.Contains(hosted, h => ReferenceEquals(h, writer));
        Assert.False(File.Exists(Path.Combine(data, "transcripts.db")));

        Assert.True(await writer.EnqueueAsync(new SessionCaptureItem("client-9", Turn(spool: null))));
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(10)));

        var store = provider.GetRequiredService<SessionStore>();
        var frames = store.ReadBodies(store.ResolveArchiveSessionId("client-9"));
        Assert.Equal(["request", "response"], frames.Select(f => Encoding.UTF8.GetString(f.Plaintext!)));
        foreach (var service in hosted.AsEnumerable().Reverse()) await service.StopAsync(CancellationToken.None);
    }

    private SessionStoreStartupService NewStartup(Lazy<SessionStore> store) =>
        new(store, _database, NullLogger<SessionStoreStartupService>.Instance);

    private SessionCaptureWriter NewWriter() =>
        new(new Lazy<SessionStore>(() => _store),
            Options.Create(new SessionCaptureOptions { ShutdownDrainTimeout = TimeSpan.FromSeconds(10) }),
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
