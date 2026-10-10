using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="ConversationAdminClient"/> - the request mapping, the progress/result stream, and error
/// translation behind the Sessions tab's Export dialog (#165 phase 3), mirroring
/// <see cref="ClusterModelAdminClientTests"/>. Driven through a subclassed generated stub rather than a live
/// server.
/// </summary>
public class ConversationAdminClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetSummaryAsync_MapsTheCounts()
    {
        var stub = new StubClient
        {
            Summary = new Contract.ConversationExportSummaryResponse
            {
                SessionCount = 4,
                TurnCount = 1_234_567_890_123,
                BytesOnDisk = 9_876_543_210,
            },
        };

        var summary = await new ConversationAdminClient(stub).GetSummaryAsync(Ct);

        summary.Should().Be(new ConversationExportSummaryInfo(4, 1_234_567_890_123, 9_876_543_210));
    }

    [Fact]
    public async Task ExportAsync_SendsTheFilterDestinationAndAuthorization()
    {
        var from = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 9, 17, 45, 30, 500, TimeSpan.Zero);
        var stub = new StubClient();
        var filter = new ConversationExportFilterInfo(from, to, "session-1", "claude-code", "anthropic", "claude-x");

        await Drain(new ConversationAdminClient(stub).ExportAsync(filter, @"C:\exports\a.zip", "authz-token", Ct));

        var request = stub.LastExportRequest!;
        request.DestinationPath.Should().Be(@"C:\exports\a.zip");
        request.AuthorizationToken.Should().Be("authz-token");
        request.FromUtc.ToDateTimeOffset().Should().Be(from);
        request.ToUtc.ToDateTimeOffset().Should().Be(to);
        request.SessionId.Should().Be("session-1");
        request.Harness.Should().Be("claude-code");
        request.Provider.Should().Be("anthropic");
        request.Model.Should().Be("claude-x");
    }

    [Fact]
    public async Task ExportAsync_AnEmptyFilterSendsNoFilterFields()
    {
        var stub = new StubClient();

        await Drain(new ConversationAdminClient(stub).ExportAsync(
            new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz-token", Ct));

        var request = stub.LastExportRequest!;
        request.FromUtc.Should().BeNull();
        request.ToUtc.Should().BeNull();
        request.HasSessionId.Should().BeFalse();
        request.HasHarness.Should().BeFalse();
        request.HasProvider.Should().BeFalse();
        request.HasModel.Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_StreamsProgressThenTheResult()
    {
        var stub = new StubClient
        {
            Events =
            [
                new Contract.ExportConversationsEvent { Progress = new Contract.ExportProgress { TurnsWritten = 1, BytesWritten = 10 } },
                new Contract.ExportConversationsEvent { Progress = new Contract.ExportProgress { TurnsWritten = 2, BytesWritten = 25 } },
                new Contract.ExportConversationsEvent
                {
                    Result = new Contract.ExportResult
                    {
                        Path = @"C:\exports\a.zip",
                        Conversations = 1,
                        Turns = 2,
                        MissingBodies = 3,
                        CorruptTurns = 4,
                        IncompleteSessions = 5,
                        BodyBytes = 25,
                    },
                },
            ],
        };

        var events = await Drain(new ConversationAdminClient(stub).ExportAsync(
            new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz-token", Ct));

        events.Should().HaveCount(3);
        events[0].Progress.Should().Be(new ConversationExportProgressInfo(1, 10));
        events[0].Result.Should().BeNull();
        events[1].Progress.Should().Be(new ConversationExportProgressInfo(2, 25));
        events[2].Progress.Should().BeNull();
        events[2].Result.Should().Be(new ConversationExportResultInfo(@"C:\exports\a.zip", 1, 2, 3, 4, 5, 25));
    }

    [Fact]
    public async Task ExportAsync_AnUnknownMessageMapsToAnEmptyEvent()
    {
        var stub = new StubClient { Events = [new Contract.ExportConversationsEvent()] };

        var events = await Drain(new ConversationAdminClient(stub).ExportAsync(
            new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz-token", Ct));

        var single = events.Should().ContainSingle().Subject;
        single.Progress.Should().BeNull();
        single.Result.Should().BeNull();
    }

    [Fact]
    public async Task ExportAsync_ARejectionKeepsTheRoutersOwnDetail()
    {
        var stub = new StubClient
        {
            Failure = new RpcException(new Status(StatusCode.Unauthenticated, "Passkey verification is required for this operation.")),
        };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() => Drain(new ConversationAdminClient(stub).ExportAsync(
            new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz-token", Ct)));

        ex.Message.Should().Be("The conversation export failed: Passkey verification is required for this operation.");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_AnUnreachableRouterBecomesAPlainLanguageMessage()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() => Drain(new ConversationAdminClient(stub).ExportAsync(
            new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz-token", Ct)));

        ex.Message.Should().Be("The conversation export failed: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetSummaryAsync_AFailureIsWrapped()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() => new ConversationAdminClient(stub).GetSummaryAsync(Ct));

        ex.Message.Should().Be("Could not read the conversation store: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_RejectsAMissingDestinationOrAuthorization()
    {
        var client = new ConversationAdminClient(new StubClient());

        await Assert.ThrowsAsync<ArgumentException>(() => Drain(client.ExportAsync(new ConversationExportFilterInfo(), "", "authz", Ct)));
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(client.ExportAsync(new ConversationExportFilterInfo(), @"C:\a.zip", "", Ct)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Drain(client.ExportAsync(null!, @"C:\a.zip", "authz", Ct)));
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ConversationAdminClient((Contract.ConversationAdminService.ConversationAdminServiceClient)null!));
    }

    private static async Task<List<ConversationExportEvent>> Drain(IAsyncEnumerable<ConversationExportEvent> events)
    {
        var all = new List<ConversationExportEvent>();
        await foreach (var update in events) all.Add(update);
        return all;
    }

    /// <summary>An <see cref="IAsyncStreamReader{T}"/> fake that yields a fixed, in-memory sequence of messages.</summary>
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

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overloads: the generated
    /// convenience overloads delegate to them, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.ConversationAdminService.ConversationAdminServiceClient
    {
        public Contract.ConversationExportSummaryResponse Summary { get; init; } = new();

        public IReadOnlyList<Contract.ExportConversationsEvent> Events { get; init; } = [];

        public RpcException? Failure { get; init; }

        public Contract.ExportConversationsRequest? LastExportRequest { get; private set; }

        public override AsyncUnaryCall<Contract.ConversationExportSummaryResponse> GetConversationExportSummaryAsync(
            Contract.GetConversationExportSummaryRequest request, CallOptions options)
        {
            return new AsyncUnaryCall<Contract.ConversationExportSummaryResponse>(
                responseAsync: Failure is null
                    ? Task.FromResult(Summary)
                    : Task.FromException<Contract.ConversationExportSummaryResponse>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        public override AsyncServerStreamingCall<Contract.ExportConversationsEvent> ExportConversations(
            Contract.ExportConversationsRequest request, CallOptions options)
        {
            LastExportRequest = request;
            IAsyncStreamReader<Contract.ExportConversationsEvent> reader = Failure is null
                ? new FakeStreamReader<Contract.ExportConversationsEvent>(Events)
                : new ThrowingStreamReader(Failure);

            return new AsyncServerStreamingCall<Contract.ExportConversationsEvent>(
                responseStream: reader,
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        private sealed class ThrowingStreamReader(RpcException failure)
            : IAsyncStreamReader<Contract.ExportConversationsEvent>
        {
            public Contract.ExportConversationsEvent Current => throw failure;

            public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(failure);
        }
    }
}
