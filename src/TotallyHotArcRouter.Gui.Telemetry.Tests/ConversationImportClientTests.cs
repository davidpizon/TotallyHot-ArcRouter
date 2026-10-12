using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for the import half of <see cref="ConversationAdminClient"/> (#165 phase 4, PR 4b): staging, the import
/// stream and its request mapping, the imports list and delete, error translation, and the dashboard's copy of the
/// import approval binding.
/// </summary>
public class ConversationImportClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StageImportAsync_SendsTheSourceAndMapsTheCopy()
    {
        var expires = new DateTimeOffset(2026, 10, 11, 12, 30, 0, TimeSpan.Zero);
        var stub = new StubClient
        {
            Staged = new Contract.StageImportResponse
            {
                StagedId = "s1",
                Sha256 = new string('b', 64),
                SizeBytes = 12_345,
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(expires),
            },
        };

        var staged = await new ConversationAdminClient(stub).StageImportAsync(@"C:\in\a.zip", Ct);

        stub.LastStage!.SourcePath.Should().Be(@"C:\in\a.zip");
        staged.Should().Be(new StagedImportInfo("s1", new string('b', 64), 12_345, expires));
    }

    [Fact]
    public async Task ImportAsync_SendsTheStagedIdOptionsAndAuthorization()
    {
        var stub = new StubClient();
        var options = new ConversationImportOptionsInfo(
            ImportTime: true, OnConflict: ConversationImportConflictChoice.KeepBoth, KeepLocalCopy: false);

        await Drain(new ConversationAdminClient(stub).ImportAsync("s1", options, "authz", Ct));

        var request = stub.LastImport!;
        request.StagedId.Should().Be("s1");
        request.AuthorizationToken.Should().Be("authz");
        request.Options.Timestamps.Should().Be(Contract.ImportTimestampMode.ImportTime);
        request.Options.OnConflict.Should().Be(Contract.ImportConflictMode.KeepBoth);
        request.Options.ReplaceLocalOnConflict.Should().BeTrue();
    }

    [Fact]
    public async Task ImportAsync_TheDefaultOptionsKeepTheLocalCopy()
    {
        var stub = new StubClient();

        await Drain(new ConversationAdminClient(stub).ImportAsync("s1", new ConversationImportOptionsInfo(), "authz", Ct));

        var options = stub.LastImport!.Options;
        options.Timestamps.Should().Be(Contract.ImportTimestampMode.Original);
        options.OnConflict.Should().Be(Contract.ImportConflictMode.Skip);
        options.ReplaceLocalOnConflict.Should().BeFalse("the zero value of the wire message is the safe default");
    }

    [Fact]
    public void Options_RoundTripThroughTheWire()
    {
        var options = new ConversationImportOptionsInfo(true, ConversationImportConflictChoice.Overwrite, false);

        ConversationAdminClient.FromWire(ConversationAdminClient.ToWire(options)).Should().Be(options);
        ConversationAdminClient.FromWire(null).Should().Be(new ConversationImportOptionsInfo());
    }

    [Fact]
    public async Task ImportAsync_StreamsProgressThenTheResult()
    {
        var stub = new StubClient
        {
            ImportEvents =
            [
                new Contract.ImportConversationsEvent { Progress = new Contract.ImportProgress { TurnsProcessed = 3, TurnsStored = 2 } },
                new Contract.ImportConversationsEvent
                {
                    Result = new Contract.ImportResult
                    {
                        ImportId = "i1", SessionsAdded = 1, SessionsSkipped = 2, TurnsAdded = 3, TurnsAlreadyPresent = 4,
                        TurnsConflictKept = 5, TurnsRejected = 6, TurnsToGrade = 7, BodyBytes = 8, TurnsDueForRetention = 9,
                    },
                },
            ],
        };

        var events = await Drain(new ConversationAdminClient(stub).ImportAsync(
            "s1", new ConversationImportOptionsInfo(), "authz", Ct));

        events.Should().HaveCount(2);
        events[0].Progress.Should().Be(new ConversationImportProgressInfo(3, 2));
        events[1].Result.Should().Be(new ConversationImportResultInfo("i1", 1, 2, 3, 4, 5, 6, 7, 8, 9));
    }

    [Fact]
    public async Task ImportAsync_ARejectionKeepsTheRoutersOwnDetail()
    {
        var stub = new StubClient
        {
            Failure = new RpcException(new Status(StatusCode.Unauthenticated, "Passkey verification is required for this operation.")),
        };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() => Drain(new ConversationAdminClient(stub).ImportAsync(
            "s1", new ConversationImportOptionsInfo(), "authz", Ct)));

        ex.Message.Should().Be("The conversation import failed: Passkey verification is required for this operation.");
    }

    [Fact]
    public async Task ImportAsync_RejectsAMissingStagedIdOrAuthorization()
    {
        var client = new ConversationAdminClient(new StubClient());

        await Assert.ThrowsAsync<ArgumentException>(() => Drain(client.ImportAsync("", new ConversationImportOptionsInfo(), "authz", Ct)));
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(client.ImportAsync("s1", new ConversationImportOptionsInfo(), "", Ct)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Drain(client.ImportAsync("s1", null!, "authz", Ct)));
    }

    [Fact]
    public async Task ListImportsAsync_MapsTheRows()
    {
        var at = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        var stub = new StubClient
        {
            Imports = new Contract.ListConversationImportsResponse
            {
                Imports =
                {
                    new Contract.ConversationImportInfo
                    {
                        ImportId = "i1", ImportedAtUtc = Timestamp.FromDateTimeOffset(at), SourceSha256 = "abc",
                        SessionsAdded = 2, TurnsAdded = 9, TurnsSkipped = 1, TurnsRejected = 3,
                    },
                },
            },
        };

        var rows = await new ConversationAdminClient(stub).ListImportsAsync(Ct);

        rows.Should().ContainSingle().Which.Should().Be(new ConversationImportRowInfo("i1", at, "abc", 2, 9, 1, 3));
    }

    [Fact]
    public async Task DeleteImportAsync_SendsTheIdAndMapsTheCounts()
    {
        var stub = new StubClient
        {
            Deleted = new Contract.DeleteConversationImportResponse { SessionsDeleted = 2, TurnsRetired = 5 },
        };

        var removed = await new ConversationAdminClient(stub).DeleteImportAsync("i1", Ct);

        stub.LastDelete!.ImportId.Should().Be("i1");
        removed.Should().Be(new ConversationImportDeleteInfo(2, 5));
    }

    [Fact]
    public async Task StageImportAsync_AnUnreachableRouterBecomesAPlainLanguageMessage()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "down")) };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() => new ConversationAdminClient(stub).StageImportAsync(@"C:\a.zip", Ct));

        ex.IsUnavailable.Should().BeTrue();
        ex.Message.Should().Be("Could not stage the import: the router is not reachable.");
    }

    [Fact]
    public void ImportOperation_IsTheRoutersName_AndItsParametersAreTheStagedDigest()
    {
        PasskeyOperations.Import.Should().Be("import");
        PasskeyOperations.ImportParameters(new string('c', 64)).Should().Be(new string('c', 64));
        Assert.Throws<ArgumentException>(() => PasskeyOperations.ImportParameters(" "));
    }

    private static async Task<List<ConversationImportEvent>> Drain(IAsyncEnumerable<ConversationImportEvent> events)
    {
        var all = new List<ConversationImportEvent>();
        await foreach (var update in events) all.Add(update);
        return all;
    }

    private sealed class FakeStreamReader<T>(IReadOnlyList<T> messages) : IAsyncStreamReader<T>
    {
        private int _index = -1;

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            _index++;
            if (_index >= messages.Count) return Task.FromResult(false);

            Current = messages[_index];
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingStreamReader<T>(RpcException failure) : IAsyncStreamReader<T>
    {
        public T Current => throw failure;

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(failure);
    }

    private sealed class StubClient : Contract.ConversationAdminService.ConversationAdminServiceClient
    {
        public Contract.StageImportResponse Staged { get; init; } = new();

        public IReadOnlyList<Contract.ImportConversationsEvent> ImportEvents { get; init; } = [];

        public Contract.ListConversationImportsResponse Imports { get; init; } = new();

        public Contract.DeleteConversationImportResponse Deleted { get; init; } = new();

        public RpcException? Failure { get; init; }

        public Contract.StageImportRequest? LastStage { get; private set; }

        public Contract.ImportConversationsRequest? LastImport { get; private set; }

        public Contract.DeleteConversationImportRequest? LastDelete { get; private set; }

        private AsyncUnaryCall<T> Reply<T>(T value) => new(
            responseAsync: Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure),
            responseHeadersAsync: Task.FromResult(new Metadata()),
            getStatusFunc: () => Status.DefaultSuccess,
            getTrailersFunc: () => [],
            disposeAction: () => { });

        public override AsyncUnaryCall<Contract.StageImportResponse> StageImportAsync(
            Contract.StageImportRequest request, CallOptions options)
        {
            LastStage = request;
            return Reply(Staged);
        }

        public override AsyncUnaryCall<Contract.ListConversationImportsResponse> ListConversationImportsAsync(
            Contract.ListConversationImportsRequest request, CallOptions options) => Reply(Imports);

        public override AsyncUnaryCall<Contract.DeleteConversationImportResponse> DeleteConversationImportAsync(
            Contract.DeleteConversationImportRequest request, CallOptions options)
        {
            LastDelete = request;
            return Reply(Deleted);
        }

        public override AsyncServerStreamingCall<Contract.ImportConversationsEvent> ImportConversations(
            Contract.ImportConversationsRequest request, CallOptions options)
        {
            LastImport = request;
            IAsyncStreamReader<Contract.ImportConversationsEvent> reader = Failure is null
                ? new FakeStreamReader<Contract.ImportConversationsEvent>(ImportEvents)
                : new ThrowingStreamReader<Contract.ImportConversationsEvent>(Failure);

            return new AsyncServerStreamingCall<Contract.ImportConversationsEvent>(
                responseStream: reader,
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}
