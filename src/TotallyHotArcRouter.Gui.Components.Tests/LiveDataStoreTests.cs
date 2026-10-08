using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for the parts of <see cref="LiveDataStore"/> that don't require a live gRPC server: initial
/// property state, <see cref="LiveDataStore.ClearLogLines"/>, and the start/dispose lifecycle. The
/// background <c>StreamEvents</c> loop itself is backed by a stub that fails every RPC as unavailable,
/// so it retries quietly in the background - consistent with the class's own "not unit-tested" remarks
/// for the networking path; these tests only cover the surrounding, deterministic surface.
/// </summary>
public sealed class LiveDataStoreTests
{
    private const string UnreachableAddress = "https://127.0.0.1:59993";

    [Fact]
    public void Conversations_and_LogLines_start_empty()
    {
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));

        store.Conversations.Should().BeEmpty();
        store.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void ClearLogLines_raises_LogLinesChanged()
    {
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));
        var raised = false;
        store.LogLinesChanged += () => raised = true;

        store.ClearLogLines();

        raised.Should().BeTrue();
        store.LogLines.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_returns_immediately_and_can_be_disposed_right_away()
    {
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));

        await store.StartAsync(TestContext.Current.CancellationToken);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_called_twice_cancels_the_first_loop_instead_of_leaking_it()
    {
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));

        await store.StartAsync(TestContext.Current.CancellationToken);
        await store.StartAsync(TestContext.Current.CancellationToken);

        await store.DisposeAsync();
    }

    /// <summary>
    /// Incremental per-session aggregation must produce exactly what a full re-aggregation of the whole
    /// event history produces, including the order of conversations.
    /// </summary>
    [Fact]
    public void Incremental_aggregation_matches_full_reaggregation()
    {
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var events = new List<RoutingTelemetryEventDto>();
        for (var i = 0; i < 300; i++)
        {
            var dto = new RoutingTelemetryEventDto(
                SessionId: $"s{i % 7}", TurnNumber: i / 7, IsSessionSynthesized: false,
                RequestedModel: "auto", ResolvedModel: $"m{i % 3}", Provider: "p", IsFallback: i % 11 == 0,
                PromptTokens: i, CompletionTokens: 2 * i, EstimatedCostUsd: i / 100m, IsStreaming: false,
                LatencyToHeadersMs: i, TotalDurationMs: i, StatusCode: 200,
                TimestampUtc: start.AddSeconds(i % 50 == 0 ? 0 : i), RoutedModel: $"m{i % 3}");
            events.Add(dto);
            store.OnRoutingTelemetryReceived(dto);
        }

        var expected = ConversationAggregator.Aggregate(events).Select(LiveConversationMapper.ToModel).ToList();

        store.Conversations.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// ADR-0021: <see cref="LiveDataStore.MapToDto(Contract.RoutingTelemetryEvent)"/> must honour
    /// optional-field presence so a live badge appears when the router sends <c>subagent_signal</c>
    /// and stays absent when an older writer omits the field.
    /// </summary>
    [Theory]
    [InlineData(true, "claude-code/explore")]
    [InlineData(false, null)]
    public void MapToDto_carries_subagent_signal_only_when_the_wire_field_is_present(
        bool present, string? expected)
    {
        var wire = new Contract.RoutingTelemetryEvent
        {
            SessionId = "s1",
            TurnNumber = 1,
            IsSessionSynthesized = false,
            RequestedModel = "auto",
            ResolvedModel = "claude-sonnet-5",
            RoutedModel = "claude-sonnet-5",
            SubstitutionReason = "AutoSelect",
            Provider = "anthropic",
            IsFallback = false,
            IsStreaming = false,
            LatencyToHeadersMs = 10,
            TotalDurationMs = 20,
            StatusCode = 200,
            TimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            RouterTokens = 0,
            RouterCostUsd = "0"
        };
        if (present) wire.SubagentSignal = "claude-code/explore";

        var dto = LiveDataStore.MapToDto(wire);

        dto.SubagentSignal.Should().Be(expected);
    }

    /// <summary>The live view keeps at most <see cref="LiveDataStore.MaxRetainedSessions"/> sessions.</summary>
    [Fact]
    public void Retention_is_bounded_to_the_most_recent_sessions()
    {
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i < LiveDataStore.MaxRetainedSessions + 25; i++)
            store.OnRoutingTelemetryReceived(new RoutingTelemetryEventDto(
                SessionId: $"s{i}", TurnNumber: 0, IsSessionSynthesized: false, RequestedModel: "auto",
                ResolvedModel: "m", Provider: "p", IsFallback: false, PromptTokens: 1, CompletionTokens: 1,
                EstimatedCostUsd: 0m, IsStreaming: false, LatencyToHeadersMs: 1, TotalDurationMs: 1, StatusCode: 200,
                TimestampUtc: start.AddSeconds(i), RoutedModel: "m"));

        store.Conversations.Should().HaveCount(LiveDataStore.MaxRetainedSessions);
        store.Conversations.Select(c => c.Id).Should().NotContain("s0");
        store.Conversations[0].Id.Should().Be($"s{LiveDataStore.MaxRetainedSessions + 24}");
    }

    private static RoutingTelemetryEventDto EventWithText(string session, int turn, DateTimeOffset at)
    {
        return new RoutingTelemetryEventDto(
            SessionId: session, TurnNumber: turn, IsSessionSynthesized: false, RequestedModel: "auto",
            ResolvedModel: "m", Provider: "p", IsFallback: false, PromptTokens: 10, CompletionTokens: 5,
            EstimatedCostUsd: 0.02m, IsStreaming: false, LatencyToHeadersMs: 1, TotalDurationMs: 1, StatusCode: 200,
            TimestampUtc: at, RoutedModel: "m", RequestSummary: "secret prompt", ResponseSummary: "secret reply");
    }

    /// <summary>
    /// ADR-0020: locking must erase every cached prompt and response from the live view but leave the metadata
    /// (turns, tokens, cost) so the Sessions tab keeps listing what happened.
    /// </summary>
    [Fact]
    public void ClearConversationText_erases_summaries_but_keeps_metadata()
    {
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));
        var now = DateTimeOffset.UtcNow;
        store.OnRoutingTelemetryReceived(EventWithText("s1", 1, now));
        store.OnRoutingTelemetryReceived(EventWithText("s1", 2, now.AddSeconds(1)));
        store.Conversations.Single().Turns.Should().OnlyContain(t => t.RequestSummary == "secret prompt");
        var changed = 0;
        store.Changed += () => changed++;

        store.ClearConversationText();

        var conversation = store.Conversations.Single();
        conversation.Turns.Should().HaveCount(2);
        conversation.Turns.Should().OnlyContain(t => t.RequestSummary == null && t.ResponseSummary == null);
        conversation.TotalPromptTokens.Should().Be(20);
        conversation.TotalCost.Should().Be(0.04m);
        changed.Should().Be(1);
    }

    [Fact]
    public void ClearConversationText_is_not_undone_by_the_next_event_for_the_same_session()
    {
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));
        var now = DateTimeOffset.UtcNow;
        store.OnRoutingTelemetryReceived(EventWithText("s1", 1, now));
        store.ClearConversationText();

        store.OnRoutingTelemetryReceived(EventWithText("s1", 2, now.AddSeconds(1)) with
        {
            RequestSummary = null,
            ResponseSummary = null
        });

        store.Conversations.Single().Turns.Should().OnlyContain(t => t.RequestSummary == null);
    }

    [Fact]
    public void Clearing_the_content_grant_erases_the_live_text()
    {
        var grant = new ContentGrantStore();
        var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress),
            contentGrant: grant);
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(5));
        store.OnRoutingTelemetryReceived(EventWithText("s1", 1, DateTimeOffset.UtcNow));

        grant.Clear();

        store.Conversations.Single().Turns.Should().OnlyContain(t => t.RequestSummary == null);
    }

    [Fact]
    public async Task A_stream_that_fails_clears_the_content_grant_before_reconnecting()
    {
        // The stub fails every stream as Unavailable, which stands in for a router that restarted and so
        // lost every grant: nothing can vouch for the one the dashboard holds.
        var grant = new ContentGrantStore();
        await using var store = new LiveDataStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress),
            contentGrant: grant);
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(5));
        var cleared = new TaskCompletionSource();
        grant.Cleared += () => cleared.TrySetResult();

        await store.StartAsync(TestContext.Current.CancellationToken);

        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        grant.IsActive.Should().BeFalse();
    }
}
