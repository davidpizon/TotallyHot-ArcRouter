using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Storage;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins the storage layer of ADR-0019 for #165 phase 1: commit order, per-session serialization, startup
/// recovery, final deletion with master-key rotation, and that the SQLite index holds no conversation text.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class SessionStoreTests : IDisposable
{
    private const string Canary = "CANARY-conversation-text-9f2c";

    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot, "session-store-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMasterKeyStore _masterKeys = new();
    private readonly string _databasePath;
    private readonly string _folder;
    private readonly SessionIndex _index;
    private readonly SessionStore _store;

    /// <summary>Builds a store over a scratch database and folder with an in-memory master key.</summary>
    public SessionStoreTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "transcripts.db");
        _folder = Path.Combine(_root, "sessions");
        var database = new TranscriptDatabase(Options.Create(new StorageOptions { TranscriptDatabasePath = _databasePath }));
        _index = new SessionIndex(database);
        _index.EnsureCreated();
        _store = new SessionStore(_index, _masterKeys, _folder);
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

    /// <summary>A turn's bodies round-trip byte-exact and its index row records where they sit.</summary>
    [Fact]
    public void AppendTurn_RoundTripsBodiesAndIndexesTheTurn()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        var request = """{"messages":[{"role":"user","content":"hello"}]}"""u8.ToArray();
        var response = """{"content":"hi"}"""u8.ToArray();
        var turn = NewTurn(request, response);

        var row = _store.AppendTurn(sessionId, "client-1", turn);

        Assert.Equal(0, row.TurnSequence);
        Assert.Equal(0, row.FirstFrameOrdinal);
        Assert.Equal(2, row.FrameCount);
        Assert.Equal(SessionTurnOrigin.Captured, row.Origin);
        var frames = _store.ReadBodies(sessionId);
        Assert.Equal(2, frames.Count);
        Assert.Equal(request, frames[0].Plaintext);
        Assert.Equal(response, frames[1].Plaintext);
        Assert.Equal(turn.ArchiveTurnId, frames[0].ArchiveTurnId);
        Assert.Equal(row, Assert.Single(_store.ListTurns(sessionId)));
    }

    /// <summary>Later turns continue the sequence and frame ordinals of the same file.</summary>
    [Fact]
    public void AppendTurn_SecondTurnContinuesSequenceAndOrdinals()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));

        var second = _store.AppendTurn(sessionId, "client-1", NewTurn("c"u8.ToArray(), "d"u8.ToArray()));

        Assert.Equal(1, second.TurnSequence);
        Assert.Equal(2, second.FirstFrameOrdinal);
        Assert.Equal(["a", "b", "c", "d"], _store.ReadBodies(sessionId).Select(f => Encoding.UTF8.GetString(f.Plaintext!)));
        Assert.Equal(2, _store.ListTurns(sessionId).Count);
    }

    /// <summary>A body that could not be captured reads back as absent, not as an empty body.</summary>
    [Fact]
    public void AppendTurn_MissingBodyReadsBackAsNull()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        var turn = new SessionTurnInput(
            SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow,
            [new SessionBodyInput(SessionBodyKind.ClientRequest, "req"u8.ToArray()),
             new SessionBodyInput(SessionBodyKind.ClientResponse, null)]);

        _store.AppendTurn(sessionId, "client-1", turn);

        var frames = _store.ReadBodies(sessionId);
        Assert.NotNull(frames[0].Plaintext);
        Assert.Null(frames[1].Plaintext);
    }

    /// <summary>
    /// The same client id keeps landing in one session, including before its first turn commits, so
    /// concurrent first requests of one client session do not split it.
    /// </summary>
    [Fact]
    public void ResolveArchiveSessionId_ReusesTheSessionBeforeAndAfterItExists()
    {
        var first = _store.ResolveArchiveSessionId("client-1");
        Assert.Equal(first, _store.ResolveArchiveSessionId("client-1"));

        _store.AppendTurn(first, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));

        Assert.Equal(first, _store.ResolveArchiveSessionId("client-1"));
        Assert.NotEqual(first, _store.ResolveArchiveSessionId("client-2"));
    }

    /// <summary>Two sessions with the same client id on different archive ids stay separate.</summary>
    [Fact]
    public void AppendTurn_SameClientIdUnderTwoArchiveIdsStaysSeparate()
    {
        var a = SessionArchiveIds.NewArchiveSessionId();
        var b = SessionArchiveIds.NewArchiveSessionId();

        _store.AppendTurn(a, "same", NewTurn("a1"u8.ToArray(), "a2"u8.ToArray()));
        _store.AppendTurn(b, "same", NewTurn("b1"u8.ToArray(), "b2"u8.ToArray()));

        Assert.Equal("a1", Encoding.UTF8.GetString(_store.ReadBodies(a)[0].Plaintext!));
        Assert.Equal("b1", Encoding.UTF8.GetString(_store.ReadBodies(b)[0].Plaintext!));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    /// <summary>A turn needs a body to commit.</summary>
    [Fact]
    public void AppendTurn_WithNoBodiesIsRejected()
    {
        var turn = new SessionTurnInput(SessionArchiveIds.NewArchiveTurnId(), DateTimeOffset.UtcNow, []);

        Assert.Throws<ArgumentException>(
            () => _store.AppendTurn(SessionArchiveIds.NewArchiveSessionId(), "client-1", turn));
    }

    /// <summary>An imported turn keeps its origin in the index (phase 4 will write these for real).</summary>
    [Fact]
    public void AppendTurn_RecordsImportedOrigin()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        var turn = new SessionTurnInput(
            SessionArchiveIds.NewArchiveTurnId(),
            DateTimeOffset.UtcNow,
            [new SessionBodyInput(SessionBodyKind.ClientRequest, "a"u8.ToArray())],
            Origin: SessionTurnOrigin.Imported);

        var row = _store.AppendTurn(sessionId, "client-1", turn);

        Assert.Equal(SessionTurnOrigin.Imported, row.Origin);
        Assert.Equal(SessionTurnOrigin.Imported, Assert.Single(_store.ListTurns(sessionId)).Origin);
    }

    /// <summary>Concurrent appends to one session serialize into distinct, gap-free sequences.</summary>
    [Fact]
    public async Task AppendTurn_ConcurrentAppendsToOneSessionGetDistinctSequences()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            _store.AppendTurn(sessionId, "client-1", NewTurn(Encoding.UTF8.GetBytes($"q{i}"), Encoding.UTF8.GetBytes($"a{i}"))))));

        var turns = _store.ListTurns(sessionId);
        Assert.Equal(Enumerable.Range(0, 24), turns.Select(t => t.TurnSequence));
        Assert.Equal(48, _store.ReadBodies(sessionId).Count);
    }

    /// <summary>Neither the index nor the folder shows conversation text in the clear.</summary>
    [Fact]
    public void Storage_HoldsNoPlaintextConversationText()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn(Encoding.UTF8.GetBytes(Canary), Encoding.UTF8.GetBytes(Canary)));
        _index.TruncateWal();

        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            Assert.DoesNotContain(Canary, Encoding.Latin1.GetString(buffer.ToArray()));
        }
    }

    /// <summary>Frames flushed without a committed index row are cut away at startup.</summary>
    [Fact]
    public void Recover_TruncatesFramesBeyondTheCommittedExtent()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        AppendUncommittedFrame(sessionId);

        var result = _store.RecoverOnStartup();

        Assert.Equal(1, result.TruncatedFiles);
        Assert.Equal(0, result.CorruptFiles);
        Assert.Equal(2, _store.ReadBodies(sessionId).Count);

        // A re-run of the lost turn lands exactly where the lost one would have.
        var again = _store.AppendTurn(sessionId, "client-1", NewTurn("c"u8.ToArray(), "d"u8.ToArray()));
        Assert.Equal(1, again.TurnSequence);
        Assert.Equal(4, _store.ReadBodies(sessionId).Count);
    }

    /// <summary>A clean store needs no repair.</summary>
    [Fact]
    public void Recover_LeavesACleanStoreAlone()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));

        var result = _store.RecoverOnStartup();

        Assert.Equal(new SessionRecoveryResult(0, 0, 0, 0, SessionRotationRecovery.None), result);
    }

    /// <summary>Files and spools that no index row names are deleted.</summary>
    [Fact]
    public void Recover_DeletesFilesNoIndexRowNames()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        File.WriteAllText(Path.Combine(_folder, "orphan.thsess"), "x");
        File.WriteAllText(Path.Combine(_folder, "turn.spool"), "x");

        var result = _store.RecoverOnStartup();

        Assert.Equal(2, result.DeletedOrphanFiles);
        Assert.Single(Directory.GetFiles(_folder));
        Assert.Equal(2, _store.ReadBodies(sessionId).Count);
    }

    /// <summary>A file the store did not create is left alone by the orphan sweep.</summary>
    [Fact]
    public void Recover_LeavesUnrelatedFilesAlone()
    {
        var stray = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(stray, "keep me");

        var result = _store.RecoverOnStartup();

        Assert.Equal(0, result.DeletedOrphanFiles);
        Assert.True(File.Exists(stray));
    }

    /// <summary>Appending to a file that lost committed frames reports corruption instead of a parameter error.</summary>
    [Fact]
    public void AppendTurn_ToAFileShorterThanItsCommittedExtentThrowsInvalidData()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        var path = Directory.GetFiles(_folder).Single();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(stream.Length - 5);
        }

        Assert.Throws<InvalidDataException>(
            () => _store.AppendTurn(sessionId, "client-1", NewTurn("c"u8.ToArray(), "d"u8.ToArray())));
    }

    /// <summary>
    /// A rotation whose promotion fails after the index was re-wrapped is resolved in-process, so appends
    /// keep working without a restart.
    /// </summary>
    [Fact]
    public void RotateMasterKey_WhenPromotionFailsResolvesTheRotationInProcess()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        _masterKeys.FailNextPromote = true;

        Assert.Throws<IOException>(() => _store.RotateMasterKey());

        Assert.Null(_masterKeys.TryGetNext());
        _store.AppendTurn(sessionId, "client-1", NewTurn("c"u8.ToArray(), "d"u8.ToArray()));
        Assert.Equal(4, _store.ReadBodies(sessionId).Count);
    }

    /// <summary>An index row whose file vanished is removed, because the session cannot be read.</summary>
    [Fact]
    public void Recover_DropsARowWhoseFileIsGone()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        File.Delete(Directory.GetFiles(_folder).Single());

        var result = _store.RecoverOnStartup();

        Assert.Equal(1, result.DroppedMissingFiles);
        Assert.Empty(_index.ListSessions());
        Assert.Empty(_store.ListTurns(sessionId));
    }

    /// <summary>A file shorter than its committed extent is reported, not silently repaired.</summary>
    [Fact]
    public void Recover_ReportsAFileShorterThanItsCommittedExtent()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        var path = Directory.GetFiles(_folder).Single();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(stream.Length - 5);
        }

        var result = _store.RecoverOnStartup();

        Assert.Equal(1, result.CorruptFiles);
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// Deleting a session removes its file and rows, and rotates the master key so a leftover copy of the
    /// deleted session's wrapped key can no longer be unwrapped, while surviving sessions stay readable.
    /// </summary>
    [Fact]
    public void DeleteSessions_RemovesTheSessionAndRotatesTheMasterKey()
    {
        var keep = _store.ResolveArchiveSessionId("keep");
        var drop = _store.ResolveArchiveSessionId("drop");
        _store.AppendTurn(keep, "keep", NewTurn("k1"u8.ToArray(), "k2"u8.ToArray()));
        _store.AppendTurn(drop, "drop", NewTurn("d1"u8.ToArray(), "d2"u8.ToArray()));
        var oldMaster = _masterKeys.TryGetCurrent()!;
        var leftoverWrappedKey = _index.TryGetSession(drop)!.WrappedKey;

        var result = _store.DeleteSessions([drop]);

        Assert.Equal(new SessionDeletionResult(1, WalTruncated: true, MasterKeyRotated: true), result);
        Assert.Null(_index.TryGetSession(drop));
        Assert.Empty(_store.ListTurns(drop));
        Assert.Single(Directory.GetFiles(_folder));
        Assert.Equal("k1", Encoding.UTF8.GetString(_store.ReadBodies(keep)[0].Plaintext!));
        Assert.NotEqual(oldMaster, _masterKeys.TryGetCurrent());
        Assert.Null(_masterKeys.TryGetNext());
        Assert.False(_masterKeys.IsRotationRequired());

        // The old master key no longer exists anywhere the store can reach, so the leftover blob is inert.
        Assert.ThrowsAny<CryptographicException>(
            () => SessionKeyMaterial.UnwrapSessionKey(_masterKeys.TryGetCurrent()!, leftoverWrappedKey));
    }

    /// <summary>Deleting nothing does not rotate the key.</summary>
    [Fact]
    public void DeleteSessions_WithUnknownIdsChangesNothing()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        var master = _masterKeys.TryGetCurrent();

        var result = _store.DeleteSessions([Guid.NewGuid()]);

        Assert.Equal(new SessionDeletionResult(0, WalTruncated: true, MasterKeyRotated: false), result);
        Assert.Equal(master, _masterKeys.TryGetCurrent());
        Assert.False(_masterKeys.IsRotationRequired());
    }

    /// <summary>
    /// A deletion that dies before StageNext still retires the old master key on the next
    /// <see cref="SessionStore.DeleteSessions"/> call, even when the deleted ids are already gone.
    /// </summary>
    [Fact]
    public void DeleteSessions_RetriesRotationWhenRequireRotationIsStillSet()
    {
        var keep = _store.ResolveArchiveSessionId("keep");
        var drop = _store.ResolveArchiveSessionId("drop");
        _store.AppendTurn(keep, "keep", NewTurn("k1"u8.ToArray(), "k2"u8.ToArray()));
        _store.AppendTurn(drop, "drop", NewTurn("d1"u8.ToArray(), "d2"u8.ToArray()));
        var leftoverWrappedKey = _index.TryGetSession(drop)!.WrappedKey;
        var oldMaster = _masterKeys.TryGetCurrent()!;

        _masterKeys.FailNextStage = true;
        Assert.Throws<IOException>(() => _store.DeleteSessions([drop]));
        Assert.True(_masterKeys.IsRotationRequired());
        Assert.Null(_index.TryGetSession(drop));

        // Same ids again: nothing left to delete, but the marker forces the key retirement.
        var result = _store.DeleteSessions([drop]);

        Assert.Equal(new SessionDeletionResult(0, WalTruncated: true, MasterKeyRotated: true), result);
        Assert.False(_masterKeys.IsRotationRequired());
        Assert.NotEqual(oldMaster, _masterKeys.TryGetCurrent());
        Assert.Equal("k1", Encoding.UTF8.GetString(_store.ReadBodies(keep)[0].Plaintext!));
        Assert.ThrowsAny<CryptographicException>(
            () => SessionKeyMaterial.UnwrapSessionKey(_masterKeys.TryGetCurrent()!, leftoverWrappedKey));
    }

    /// <summary>
    /// Startup finishes a deletion whose rows are gone and whose rotation never staged a next key.
    /// </summary>
    [Fact]
    public void Recover_CompletesARequiredRotationLeftByAFailedDeletion()
    {
        var keep = _store.ResolveArchiveSessionId("keep");
        var drop = _store.ResolveArchiveSessionId("drop");
        _store.AppendTurn(keep, "keep", NewTurn("k1"u8.ToArray(), "k2"u8.ToArray()));
        _store.AppendTurn(drop, "drop", NewTurn("d1"u8.ToArray(), "d2"u8.ToArray()));
        var leftoverWrappedKey = _index.TryGetSession(drop)!.WrappedKey;
        var oldMaster = _masterKeys.TryGetCurrent()!;

        var dropFile = _index.TryGetSession(drop)!.FileName;
        _masterKeys.RequireRotation();
        _index.DeleteSession(drop);
        File.Delete(Path.Combine(_folder, dropFile));

        var result = _store.RecoverOnStartup();

        Assert.Equal(SessionRotationRecovery.Promoted, result.RotationOutcome);
        Assert.False(_masterKeys.IsRotationRequired());
        Assert.NotEqual(oldMaster, _masterKeys.TryGetCurrent());
        Assert.Equal("k1", Encoding.UTF8.GetString(_store.ReadBodies(keep)[0].Plaintext!));
        Assert.ThrowsAny<CryptographicException>(
            () => SessionKeyMaterial.UnwrapSessionKey(_masterKeys.TryGetCurrent()!, leftoverWrappedKey));
    }

    /// <summary>A short committed extent fails the read the same way an append would, not with a partial body list.</summary>
    [Fact]
    public void ReadBodies_RejectsAFileShorterThanItsCommittedExtent()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        var path = Directory.GetFiles(_folder).Single();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(stream.Length - 5);
        }

        Assert.Throws<InvalidDataException>(() => _store.ReadBodies(sessionId));
    }

    /// <summary>A crash after the re-wrap but before the promotion is finished by promoting the staged key.</summary>
    [Fact]
    public void Recover_PromotesAStagedKeyWhenTheIndexWasAlreadyRewrapped()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        var oldMaster = _masterKeys.TryGetCurrent()!;
        var newMaster = SessionKeyMaterial.CreateMasterKey();
        _masterKeys.StageNext(newMaster);
        _index.RewrapAllKeys(w =>
            SessionKeyMaterial.WrapSessionKey(newMaster, SessionKeyMaterial.UnwrapSessionKey(oldMaster, w)));

        var result = _store.RecoverOnStartup();

        Assert.Equal(SessionRotationRecovery.Promoted, result.RotationOutcome);
        Assert.Equal(newMaster, _masterKeys.TryGetCurrent());
        Assert.Null(_masterKeys.TryGetNext());
        Assert.Equal(2, _store.ReadBodies(sessionId).Count);
    }

    /// <summary>A crash before the re-wrap leaves the old key right, so the staged key is discarded.</summary>
    [Fact]
    public void Recover_DiscardsAStagedKeyWhenTheIndexWasNeverRewrapped()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        var oldMaster = _masterKeys.TryGetCurrent()!;
        _masterKeys.StageNext(SessionKeyMaterial.CreateMasterKey());

        var result = _store.RecoverOnStartup();

        Assert.Equal(SessionRotationRecovery.Discarded, result.RotationOutcome);
        Assert.Equal(oldMaster, _masterKeys.TryGetCurrent());
        Assert.Null(_masterKeys.TryGetNext());
        Assert.Equal(2, _store.ReadBodies(sessionId).Count);
    }

    /// <summary>A crash after promotion wrote the current key but before the staged copy was removed is harmless.</summary>
    [Fact]
    public void Recover_DiscardsAStagedKeyThatEqualsTheCurrentKey()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        _masterKeys.StageNext(_masterKeys.TryGetCurrent()!);

        var result = _store.RecoverOnStartup();

        Assert.Equal(SessionRotationRecovery.Discarded, result.RotationOutcome);
        Assert.Null(_masterKeys.TryGetNext());
    }

    /// <summary>The session folder path must not be a file.</summary>
    [Fact]
    public void Constructor_RejectsAFileAtTheFolderPath()
    {
        var path = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(path, "x");

        Assert.Throws<InvalidOperationException>(() => new SessionStore(_index, _masterKeys, path));
    }

    /// <summary>The uninstall shred removes the session files and index rows and destroys the master key.</summary>
    [Fact]
    public void Shred_RemovesSessionFilesIndexRowsAndTheMasterKey()
    {
        var sessionId = _store.ResolveArchiveSessionId("client-1");
        _store.AppendTurn(sessionId, "client-1", NewTurn("a"u8.ToArray(), "b"u8.ToArray()));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var exitCode = ShredConversationsCommand.Shred(
            databasePath: _databasePath,
            logsDirectory: Path.Combine(_root, "logs"),
            logger: NullLogger.Instance,
            probeVolume: _ => new SqliteScrub.VolumeSpace(Free: long.MaxValue, Total: 1),
            masterKeys: _masterKeys);

        Assert.Equal(ShredConversationsCommand.DoneExitCode, exitCode);
        Assert.False(Directory.Exists(_folder));
        Assert.Empty(_index.ListSessions());
        Assert.Null(_masterKeys.TryGetCurrent());
    }

    /// <summary>The secret-store-backed key store creates once, rotates in two steps, and can destroy the key.</summary>
    [Fact]
    public void SecretStoreMasterKeyStore_CreatesStagesPromotesAndDestroys()
    {
        var secrets = new ProtectedSecretStore(Path.Combine(_root, "secrets.dat"));
        var keys = new SecretStoreSessionMasterKeyStore(secrets);
        Assert.Null(keys.TryGetCurrent());

        var first = keys.GetOrCreateCurrent();
        Assert.Equal(first, keys.GetOrCreateCurrent());
        Assert.Equal(first, keys.TryGetCurrent());

        var next = SessionKeyMaterial.CreateMasterKey();
        keys.StageNext(next);
        Assert.Equal(first, keys.TryGetCurrent());
        Assert.Equal(next, keys.TryGetNext());

        keys.PromoteNext();
        Assert.Equal(next, keys.TryGetCurrent());
        Assert.Null(keys.TryGetNext());

        keys.RequireRotation();
        Assert.True(keys.IsRotationRequired());
        keys.ClearRotationRequired();
        Assert.False(keys.IsRotationRequired());

        keys.DestroyAll();
        Assert.Null(keys.TryGetCurrent());
        Assert.False(keys.IsRotationRequired());
    }

    private static SessionTurnInput NewTurn(byte[] request, byte[] response) => new(
        SessionArchiveIds.NewArchiveTurnId(),
        DateTimeOffset.UtcNow,
        [new SessionBodyInput(SessionBodyKind.ClientRequest, request),
         new SessionBodyInput(SessionBodyKind.ClientResponse, response)]);

    /// <summary>Appends a frame straight to the file, as a crash between the flush and the index commit would leave.</summary>
    private void AppendUncommittedFrame(Guid sessionId)
    {
        var row = _index.TryGetSession(sessionId)!;
        var sessionKey = SessionKeyMaterial.UnwrapSessionKey(_masterKeys.TryGetCurrent()!, row.WrappedKey);
        using var file = SessionFile.Open(Path.Combine(_folder, row.FileName), sessionKey, sessionId);
        file.AppendBody(1, SessionBodyKind.ClientRequest, "lost"u8, SessionArchiveIds.NewArchiveTurnId());
    }
}
