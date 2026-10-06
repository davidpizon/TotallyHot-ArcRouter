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
}
