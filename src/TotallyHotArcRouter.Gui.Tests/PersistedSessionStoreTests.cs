using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="PersistedSessionStore"/>: the load-then-map round trip, the
/// capture-disabled/unreachable states, and the <see cref="PersistedSessionStore.Changed"/> notification
/// (docs/router/sessions-tab-training-data-plan.md Phase 2).
/// </summary>
public sealed class PersistedSessionStoreTests
{
    private static PersistedTranscriptDto CreateTranscript(
        string sessionId = "sess-1", int turnNumber = 1, long? memoryEntryId = null) => new(
        SessionId: sessionId,
        CorrelationId: $"{sessionId}:{turnNumber}",
        CreatedAtUtc: DateTimeOffset.UtcNow,
        RequestedModel: "gpt-5.4",
        RoutedModel: "kimi-k2.5",
        PromptText: "hello",
        ResponseText: "hi",
        CostUsd: 0.01m,
        InputTokens: 10,
        OutputTokens: 5,
        MemoryEntryId: memoryEntryId);

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
                TranscriptCaptureEnabled: true,
                Transcripts: [CreateTranscript(memoryEntryId: 7)]),
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
            Result = new PersistedSessionsResult(TranscriptCaptureEnabled: false, Transcripts: []),
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
            Failure = new PersistedSessionsClientException("router is gone", isUnavailable: true),
        };
        var store = new PersistedSessionStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeFalse();
        store.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_RaisesChangedExactlyOnce()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(TranscriptCaptureEnabled: true, Transcripts: []),
        };
        var store = new PersistedSessionStore(client);
        var changedCount = 0;
        store.Changed += () => changedCount++;

        await store.LoadAsync(TestContext.Current.CancellationToken);

        changedCount.Should().Be(1);
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
        public PersistedSessionsResult Result { get; set; } = new(true, []);

        public Exception? Failure { get; set; }

        public bool Disposed { get; private set; }

        public Task<PersistedSessionsResult> ListAsync(int limit, CancellationToken cancellationToken = default) =>
            Failure is not null ? Task.FromException<PersistedSessionsResult>(Failure) : Task.FromResult(Result);

        public void Dispose() => Disposed = true;
    }
}
