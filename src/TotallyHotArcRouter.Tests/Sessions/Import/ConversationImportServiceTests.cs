using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Sessions.Export;
using TotallyHot.ArcRouter.Sessions.Import;
using TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Sessions.Import;

/// <summary>
/// Pins the transport half of the conversation import (#165 phase 4, PR 4b): the staging area that makes the
/// approved bytes the imported bytes, the gated import and staging RPCs, the imports list and delete, and the
/// import case of the browser approval hand-off.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class ConversationImportServiceTests : IDisposable
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SessionTestStore _source = new();
    private readonly SessionTestStore _target = new();
    private readonly PasskeyGateHarness _gate = PasskeyGateHarness.Create(enrolled: true);
    private readonly string _work = Path.Combine(TestScratchDirectory.RunRoot, "import-svc-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the folder the zips live in, outside both stores' data directories.</summary>
    public ConversationImportServiceTests()
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

    /// <summary>Staging copies the zip, hashes the copy, and the copy no longer follows the original.</summary>
    [Fact]
    public void Stage_CopiesAndHashesTheFile_AndTheCopyIsIndependentOfTheOriginal()
    {
        var zip = WriteZip("a.zip");
        var original = File.ReadAllBytes(zip);
        var area = NewStaging();

        var staged = area.Stage(_target.Store, zip, Ct);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), staged.Sha256Hex);
        Assert.Equal(original.Length, staged.SizeBytes);
        Assert.Equal(_target.Folder, Path.GetDirectoryName(staged.StagedPath));
        File.WriteAllBytes(zip, [1, 2, 3]);
        Assert.Equal(original, File.ReadAllBytes(staged.StagedPath));
        Assert.Same(staged, area.TryGet(staged.Id));
    }

    /// <summary>A source that breaks a rule is refused before anything is copied.</summary>
    [Fact]
    public void Stage_ASourceThatBreaksTheRules_IsRefused()
    {
        var area = NewStaging();
        var notZip = Path.Combine(_work, "notzip.zip");
        File.WriteAllText(notZip, "hello, this is not a zip");
        var inside = Path.Combine(_target.Root, "inside.zip");
        File.WriteAllBytes(inside, File.ReadAllBytes(WriteZip("copy.zip")));
        var directory = Path.Combine(_work, "folder.zip");
        Directory.CreateDirectory(directory);

        foreach (var source in new[] { "relative.zip", Path.Combine(_work, "missing.zip"), directory, notZip, inside })
        {
            var ex = Assert.Throws<ConversationImportException>(() => area.Stage(_target.Store, source, Ct));
            Assert.Equal(ConversationImportFailure.InvalidSource, ex.Reason);
        }

        Assert.Empty(Directory.GetFiles(_target.Folder, "*.tmp"));
    }

    /// <summary>Only a few copies may be staged at once, and discarding one makes room.</summary>
    [Fact]
    public void Stage_IsBounded_AndDiscardMakesRoom()
    {
        var area = NewStaging();
        var zip = WriteZip("many.zip");
        var staged = Enumerable.Range(0, ImportStagingArea.MaxStaged).Select(_ => area.Stage(_target.Store, zip, Ct)).ToList();

        var ex = Assert.Throws<ConversationImportException>(() => area.Stage(_target.Store, zip, Ct));
        Assert.Equal(ConversationImportFailure.TooManyStaged, ex.Reason);

        area.Discard(staged[0].Id);
        Assert.False(File.Exists(staged[0].StagedPath));
        Assert.NotNull(area.Stage(_target.Store, zip, Ct));
    }

    /// <summary>A copy nobody imports is discarded when it lapses.</summary>
    [Fact]
    public void Stage_ACopyThatLapses_IsDiscarded()
    {
        var clock = new MovableTime(Epoch);
        var area = new ImportStagingArea(_ => (long.MaxValue / 2, long.MaxValue / 2), clock);
        var staged = area.Stage(_target.Store, WriteZip("lapse.zip"), Ct);
        clock.Now = Epoch + ImportStagingArea.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(area.TryGet(staged.Id));
        Assert.False(File.Exists(staged.StagedPath));
    }

    /// <summary>Staging refuses a volume that cannot hold the copy and the reserve, and leaves nothing behind.</summary>
    [Fact]
    public void Stage_WithTooLittleFreeSpace_IsRefused()
    {
        var area = new ImportStagingArea(_ => (FreeBytes: 10, TotalBytes: 1L << 40));

        var ex = Assert.Throws<ConversationImportException>(() => area.Stage(_target.Store, WriteZip("full.zip"), Ct));

        Assert.Equal(ConversationImportFailure.InsufficientDiskSpace, ex.Reason);
        Assert.Empty(Directory.GetFiles(_target.Folder, "*.tmp"));
    }

    /// <summary>An import call with no authorization is refused and the staged copy is kept so the operator can approve it.</summary>
    [Fact]
    public async Task Import_WithoutAToken_IsUnauthenticated_AndKeepsTheStagedCopy()
    {
        var (service, staging, staged) = await ServiceWithStagedExportAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ImportConversations(
            ImportRequest(staged.Id, token: null), new FakeWriter(), Context()));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.NotNull(staging.TryGet(staged.Id));
        Assert.Equal(0, _target.Scalar("SELECT COUNT(*) FROM session_files"));
    }

    /// <summary>An approval bound to some other zip's digest cannot import this one.</summary>
    [Fact]
    public async Task Import_ATokenForADifferentDigest_IsUnauthenticated()
    {
        var (service, _, staged) = await ServiceWithStagedExportAsync();
        var token = _gate.OneOperations.Issue(GatedOperation.Import, GatedOperation.ImportParameters(new string('a', 64)));

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ImportConversations(
            ImportRequest(staged.Id, token), new FakeWriter(), Context()));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Equal(0, _target.Scalar("SELECT COUNT(*) FROM session_files"));
    }

    /// <summary>An unknown staged id is not found.</summary>
    [Fact]
    public async Task Import_AnUnknownStagedId_IsNotFound()
    {
        var (service, _, _) = await ServiceWithStagedExportAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ImportConversations(
            ImportRequest("0123456789abcdef", "anything"), new FakeWriter(), Context()));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    /// <summary>An approved import streams progress then the result, discards the staged copy, and is listed.</summary>
    [Fact]
    public async Task Import_WithAMatchingToken_StreamsTheResult_DiscardsTheCopy_AndIsListed()
    {
        var (service, staging, staged) = await ServiceWithStagedExportAsync();
        var token = _gate.OneOperations.Issue(GatedOperation.Import, GatedOperation.ImportParameters(staged.Sha256Hex));
        var writer = new FakeWriter();

        await service.ImportConversations(ImportRequest(staged.Id, token), writer, Context());

        var result = writer.Written[^1].Result;
        Assert.NotNull(result);
        Assert.Equal(2, result.SessionsAdded);
        Assert.Equal(3, result.TurnsAdded);
        Assert.All(writer.Written[..^1], message => Assert.NotNull(message.Progress));
        Assert.Null(staging.TryGet(staged.Id));
        Assert.False(File.Exists(staged.StagedPath));

        var listed = (await service.ListConversationImports(new Contract.ListConversationImportsRequest(), Context())).Imports;
        var import = Assert.Single(listed);
        Assert.Equal(result.ImportId, import.ImportId);
        Assert.Equal(3, import.TurnsAdded);
        Assert.Equal(staged.Sha256Hex, import.SourceSha256);
    }

    /// <summary>The staging RPC refuses a bad source with an invalid-argument status.</summary>
    [Fact]
    public async Task StageImport_ABadSource_IsInvalidArgument()
    {
        var (service, _, _) = await ServiceWithStagedExportAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.StageImport(
            new Contract.StageImportRequest { SourcePath = "relative.zip" }, Context()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    /// <summary>A router built without import refuses its RPCs as unimplemented.</summary>
    [Fact]
    public async Task ImportRpcs_WithoutImportWired_AreUnimplemented()
    {
        var service = new ConversationAdminGrpcService(
            new Lazy<SessionStore>(() => _target.Store), new ConversationExportWriter(), _gate.Gate, Database());

        var stage = await Assert.ThrowsAsync<RpcException>(() => service.StageImport(
            new Contract.StageImportRequest { SourcePath = WriteZip("x.zip") }, Context()));
        var list = await Assert.ThrowsAsync<RpcException>(() => service.ListConversationImports(
            new Contract.ListConversationImportsRequest(), Context()));

        Assert.Equal(StatusCode.Unimplemented, stage.StatusCode);
        Assert.Equal(StatusCode.Unimplemented, list.StatusCode);
    }

    /// <summary>
    /// Deleting an import removes the sessions it created and retires only the turns it added to a session that
    /// already existed, leaving that session's other turns alone.
    /// </summary>
    [Fact]
    public async Task DeleteImport_RemovesWhatItAdded_AndNothingElse()
    {
        var shared = SessionArchiveIds.NewArchiveSessionId();
        var first = SessionArchiveIds.NewArchiveTurnId();
        var second = SessionArchiveIds.NewArchiveTurnId();
        var other = SessionArchiveIds.NewArchiveSessionId();
        foreach (var store in new[] { _source.Store, _target.Store })
        {
            store.AppendTurn(shared, "client-shared", Turn(first, Epoch));
        }

        _source.Store.AppendTurn(shared, "client-shared", Turn(second, Epoch.AddMinutes(1)));
        _source.Store.AppendTurn(other, "client-other", Turn(SessionArchiveIds.NewArchiveTurnId(), Epoch.AddMinutes(2)));
        var (service, _, staged) = await ServiceWithStagedExportAsync(seed: false);
        var token = _gate.OneOperations.Issue(GatedOperation.Import, GatedOperation.ImportParameters(staged.Sha256Hex));
        var writer = new FakeWriter();
        await service.ImportConversations(ImportRequest(staged.Id, token), writer, Context());
        Assert.Equal(2, _target.Store.ListTurns(shared).Count);

        var deleted = await service.DeleteConversationImport(
            new Contract.DeleteConversationImportRequest { ImportId = writer.Written[^1].Result.ImportId }, Context());

        Assert.Equal(1, deleted.SessionsDeleted);
        Assert.Equal(1, deleted.TurnsRetired);
        Assert.Null(_target.Store.TryGetSession(other));
        Assert.Equal([first], _target.Store.ListTurns(shared).Select(t => t.ArchiveTurnId));
        Assert.Empty((await service.ListConversationImports(new Contract.ListConversationImportsRequest(), Context())).Imports);
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.DeleteConversationImport(
            new Contract.DeleteConversationImportRequest { ImportId = writer.Written[^1].Result.ImportId }, Context()));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    /// <summary>Deleting an import is refused while an import is running, so it cannot orphan turns the import is still adding.</summary>
    [Fact]
    public async Task DeleteImport_WhileAnImportRuns_IsAFailedPrecondition()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var importer = new ConversationImporter(
            new HoldingSink(gate.Task, entered), volumeSpace: _ => (long.MaxValue / 2, long.MaxValue / 2));
        var database = Database();
        var staging = NewStaging();
        var service = new ConversationAdminGrpcService(
            new Lazy<SessionStore>(() => _target.Store),
            new ConversationExportWriter(),
            _gate.Gate,
            database,
            importer: importer,
            staging: staging,
            imports: new ConversationImportCatalog(
                new Lazy<SessionStore>(() => _target.Store),
                new SessionMaintenance(new Lazy<SessionStore>(() => _target.Store), database),
                database,
                importer));
        _source.Store.AppendTurn(SessionArchiveIds.NewArchiveSessionId(), "client-a", Turn(SessionArchiveIds.NewArchiveTurnId(), Epoch));
        var zip = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".zip");
        await new ConversationExportWriter().WriteAsync(_source.Store, new ConversationExportFilter(), zip, null, Ct);
        var staged = staging.Stage(_target.Store, zip, Ct);
        var token = _gate.OneOperations.Issue(GatedOperation.Import, GatedOperation.ImportParameters(staged.Sha256Hex));
        var running = service.ImportConversations(ImportRequest(staged.Id, token), new FakeWriter(), Context());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(4), Ct);

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.DeleteConversationImport(
            new Contract.DeleteConversationImportRequest { ImportId = Guid.NewGuid().ToString("D") }, Context()));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        gate.SetResult();
        await running;
    }

    /// <summary>A source that is itself a link into the data directory is refused, as a link in its folder chain is.</summary>
    [Fact]
    public void Stage_ASourceThatIsALinkIntoTheDataDirectory_IsRefused()
    {
        var inside = Path.Combine(_target.Root, "inside.zip");
        File.Copy(WriteZip("target.zip"), inside);
        var link = Path.Combine(_work, "link.zip");
        try
        {
            File.CreateSymbolicLink(link, inside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Creating a symbolic link needs a privilege this account may not hold; there is nothing to test then.
            return;
        }

        var refused = Assert.Throws<ConversationImportException>(() => NewStaging().Stage(_target.Store, link, Ct));

        Assert.Equal(ConversationImportFailure.InvalidSource, refused.Reason);
    }

    /// <summary>The wire mapping of the import options round-trips and defaults to keeping the local copy.</summary>
    [Fact]
    public void ImportOptions_RoundTripThroughTheWire_AndDefaultToKeepingTheLocalCopy()
    {
        var options = new ConversationImportOptions(ImportTimestampMode.ImportTime, ImportConflictMode.KeepBoth, KeepLocalCopy: false);

        Assert.Equal(options, ConversationImportWire.ToOptions(ConversationImportWire.ToWire(options)));
        Assert.Equal(new ConversationImportOptions(), ConversationImportWire.ToOptions(null));
        Assert.Equal(new ConversationImportOptions(), ConversationImportWire.ToOptions(new Contract.ImportOptions()));
    }

    /// <summary>The browser hand-off carries an import: the request shows the staged digest and the approval collects an authorization for exactly it.</summary>
    [Fact]
    public async Task ApprovalHandOff_ForAnImport_IsBoundToTheStagedDigest()
    {
        var staging = NewStaging();
        var staged = staging.Stage(_target.Store, WriteZip("handoff.zip"), Ct);
        var pending = new PendingApprovalTable(new Uri("https://localhost:47104"));
        var service = new PasskeyAdminGrpcService(
            _gate.Gate,
            new EnrollmentCodeService(new ProtectedSecretStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat"))),
            new FakeWebAuthnCeremonyService(new ChallengeStore(_gate.Options), _gate.Credentials),
            _gate.Grants,
            _gate.OneOperations,
            new PasskeyApprovalLog(),
            _gate.Credentials,
            _gate.Options,
            pending,
            importStaging: staging);
        var context = PasskeyGateHarness.Context(null, Ct);

        var created = await service.CreateApprovalRequest(
            new Contract.CreateApprovalRequestRequest
            {
                Import = new Contract.ImportApprovalDetails
                {
                    StagedId = staged.Id,
                    Options = new Contract.ImportOptions { Timestamps = Contract.ImportTimestampMode.ImportTime },
                },
            },
            context);

        var listed = Assert.Single((await service.ListPendingApprovals(new Contract.ListPendingApprovalsRequest(), context)).Approvals);
        Assert.Equal(Contract.PendingApproval.OperationOneofCase.Import, listed.OperationCase);
        Assert.Equal(staged.Sha256Hex, listed.Import.Sha256);
        Assert.Equal(staged.SizeBytes, listed.Import.SizeBytes);
        Assert.Equal(staged.SourcePath, listed.Import.SourcePath);
        Assert.Equal(Contract.ImportTimestampMode.ImportTime, listed.Import.Options.Timestamps);

        var options = await service.BeginApproval(new Contract.BeginApprovalRequest { ApprovalId = created.ApprovalId }, context);
        using (var doc = JsonDocument.Parse(options.OptionsJson))
        {
            await service.FinishApproval(
                new Contract.FinishApprovalRequest
                {
                    ApprovalId = created.ApprovalId,
                    AssertionJson = JsonSerializer.Serialize(new { challenge = doc.RootElement.GetProperty("challenge").GetString() }),
                },
                context);
        }

        var collected = await service.WaitForApproval(new Contract.WaitForApprovalRequest { ApprovalId = created.ApprovalId }, context);
        Assert.Equal(Contract.ApprovalOutcome.Approved, collected.Outcome);
        ContentGateHooks.RequireImport(_gate.Gate, collected.AuthorizationToken, staged.Sha256Hex);
    }

    /// <summary>An approval request cannot name a staged copy the router does not hold, so a client cannot supply its own digest.</summary>
    [Fact]
    public async Task ApprovalHandOff_ForAnUnknownStagedCopy_IsNotFound()
    {
        var service = new PasskeyAdminGrpcService(
            _gate.Gate,
            new EnrollmentCodeService(new ProtectedSecretStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat"))),
            new FakeWebAuthnCeremonyService(new ChallengeStore(_gate.Options), _gate.Credentials),
            _gate.Grants,
            _gate.OneOperations,
            new PasskeyApprovalLog(),
            _gate.Credentials,
            _gate.Options,
            new PendingApprovalTable(new Uri("https://localhost:47104")),
            importStaging: NewStaging());

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.CreateApprovalRequest(
            new Contract.CreateApprovalRequestRequest { Import = new Contract.ImportApprovalDetails { StagedId = "feedface" } },
            PasskeyGateHarness.Context(null, Ct)));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServerCallContext Context() => PasskeyGateHarness.Context(cancellationToken: Ct);

    private TranscriptDatabase Database() => new(Options.Create(
        new StorageOptions { TranscriptDatabasePath = Path.Combine(_target.Root, "transcripts.db") }));

    private static ImportStagingArea NewStaging() => new(_ => (long.MaxValue / 2, long.MaxValue / 2));

    private static Contract.ImportConversationsRequest ImportRequest(string stagedId, string? token)
    {
        var request = new Contract.ImportConversationsRequest { StagedId = stagedId };
        if (token is not null) request.AuthorizationToken = token;
        return request;
    }

    /// <summary>Builds the service over the target store, with a zip exported from the source staged and ready.</summary>
    private async Task<(ConversationAdminGrpcService Service, ImportStagingArea Staging, StagedImport Staged)>
        ServiceWithStagedExportAsync(bool seed = true)
    {
        if (seed)
        {
            foreach (var (session, client) in new[]
                     {
                         (SessionArchiveIds.NewArchiveSessionId(), "client-a"),
                         (SessionArchiveIds.NewArchiveSessionId(), "client-b"),
                     })
            {
                var turns = client == "client-a" ? 2 : 1;
                for (var i = 0; i < turns; i++)
                {
                    _source.Store.AppendTurn(session, client, Turn(SessionArchiveIds.NewArchiveTurnId(), Epoch.AddMinutes(i)));
                }
            }
        }

        var zip = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".zip");
        await new ConversationExportWriter().WriteAsync(_source.Store, new ConversationExportFilter(), zip, null, Ct);

        var staging = NewStaging();
        var database = Database();
        var maintenance = new SessionMaintenance(new Lazy<SessionStore>(() => _target.Store), database);
        var service = new ConversationAdminGrpcService(
            new Lazy<SessionStore>(() => _target.Store),
            new ConversationExportWriter(),
            _gate.Gate,
            database,
            importer: new ConversationImporter(volumeSpace: _ => (long.MaxValue / 2, long.MaxValue / 2)),
            staging: staging,
            imports: new ConversationImportCatalog(new Lazy<SessionStore>(() => _target.Store), maintenance, database));
        var staged = (await service.StageImport(new Contract.StageImportRequest { SourcePath = zip }, Context()));
        return (service, staging, staging.TryGet(staged.StagedId)!);
    }

    private static SessionTurnInput Turn(Guid turnId, DateTimeOffset at) => new(
        turnId,
        at,
        [
            new SessionBodyInput(SessionBodyKind.ClientRequest, """{"messages":[{"role":"user","content":"hi"}]}"""u8.ToArray()),
            new SessionBodyInput(SessionBodyKind.ClientResponse, """{"choices":[{"message":{"content":"yo"}}]}"""u8.ToArray()),
            new SessionBodyInput(SessionBodyKind.TurnMetadata, """{"provider":"openai","routed_model":"gpt-x"}"""u8.ToArray()),
            new SessionBodyInput(SessionBodyKind.Extracts, """{"newest_user_message":"hi"}"""u8.ToArray()),
        ]);

    /// <summary>Writes a small zip, which is all staging needs: it checks the signature and does not parse the archive.</summary>
    private string WriteZip(string name)
    {
        var path = Path.Combine(_work, name);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("hello.txt").Open();
        entry.Write("hello"u8);
        return path;
    }

    private sealed class FakeWriter : IServerStreamWriter<Contract.ImportConversationsEvent>
    {
        public List<Contract.ImportConversationsEvent> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Contract.ImportConversationsEvent message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class HoldingSink(Task gate, TaskCompletionSource entered) : IImportedTurnSink
    {
        public Task TurnStoredAsync(ImportedTurn turn, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            return gate;
        }
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
