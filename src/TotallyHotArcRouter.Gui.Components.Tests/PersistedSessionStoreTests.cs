using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="PersistedSessionStore"/>: the load-then-map round trip, the
/// capture-disabled/unreachable states, and the <see cref="PersistedSessionStore.Changed"/> notification
/// (docs/router/sessions-tab-training-data-plan.md Phase 2).
/// </summary>
public sealed class PersistedSessionStoreTests
{
    private static PersistedTranscriptDto CreateTranscript(
        string sessionId = "sess-1", int turnNumber = 1, long? memoryEntryId = null)
    {
        return new PersistedTranscriptDto(
            SessionId: sessionId,
            CorrelationId: $"{sessionId}:{turnNumber}",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: "gpt-5.4",
            RoutedModel: "kimi-k2.5",
            PromptText: "hello",
            ResponseText: "hi",
            0.01m,
            10,
            5,
            MemoryEntryId: memoryEntryId);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        var act = () => new PersistedSessionStore((IPersistedSessionsClient)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task LoadAsync_Success_PopulatesSessionsGroupedAndMapped()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(
                true,
                Transcripts: [CreateTranscript(memoryEntryId: 7)])
        };
        var store = new PersistedSessionStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeTrue();
        store.TranscriptCaptureEnabled.Should().BeTrue();
        var session = store.Sessions.Should().ContainSingle().Subject;
        session.Id.Should().Be("sess-1");
        session.IsUsedForTraining.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_TranscriptCaptureDisabled_ReportsFalseFlagWithEmptySessions()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(false, Transcripts: [])
        };
        var store = new PersistedSessionStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.TranscriptCaptureEnabled.Should().BeFalse();
        store.Sessions.Should().BeEmpty();
        store.IsReachable.Should().BeTrue("the call itself succeeded - capture is just off");
    }

    [Fact]
    public async Task LoadAsync_ClientThrows_SetsUnreachableWithoutThrowing()
    {
        var client = new FakePersistedSessionsClient
        {
            Failure = new GrpcAdminException(message: "router is gone", isUnavailable: true)
        };
        var store = new PersistedSessionStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeFalse();
        store.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_OversizedResponseRejection_StaysReachableWithNoSessionsAndCaptureReadingOff()
    {
        // The exact rejection PersistedSessionsClient raises when the list exceeds the client's default 4 MiB
        // receive cap - pinned end to end in PersistedSessionsClientTests. The router answered, so it is not
        // an outage.
        const string oversizedMessage =
            "Could not read persisted sessions: Received message exceeds the maximum configured message size.";
        var client = new FakePersistedSessionsClient { Failure = new GrpcAdminException(message: oversizedMessage) };
        var store = new PersistedSessionStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeTrue("a rejection reached the router");
        store.LastError.Should().Be(oversizedMessage);
        store.Sessions.Should().BeEmpty();
        store.TranscriptCaptureEnabled.Should()
            .BeFalse("only a successful load sets it, so a failed load reads the same as capture being off");
    }

    [Fact]
    public void HistoryNotice_BeforeTheFirstLoad_IsNull()
    {
        var store = new PersistedSessionStore(new FakePersistedSessionsClient());

        store.HistoryNotice.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_CompleteHistory_HasNoNotice()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(true, Transcripts: [CreateTranscript()], HasMore: false)
        };
        var store = new PersistedSessionStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.HasMore.Should().BeFalse();
        store.HistoryNotice.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_HasMore_SaysHowManyNewestTurnsAreShown()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(
                true,
                Transcripts: [CreateTranscript(turnNumber: 1), CreateTranscript(turnNumber: 2)],
                HasMore: true)
        };
        var store = new PersistedSessionStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.HasMore.Should().BeTrue();
        store.LoadedTurnCount.Should().Be(2);
        store.HistoryNotice.Should().Be("Showing the newest 2 persisted turns.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAsync_Failure_ReportsTheErrorInTheNotice(bool isUnavailable)
    {
        const string message = "Could not read persisted sessions: boom";
        var client = new FakePersistedSessionsClient
        {
            Failure = new GrpcAdminException(message: message, isUnavailable: isUnavailable)
        };
        var store = new PersistedSessionStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.HistoryNotice.Should().Be($"Persisted history couldn't be loaded: {message}");
    }

    [Fact]
    public async Task LoadAsync_RaisesChangedExactlyOnce()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(true, Transcripts: [])
        };
        var store = new PersistedSessionStore(client);
        var changedCount = 0;
        store.Changed += () => changedCount++;

        await store.LoadAsync(TestContext.Current.CancellationToken);

        changedCount.Should().Be(1);
    }

    [Fact]
    public async Task ClearConversationText_erases_prompts_and_responses_but_keeps_metadata()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(true, Transcripts: [CreateTranscript()])
        };
        var store = new PersistedSessionStore(client);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        store.Sessions.Single().Turns.Single().RequestSummary.Should().Be("hello");
        var changed = 0;
        store.Changed += () => changed++;

        store.ClearConversationText();

        var session = store.Sessions.Should().ContainSingle().Subject;
        var turn = session.Turns.Should().ContainSingle().Subject;
        turn.RequestSummary.Should().BeNull();
        turn.ResponseSummary.Should().BeNull();
        turn.PromptTokens.Should().Be(10);
        turn.Model.Should().Be("kimi-k2.5");
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Clearing_the_content_grant_erases_the_persisted_text()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(true, Transcripts: [CreateTranscript()])
        };
        var grant = new ContentGrantStore();
        var store = new PersistedSessionStore(client, grant);
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(5));
        await store.LoadAsync(TestContext.Current.CancellationToken);

        grant.Clear();

        store.Sessions.Single().Turns.Single().RequestSummary.Should().BeNull();
    }

    [Fact]
    public async Task Setting_the_content_grant_reloads_the_sessions_so_the_released_text_appears()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(true, Transcripts: [CreateTranscript()])
        };
        var grant = new ContentGrantStore();
        var store = new PersistedSessionStore(client, grant);
        var loaded = new TaskCompletionSource();
        store.Changed += () => loaded.TrySetResult();

        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(5));

        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        store.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task A_rejected_grant_on_load_clears_the_grant()
    {
        var rpc = new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "grant expired"));
        var client = new FakePersistedSessionsClient
        {
            Failure = new GrpcAdminException(message: "Could not read persisted sessions: grant expired",
                innerException: rpc)
        };
        var grant = new ContentGrantStore();
        var store = new PersistedSessionStore(client, grant);
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(5));
        // SetGrant triggers its own background reload; let it settle before asserting on the explicit one.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        grant.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task A_non_authentication_failure_leaves_the_grant_alone()
    {
        var client = new FakePersistedSessionsClient
        {
            Failure = new GrpcAdminException(message: "router is gone", isUnavailable: true)
        };
        var grant = new ContentGrantStore();
        var store = new PersistedSessionStore(client, grant);
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(5));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        grant.IsActive.Should().BeTrue("an outage says nothing about the grant");
    }

    [Fact]
    public void Dispose_OverCallerSuppliedClient_DoesNotDisposeTheClient()
    {
        var client = new FakePersistedSessionsClient();
        var store = new PersistedSessionStore(client);

        store.Dispose();

        client.Disposed.Should().BeFalse();
    }

    private sealed class FakePersistedSessionsClient : IPersistedSessionsClient, IDisposable
    {
        public PersistedSessionsResult Result { get; init; } = new(true, Transcripts: []);

        public Exception? Failure { get; init; }

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }

        public Task<PersistedSessionsResult> ListAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Failure is not null ? Task.FromException<PersistedSessionsResult>(Failure) : Task.FromResult(Result);
        }
    }
}