using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins how captured sessions are deleted (ADR-0019, "Retention" and "Deletion"): the newest N turns are kept by
/// removing whole oldest sessions, the newest session is never removed, Clear removes everything, and none of it
/// opens session storage on a router that never captured a turn.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class SessionRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot, "session-retention-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly TranscriptDatabase _database;
    private readonly SessionStore _store;

    /// <summary>Builds a store over a scratch database and folder with an in-memory master key.</summary>
    public SessionRetentionTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Path.Combine(_root, "sessions");
        _database = new TranscriptDatabase(Options.Create(new StorageOptions
        {
            TranscriptDatabasePath = Path.Combine(_root, "transcripts.db")
        }));
        var index = new SessionIndex(_database);
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

    /// <summary>Over the limit, whole sessions go oldest first until the rest fits, and the newest session stays.</summary>
    [Fact]
    public void EnforceRetention_DeletesWholeOldestSessionsFirst()
    {
        var oldest = AddSession("a", turns: 3, firstMinute: 0);
        var middle = AddSession("b", turns: 3, firstMinute: 10);
        var newest = AddSession("c", turns: 3, firstMinute: 20);

        var result = _store.EnforceRetention(maxTurns: 5);

        Assert.Equal(2, result.DeletedSessions);
        Assert.True(result.MasterKeyRotated);
        Assert.Empty(_store.ListTurns(oldest));
        Assert.Empty(_store.ListTurns(middle));
        Assert.Equal(3, _store.ListTurns(newest).Count);
        Assert.Single(Directory.GetFiles(_folder));
    }

    /// <summary>A session is ranked by its last turn, so an old session that is still being written is not the oldest.</summary>
    [Fact]
    public void EnforceRetention_RanksSessionsByTheirLastTurn()
    {
        var startedFirstButActive = AddSession("a", turns: 2, firstMinute: 0);
        var startedLater = AddSession("b", turns: 2, firstMinute: 10);
        _store.AppendTurn(startedFirstButActive, "a", Turn(Start.AddMinutes(30)));

        _store.EnforceRetention(maxTurns: 3);

        Assert.Empty(_store.ListTurns(startedLater));
        Assert.Equal(3, _store.ListTurns(startedFirstButActive).Count);
    }

    /// <summary>The newest session is kept even when it alone exceeds the limit, because it may be the one being written.</summary>
    [Fact]
    public void EnforceRetention_NeverDeletesTheNewestSession()
    {
        var only = AddSession("a", turns: 6, firstMinute: 0);

        var result = _store.EnforceRetention(maxTurns: 2);

        Assert.Equal(0, result.DeletedSessions);
        Assert.False(result.MasterKeyRotated);
        Assert.Equal(6, _store.ListTurns(only).Count);
    }

    /// <summary>A non-positive Sample Size is a misconfiguration, not a policy, and must not wipe the store.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void EnforceRetention_WithNonPositiveLimit_DeletesNothing(int maxTurns)
    {
        AddSession("a", turns: 3, firstMinute: 0);
        AddSession("b", turns: 3, firstMinute: 10);

        var result = _store.EnforceRetention(maxTurns);

        Assert.Equal(0, result.DeletedSessions);
        Assert.Equal(2, _store.ListSessions().Count);
    }

    /// <summary>At or under the limit nothing is deleted and the key is not rotated.</summary>
    [Fact]
    public void EnforceRetention_WithinTheLimit_ChangesNothing()
    {
        AddSession("a", turns: 2, firstMinute: 0);
        AddSession("b", turns: 2, firstMinute: 10);

        var result = _store.EnforceRetention(maxTurns: 4);

        Assert.Equal(0, result.DeletedSessions);
        Assert.False(result.MasterKeyRotated);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    /// <summary>Clear removes every session, files included, and rotates the master key.</summary>
    [Fact]
    public void DeleteAllSessions_LeavesNoSessionOrFile()
    {
        AddSession("a", turns: 2, firstMinute: 0);
        AddSession("b", turns: 2, firstMinute: 10);

        var result = _store.DeleteAllSessions();

        Assert.Equal(2, result.DeletedSessions);
        Assert.True(result.WalTruncated);
        Assert.True(result.MasterKeyRotated);
        Assert.Empty(_store.ListSessions());
        Assert.Empty(Directory.GetFiles(_folder));
    }

    /// <summary>A router that never captured a turn has no folder, so neither operation opens the store.</summary>
    [Fact]
    public async Task Maintenance_WhenNothingWasEverCaptured_DoesNotOpenTheStore()
    {
        var neverCreated = new TranscriptDatabase(Options.Create(new StorageOptions
        {
            TranscriptDatabasePath = Path.Combine(_root, "fresh", "transcripts.db")
        }));
        var opened = false;
        var maintenance = new SessionMaintenance(
            new Lazy<SessionStore>(() =>
            {
                opened = true;
                return _store;
            }),
            neverCreated);

        var all = await maintenance.DeleteAllAsync();
        var retained = maintenance.EnforceRetention(10);

        Assert.False(opened);
        Assert.Equal(0, all.Deletion.DeletedSessions);
        Assert.True(all.IsFinal);
        Assert.Equal(0, retained.DeletedSessions);
    }

    /// <summary>Clear invalidates in-flight turns even on a router that has captured nothing yet, so a first turn still being relayed is not stored after it.</summary>
    [Fact]
    public async Task DeleteAllAsync_AdvancesTheEpochEvenWhenNothingWasCaptured()
    {
        var epoch = new CaptureEpoch();
        var neverCreated = new TranscriptDatabase(Options.Create(new StorageOptions
        {
            TranscriptDatabasePath = Path.Combine(_root, "fresh", "transcripts.db")
        }));
        var maintenance = new SessionMaintenance(new Lazy<SessionStore>(() => _store), neverCreated, epoch: epoch);
        var before = epoch.Current;

        await maintenance.DeleteAllAsync();

        Assert.True(epoch.Current > before);
    }

    /// <summary>A writer that cannot drain in time makes Clear report that it is not final, so the caller can repeat it.</summary>
    [Fact]
    public async Task DeleteAllAsync_WhenTheWriterCannotDrain_IsNotFinal()
    {
        Directory.CreateDirectory(_folder);

        // Never started, so nothing ever reads its queue and it never goes idle.
        using var writer = new SessionCaptureWriter(
            new Lazy<SessionStore>(() => _store), Options.Create(new SessionCaptureOptions()),
            NullLogger<SessionCaptureWriter>.Instance);
        await writer.EnqueueAsync(new SessionCaptureItem("stuck", Turn(Start)));
        var maintenance = new SessionMaintenance(
            new Lazy<SessionStore>(() => _store), _database, writer, TimeSpan.FromMilliseconds(200));

        var result = await maintenance.DeleteAllAsync();

        Assert.False(result.QueueDrained);
        Assert.False(result.IsFinal);
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// A session that gained a turn after retention chose its candidates is no longer an old one; deleting it
    /// would delete the turn just written, so it is skipped.
    /// </summary>
    [Fact]
    public void DeleteUnchanged_SkipsASessionThatGainedATurnAfterTheSnapshot()
    {
        var oldest = AddSession("a", turns: 3, firstMinute: 0);
        var newest = AddSession("b", turns: 3, firstMinute: 10);
        var candidates = _store.SelectExpiredSessions(maxTurns: 3);
        Assert.Equal(oldest, Assert.Single(candidates).ArchiveSessionId);

        _store.AppendTurn(oldest, "a", Turn(Start.AddMinutes(30)));
        var result = _store.DeleteUnchanged(candidates);

        Assert.Equal(0, result.DeletedSessions);
        Assert.False(result.MasterKeyRotated);
        Assert.Equal(4, _store.ListTurns(oldest).Count);
        Assert.Equal(3, _store.ListTurns(newest).Count);
    }

    /// <summary>A candidate nothing has touched is deleted as before, and the master key rotates.</summary>
    [Fact]
    public void DeleteUnchanged_DeletesAnUntouchedCandidate()
    {
        var oldest = AddSession("a", turns: 3, firstMinute: 0);
        AddSession("b", turns: 3, firstMinute: 10);

        var result = _store.DeleteUnchanged(_store.SelectExpiredSessions(maxTurns: 3));

        Assert.Equal(1, result.DeletedSessions);
        Assert.True(result.MasterKeyRotated);
        Assert.Empty(_store.ListTurns(oldest));
    }

    /// <summary>
    /// A turn that finished just before Clear sits in the capture writer's queue. Clear invalidates it (its request
    /// holds the history being wiped) and waits for the writer to go idle, so nothing from before the wipe is on
    /// disk afterwards, whichever way the race between the queue and the deletion falls.
    /// </summary>
    [Fact]
    public async Task DeleteAllAsync_LetsTurnsAlreadyQueuedLandFirst()
    {
        Directory.CreateDirectory(_folder);

        // Not disposed: nothing asks it for a wait handle, so it holds no operating-system resource, and the
        // writer's thread still reads it until the test has stopped the writer.
        var gate = new ManualResetEventSlim();

        // Opening the store blocks the writer's consumer, so the turn stays queued while Clear starts.
        var lazy = new Lazy<SessionStore>(() =>
        {
            gate.Wait();
            return _store;
        });
        using var writer = new SessionCaptureWriter(
            lazy, Options.Create(new SessionCaptureOptions()), NullLogger<SessionCaptureWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        var maintenance = new SessionMaintenance(lazy, _database, writer, TimeSpan.FromSeconds(30));
        await writer.EnqueueAsync(new SessionCaptureItem("queued", Turn(Start)));

        // On a pool thread: should the wait ever be skipped, Clear would block opening the store behind the writer,
        // and the test must then fail instead of hanging on the gate it releases itself.
        var clear = Task.Run(maintenance.DeleteAllAsync);
        await Task.Delay(100);
        gate.Set();
        var result = await clear.WaitAsync(TimeSpan.FromSeconds(30));
        await writer.StopAsync(CancellationToken.None);

        Assert.True(result.IsFinal);
        Assert.Empty(_store.ListSessions());
    }

    /// <summary>Deleting a session removes its transcript rows and the embeddings those rows pointed at.</summary>
    [Fact]
    public void EnforceRetention_DeletesTranscriptRowsAndMemoryEntries()
    {
        var oldest = AddSession("a", turns: 3, firstMinute: 0);
        AddSession("b", turns: 3, firstMinute: 10);
        var transcripts = new SqliteTranscriptStore(
            _database,
            new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }));
        transcripts.InsertAsync(new TranscriptRecord(
            0, "a:1", Start, "gpt-5.4", "kimi-k2.5", null, null, null, false,
            "secret prompt", "secret reply", null, null, false, 1, null, null, 42,
            ArchiveSessionId: oldest, ArchiveTurnId: Guid.CreateVersion7())).GetAwaiter().GetResult();
        var deletedMemory = new List<long>();
        var memory = new Mock<IMemoryEntryStore>();
        memory.Setup(store => store.DeleteManyAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<long>, CancellationToken>((ids, _) => deletedMemory.AddRange(ids))
            .Returns(Task.CompletedTask);
        var maintenance = new SessionMaintenance(
            new Lazy<SessionStore>(() => _store),
            _database,
            transcripts: transcripts,
            memory: memory.Object);

        var result = maintenance.EnforceRetention(maxTurns: 3);

        Assert.Equal(1, result.DeletedSessions);
        Assert.Equal([42L], deletedMemory);
        Assert.Empty(transcripts.ListSessionsAsync(10).GetAwaiter().GetResult());
    }

    /// <summary>The retention service reads the Sample Size live and trims to it.</summary>
    [Fact]
    public void RetentionService_TrimsToTheLiveSampleSize()
    {
        AddSession("a", turns: 3, firstMinute: 0);
        var kept = AddSession("b", turns: 3, firstMinute: 10);
        var monitor = new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { EmbeddingMemoryCapacity = 100 });
        using var service = new SessionRetentionService(
            new SessionMaintenance(new Lazy<SessionStore>(() => _store), _database),
            monitor,
            NullLogger<SessionRetentionService>.Instance);

        Assert.Equal(0, service.RunOnce()!.DeletedSessions);

        monitor.Set(new RoutingOptions { EmbeddingMemoryCapacity = 3 });
        var result = service.RunOnce();

        Assert.Equal(1, result!.DeletedSessions);
        Assert.Equal(3, _store.ListTurns(kept).Count);
    }

    /// <summary>A failing pass is reported as a failure, not thrown, so the loop survives to retry.</summary>
    [Fact]
    public void RetentionService_WhenAPassFails_ReturnsNullInsteadOfThrowing()
    {
        Directory.CreateDirectory(_folder);
        using var service = new SessionRetentionService(
            new SessionMaintenance(new Lazy<SessionStore>(() => throw new InvalidOperationException("cannot open")), _database),
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions()),
            NullLogger<SessionRetentionService>.Instance);

        Assert.Null(service.RunOnce());
    }

    private Guid AddSession(string clientId, int turns, int firstMinute)
    {
        var id = _store.ResolveArchiveSessionId(clientId);
        for (var i = 0; i < turns; i++) _store.AppendTurn(id, clientId, Turn(Start.AddMinutes(firstMinute + i)));

        return id;
    }

    private static SessionTurnInput Turn(DateTimeOffset at) => new(
        SessionArchiveIds.NewArchiveTurnId(),
        at,
        [new SessionBodyInput(SessionBodyKind.ClientRequest, "request"u8.ToArray()),
         new SessionBodyInput(SessionBodyKind.ClientResponse, "response"u8.ToArray())]);
}
