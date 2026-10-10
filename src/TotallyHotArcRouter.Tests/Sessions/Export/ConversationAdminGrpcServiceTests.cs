using System.IO.Compression;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Sessions.Export;
using TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Sessions.Export;

/// <summary>
/// Pins <see cref="ConversationAdminGrpcService"/> (#165 phase 3, PR 3b): the passkey gate is consumed before any
/// file is touched and is bound to the filter and destination, the writer's refusals map to the right status
/// codes, progress streams before the result, and the summary never creates a store that was never captured into.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class ConversationAdminGrpcServiceTests : IDisposable
{
    private readonly SessionTestStore _fixture = new();
    private readonly PasskeyGateHarness _gate = PasskeyGateHarness.Create(enrolled: true);
    private readonly string _outputDirectory = Path.Combine(
        TestScratchDirectory.RunRoot, "admin-export-out-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the folder the zips are written to, outside the store's data directory.</summary>
    public ConversationAdminGrpcServiceTests()
    {
        Directory.CreateDirectory(_outputDirectory);
    }

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

    /// <summary>A call with no authorization is refused and the store is never opened and no file is written.</summary>
    [Fact]
    public async Task Export_WithoutAToken_IsUnauthenticated_AndTouchesNothing()
    {
        var opened = false;
        var service = NewService(new Lazy<SessionStore>(() =>
        {
            opened = true;
            return _fixture.Store;
        }));
        var destination = OutputPath();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportConversations(
            Request(destination, token: null), new FakeServerStreamWriter(), Context()));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.False(opened, "the gate must run before the session store is opened");
        AssertNothingWritten(destination);
    }

    /// <summary>A token that was already used cannot run a second export.</summary>
    [Fact]
    public async Task Export_ASpentToken_IsUnauthenticated_AndWritesNothing()
    {
        Seed("client-spent", 1);
        var service = NewService();
        var destination = OutputPath();
        var request = Request(destination, Approve(new ConversationExportFilter(), destination));
        await service.ExportConversations(request, new FakeServerStreamWriter(), Context());
        File.Delete(destination);

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportConversations(
            request, new FakeServerStreamWriter(), Context()));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        AssertNothingWritten(destination);
    }

    /// <summary>An approval for one filter cannot run an export with another.</summary>
    [Fact]
    public async Task Export_ATokenForADifferentFilter_IsUnauthenticated_AndWritesNothing()
    {
        Seed("client-filter", 1);
        var service = NewService();
        var destination = OutputPath();
        var token = Approve(new ConversationExportFilter(Harness: "codex"), destination);
        var request = Request(destination, token);
        request.Harness = "claude-code";

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportConversations(
            request, new FakeServerStreamWriter(), Context()));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        AssertNothingWritten(destination);
    }

    /// <summary>An approval for one destination cannot write to another.</summary>
    [Fact]
    public async Task Export_ATokenForADifferentDestination_IsUnauthenticated_AndWritesNothing()
    {
        Seed("client-path", 1);
        var service = NewService();
        var approved = OutputPath();
        var requested = OutputPath();
        var request = Request(requested, Approve(new ConversationExportFilter(), approved));

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportConversations(
            request, new FakeServerStreamWriter(), Context()));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        AssertNothingWritten(approved);
        AssertNothingWritten(requested);
    }

    /// <summary>With no passkey enrolled the call is a failed precondition carrying the enrollment message.</summary>
    [Fact]
    public async Task Export_WithNoPasskeyEnrolled_IsAFailedPrecondition()
    {
        var service = NewService(gate: PasskeyGateHarness.Create(enrolled: false).Gate);
        var destination = OutputPath();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportConversations(
            Request(destination, "anything"), new FakeServerStreamWriter(), Context()));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal(ContentGate.EnrollmentRequiredDetail, ex.Status.Detail);
        AssertNothingWritten(destination);
    }

    /// <summary>An approved export streams progress and ends with the result, and the zip is where the request said.</summary>
    [Fact]
    public async Task Export_WithAMatchingToken_StreamsProgressThenTheResult()
    {
        Seed("client-ok", 3);
        var service = NewService();
        var destination = OutputPath();
        var writer = new FakeServerStreamWriter();

        await service.ExportConversations(
            Request(destination, Approve(new ConversationExportFilter(), destination)), writer, Context());

        Assert.NotEmpty(writer.Written);
        var result = writer.Written[^1].Result;
        Assert.NotNull(result);
        Assert.Equal(destination, result.Path);
        Assert.Equal(1, result.Conversations);
        Assert.Equal(3, result.Turns);
        Assert.All(writer.Written[..^1], message => Assert.NotNull(message.Progress));
        Assert.Contains(writer.Written, message => message.Progress is { TurnsWritten: 3 });
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(destination + ".partial"));
        using var zip = ZipFile.OpenRead(destination);
        Assert.NotNull(zip.GetEntry("manifest.json"));
    }

    /// <summary>The filter on the wire reaches the writer, so only matching turns are exported.</summary>
    [Fact]
    public async Task Export_AppliesTheRequestsFilter()
    {
        Seed("client-a", 2, harness: "claude-code");
        Seed("client-b", 1, harness: "codex");
        var service = NewService();
        var destination = OutputPath();
        var filter = new ConversationExportFilter(Harness: "codex");
        var request = Request(destination, Approve(filter, destination));
        request.Harness = "codex";
        var writer = new FakeServerStreamWriter();

        await service.ExportConversations(request, writer, Context());

        Assert.Equal(1, writer.Written[^1].Result.Turns);
    }

    /// <summary>The path rules refuse a bad destination as an invalid argument before any file is created.</summary>
    [Fact]
    public async Task Export_APathThatBreaksTheRules_IsInvalidArgument_BeforeAnyFileIsWritten()
    {
        Seed("client-rules", 1);
        var service = NewService();
        var existing = OutputPath();
        await File.WriteAllTextAsync(existing, "keep me", Ct);
        var underData = Path.Combine(_fixture.Root, "export.zip");
        var inSessionFolder = Path.Combine(_fixture.Folder, "export.zip");

        foreach (var destination in new[] { "relative.zip", existing, underData, inSessionFolder })
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportConversations(
                Request(destination, Approve(new ConversationExportFilter(), destination)),
                new FakeServerStreamWriter(), Context()));

            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
            Assert.False(File.Exists(destination + ".partial"), destination);
        }

        Assert.Equal("keep me", await File.ReadAllTextAsync(existing, Ct));
        Assert.False(File.Exists(underData));
        Assert.False(File.Exists(inSessionFolder));
    }

    /// <summary>A full volume is a failed precondition, and nothing is left behind.</summary>
    [Fact]
    public async Task Export_WhenTheVolumeIsTooFull_IsAFailedPrecondition()
    {
        Seed("client-full", 1);
        var service = NewService(writer: new ConversationExportWriter(volumeSpace: _ => (FreeBytes: 0, TotalBytes: 1L << 40)));
        var destination = OutputPath();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportConversations(
            Request(destination, Approve(new ConversationExportFilter(), destination)),
            new FakeServerStreamWriter(), Context()));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        AssertNothingWritten(destination);
    }

    /// <summary>A timestamp outside the representable range is an invalid argument and consumes no approval.</summary>
    [Fact]
    public async Task Export_AnOutOfRangeTimestamp_IsInvalidArgument()
    {
        var service = NewService();
        var destination = OutputPath();
        var request = Request(destination, token: "anything");
        request.FromUtc = new Timestamp { Seconds = long.MaxValue };

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportConversations(
            request, new FakeServerStreamWriter(), Context()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        AssertNothingWritten(destination);
    }

    /// <summary>Cancelling the call leaves no zip and no partial file.</summary>
    [Fact]
    public async Task Export_WhenTheCallIsCancelled_LeavesNoFile()
    {
        Seed("client-cancel", 2);
        var service = NewService();
        var destination = OutputPath();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportConversations(
            Request(destination, Approve(new ConversationExportFilter(), destination)),
            new FakeServerStreamWriter(), Context(cancellation.Token)));

        AssertNothingWritten(destination);
    }

    /// <summary>The summary reports sessions, turns and committed bytes.</summary>
    [Fact]
    public async Task GetSummary_ReportsWhatTheStoreHolds()
    {
        Seed("client-one", 2);
        Seed("client-two", 3);
        var service = NewService();

        var summary = await service.GetConversationExportSummary(new Contract.GetConversationExportSummaryRequest(), Context());

        Assert.Equal(2, summary.SessionCount);
        Assert.Equal(5, summary.TurnCount);
        Assert.Equal(_fixture.Store.ListSessions().Sum(s => s.CommittedLength), summary.BytesOnDisk);
        Assert.True(summary.BytesOnDisk > 0);
    }

    /// <summary>A router that never captured reports zeros without creating the store.</summary>
    [Fact]
    public async Task GetSummary_WhenNothingWasEverCaptured_IsZero_AndNeverOpensTheStore()
    {
        var emptyRoot = Path.Combine(_outputDirectory, "never-captured");
        Directory.CreateDirectory(emptyRoot);
        var opened = false;
        var service = new ConversationAdminGrpcService(
            new Lazy<SessionStore>(() =>
            {
                opened = true;
                return _fixture.Store;
            }),
            new ConversationExportWriter(),
            _gate.Gate,
            Database(emptyRoot));

        var summary = await service.GetConversationExportSummary(new Contract.GetConversationExportSummaryRequest(), Context());

        Assert.Equal(0, summary.SessionCount);
        Assert.Equal(0, summary.TurnCount);
        Assert.Equal(0, summary.BytesOnDisk);
        Assert.False(opened);
        Assert.False(Directory.Exists(Path.Combine(emptyRoot, "sessions")));
    }

    /// <summary>The constructor refuses missing collaborators.</summary>
    [Fact]
    public void Constructor_RejectsNullCollaborators()
    {
        var store = new Lazy<SessionStore>(() => _fixture.Store);
        var database = Database(_fixture.Root);

        Assert.Throws<ArgumentNullException>(() => new ConversationAdminGrpcService(null!, new ConversationExportWriter(), _gate.Gate, database));
        Assert.Throws<ArgumentNullException>(() => new ConversationAdminGrpcService(store, null!, _gate.Gate, database));
        Assert.Throws<ArgumentNullException>(() => new ConversationAdminGrpcService(store, new ConversationExportWriter(), null!, database));
        Assert.Throws<ArgumentNullException>(() => new ConversationAdminGrpcService(store, new ConversationExportWriter(), _gate.Gate, null!));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TranscriptDatabase Database(string root) => new(Options.Create(
        new StorageOptions { TranscriptDatabasePath = Path.Combine(root, "transcripts.db") }));

    private static ServerCallContext Context(CancellationToken? cancellationToken = null) =>
        PasskeyGateHarness.Context(cancellationToken: cancellationToken ?? Ct);

    private ConversationAdminGrpcService NewService(
        Lazy<SessionStore>? store = null, ContentGate? gate = null, ConversationExportWriter? writer = null) =>
        new(
            store ?? new Lazy<SessionStore>(() => _fixture.Store),
            writer ?? new ConversationExportWriter(volumeSpace: _ => (FreeBytes: long.MaxValue / 2, TotalBytes: long.MaxValue / 2)),
            gate ?? _gate.Gate,
            Database(_fixture.Root));

    private string OutputPath() => Path.Combine(_outputDirectory, Guid.NewGuid().ToString("N") + ".zip");

    private string Approve(ConversationExportFilter filter, string destination) =>
        _gate.OneOperations.Issue(GatedOperation.Export, GatedOperation.ExportParameters(filter, destination));

    private static Contract.ExportConversationsRequest Request(string destination, string? token)
    {
        var request = new Contract.ExportConversationsRequest { DestinationPath = destination };
        if (token is not null) request.AuthorizationToken = token;
        return request;
    }

    private static void AssertNothingWritten(string destination)
    {
        Assert.False(File.Exists(destination), "no zip may exist");
        Assert.False(File.Exists(destination + ".partial"), "no partial zip may exist");
    }

    private void Seed(string client, int turns, string harness = "claude-code")
    {
        var sessionId = _fixture.Store.ResolveArchiveSessionId(client);
        var epoch = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < turns; i++)
        {
            var metadata = $$"""{"session_id":"{{client}}","turn_number":{{i + 1}},"harness":"{{harness}}","provider":"anthropic"}""";
            _fixture.Store.AppendTurn(sessionId, client, new SessionTurnInput(
                SessionArchiveIds.NewArchiveTurnId(),
                epoch.AddMinutes(i),
                [
                    new SessionBodyInput(SessionBodyKind.ClientRequest, Encoding.UTF8.GetBytes($"{{\"turn\":{i}}}")),
                    new SessionBodyInput(SessionBodyKind.ClientResponse, Encoding.UTF8.GetBytes($"{{\"reply\":{i}}}")),
                    new SessionBodyInput(SessionBodyKind.TurnMetadata, Encoding.UTF8.GetBytes(metadata)),
                    new SessionBodyInput(SessionBodyKind.Extracts, """{"newest_user_message":"hi"}"""u8.ToArray()),
                ]));
        }
    }

    private sealed class FakeServerStreamWriter : IServerStreamWriter<Contract.ExportConversationsEvent>
    {
        public List<Contract.ExportConversationsEvent> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Contract.ExportConversationsEvent message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }
}
