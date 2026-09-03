namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>Covers <see cref="ConversationAggregator.Aggregate"/>: grouping, ordering, and totals.</summary>
public class ConversationAggregatorTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private static RoutingTelemetryEventDto CreateEvent(
        string sessionId = "session-1",
        int turnNumber = 1,
        bool isSessionSynthesized = false,
        string requestedModel = "gpt-4o",
        string resolvedModel = "gpt-4o-mini",
        string provider = "openai",
        bool isFallback = false,
        int? promptTokens = 100,
        int? completionTokens = 50,
        decimal? estimatedCostUsd = 0.01m,
        bool isStreaming = false,
        long latencyToHeadersMs = 120,
        long totalDurationMs = 400,
        int statusCode = 200,
        DateTimeOffset? timestampUtc = null,
        string? requestSummary = null,
        string? responseSummary = null,
        int? cacheCreationTokens = null,
        int? cacheReadTokens = null,
        string? costConfidence = null,
        string? routedModel = null,
        string? substitutionReason = null)
    {
        return new RoutingTelemetryEventDto(
            SessionId: sessionId,
            TurnNumber: turnNumber,
            IsSessionSynthesized: isSessionSynthesized,
            RequestedModel: requestedModel,
            ResolvedModel: resolvedModel,
            Provider: provider,
            IsFallback: isFallback,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            EstimatedCostUsd: estimatedCostUsd,
            IsStreaming: isStreaming,
            LatencyToHeadersMs: latencyToHeadersMs,
            TotalDurationMs: totalDurationMs,
            StatusCode: statusCode,
            TimestampUtc: timestampUtc ?? BaseTime.AddMinutes(turnNumber),
            RoutedModel: routedModel ?? resolvedModel,
            CacheCreationTokens: cacheCreationTokens,
            CacheReadTokens: cacheReadTokens,
            RequestSummary: requestSummary,
            ResponseSummary: responseSummary,
            CostConfidence: costConfidence,
            SubstitutionReason: substitutionReason);
    }

    [Fact]
    public void Aggregate_NullEvents_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConversationAggregator.Aggregate(null!));
    }

    [Fact]
    public void Aggregate_EmptyList_ReturnsEmpty()
    {
        var result = ConversationAggregator.Aggregate([]);

        Assert.Empty(result);
    }

    [Fact]
    public void Aggregate_SingleEvent_ReturnsSingleOneTurnConversation()
    {
        var events = new[] { CreateEvent(sessionId: "session-1", turnNumber: 1) };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal("session-1", conversation.SessionId);
        var turn = Assert.Single(conversation.Turns);
        Assert.Equal(1, turn.TurnNumber);
    }

    [Fact]
    public void Aggregate_MultipleEventsSameSession_GroupsOrdersAndSumsCorrectly()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", turnNumber: 1, promptTokens: 100, completionTokens: 20, estimatedCostUsd: 0.01m),
            CreateEvent(sessionId: "session-1", turnNumber: 2, promptTokens: 150, completionTokens: 30, estimatedCostUsd: 0.02m),
            CreateEvent(sessionId: "session-1", turnNumber: 3, promptTokens: 200, completionTokens: 40, estimatedCostUsd: 0.03m),
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal("session-1", conversation.SessionId);
        Assert.Equal(3, conversation.Turns.Count);
        Assert.Equal([1, 2, 3], conversation.Turns.Select(t => t.TurnNumber));
        Assert.Equal(450, conversation.TotalPromptTokens);
        Assert.Equal(90, conversation.TotalCompletionTokens);
        Assert.Equal(0.06m, conversation.TotalCost);
    }

    [Fact]
    public void Aggregate_MultipleSessions_ReturnsIndependentConversationsOrderedByLastTimestampDescending()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "older", turnNumber: 1, timestampUtc: BaseTime),
            CreateEvent(sessionId: "newer", turnNumber: 1, timestampUtc: BaseTime.AddHours(1)),
            CreateEvent(sessionId: "middle", turnNumber: 1, timestampUtc: BaseTime.AddMinutes(30)),
        };

        var result = ConversationAggregator.Aggregate(events);

        Assert.Equal(["newer", "middle", "older"], result.Select(c => c.SessionId));
    }

    [Fact]
    public void Aggregate_UnsortedByTurnNumberInput_StillProducesCorrectlyOrderedTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", turnNumber: 3, timestampUtc: BaseTime.AddMinutes(3)),
            CreateEvent(sessionId: "session-1", turnNumber: 1, timestampUtc: BaseTime.AddMinutes(1)),
            CreateEvent(sessionId: "session-1", turnNumber: 2, timestampUtc: BaseTime.AddMinutes(2)),
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal([1, 2, 3], conversation.Turns.Select(t => t.TurnNumber));
        Assert.Equal(BaseTime.AddMinutes(1), conversation.FirstTimestampUtc);
        Assert.Equal(BaseTime.AddMinutes(3), conversation.LastTimestampUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Aggregate_IsSessionSynthesized_PropagatesFromFirstTurn(bool isSynthesized)
    {
        var events = new[] { CreateEvent(sessionId: "session-1", turnNumber: 1, isSessionSynthesized: isSynthesized) };

        var result = ConversationAggregator.Aggregate(events);

        Assert.Equal(isSynthesized, result[0].IsSessionSynthesized);
    }

    [Fact]
    public void Aggregate_NoFallbackTurns_HasFallbackTurnsIsFalse()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", turnNumber: 1, isFallback: false),
            CreateEvent(sessionId: "session-1", turnNumber: 2, isFallback: false),
        };

        var result = ConversationAggregator.Aggregate(events);

        Assert.False(result[0].HasFallbackTurns);
    }

    [Fact]
    public void Aggregate_OneFallbackTurnAmongMany_HasFallbackTurnsIsTrue()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", turnNumber: 1, isFallback: false),
            CreateEvent(sessionId: "session-1", turnNumber: 2, isFallback: true),
        };

        var result = ConversationAggregator.Aggregate(events);

        Assert.True(result[0].HasFallbackTurns);
    }

    [Fact]
    public void Aggregate_NullTokenAndCostFields_TreatedAsZeroInTotalsAndTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", turnNumber: 1, promptTokens: null, completionTokens: null, estimatedCostUsd: null),
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(0, conversation.TotalPromptTokens);
        Assert.Equal(0, conversation.TotalCompletionTokens);
        Assert.Equal(0m, conversation.TotalCost);
        var turn = Assert.Single(conversation.Turns);
        Assert.Equal(0, turn.PromptTokens);
        Assert.Equal(0, turn.CompletionTokens);
        Assert.Equal(0m, turn.EstimatedCostUsd);
    }

    [Fact]
    public void Aggregate_RequestAndResponseSummaries_PassThroughUnchanged()
    {
        var events = new[]
        {
            CreateEvent(
                sessionId: "session-1",
                turnNumber: 1,
                requestSummary: "What is the capital of France?",
                responseSummary: "The capital of France is Paris."),
        };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Equal("What is the capital of France?", turn.RequestSummary);
        Assert.Equal("The capital of France is Paris.", turn.ResponseSummary);
    }

    [Fact]
    public void Aggregate_NoRequestOrResponseSummary_TurnFieldsAreNull()
    {
        var events = new[] { CreateEvent(sessionId: "session-1", turnNumber: 1) };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Null(turn.RequestSummary);
        Assert.Null(turn.ResponseSummary);
    }

    [Fact]
    public void Aggregate_TurnAgentAndModel_BothSetToResolvedModel()
    {
        var events = new[] { CreateEvent(sessionId: "session-1", turnNumber: 1, resolvedModel: "claude-sonnet-5") };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Equal("claude-sonnet-5", turn.Agent);
        Assert.Equal("claude-sonnet-5", turn.Model);
    }

    [Fact]
    public void Aggregate_RequestedRoutedAndSubstitutionReason_PropagateThroughToTheTurn()
    {
        var events = new[]
        {
            CreateEvent(
                sessionId: "session-1",
                turnNumber: 1,
                requestedModel: "auto",
                routedModel: "claude-sonnet-5",
                substitutionReason: "AutoSelect"),
        };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Equal("auto", turn.RequestedModel);
        Assert.Equal("claude-sonnet-5", turn.RoutedModel);
        Assert.Equal("AutoSelect", turn.SubstitutionReason);
    }

    [Fact]
    public void Aggregate_NullCacheTokenFields_TreatedAsZeroInTotalsAndTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", turnNumber: 1, cacheCreationTokens: null, cacheReadTokens: null),
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(0, conversation.TotalCacheCreationTokens);
        Assert.Equal(0, conversation.TotalCacheReadTokens);
        var turn = Assert.Single(conversation.Turns);
        Assert.Equal(0, turn.CacheCreationTokens);
        Assert.Equal(0, turn.CacheReadTokens);
    }

    [Fact]
    public void Aggregate_CacheTokenFields_SummedAcrossTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", turnNumber: 1, cacheCreationTokens: 30, cacheReadTokens: 500),
            CreateEvent(sessionId: "session-1", turnNumber: 2, cacheCreationTokens: 10, cacheReadTokens: 200),
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(40, conversation.TotalCacheCreationTokens);
        Assert.Equal(700, conversation.TotalCacheReadTokens);
    }

    [Fact]
    public void Aggregate_NoUnpricedTurns_UnpricedTurnsIsZero()
    {
        var events = new[] { CreateEvent(sessionId: "session-1", turnNumber: 1, estimatedCostUsd: 0.01m) };

        var result = ConversationAggregator.Aggregate(events);

        Assert.Equal(0, result[0].UnpricedTurns);
    }

    [Fact]
    public void Aggregate_SomeTurnsWithNullCost_CountsThemAsUnpriced_WithoutTaintingPricedTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", turnNumber: 1, estimatedCostUsd: 0.01m),
            CreateEvent(sessionId: "session-1", turnNumber: 2, estimatedCostUsd: null),
            CreateEvent(sessionId: "session-1", turnNumber: 3, estimatedCostUsd: null),
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(2, conversation.UnpricedTurns);
        Assert.Equal(0.01m, conversation.TotalCost);
    }

    [Fact]
    public void Aggregate_CostConfidence_PassesThroughToTurn()
    {
        var events = new[] { CreateEvent(sessionId: "session-1", turnNumber: 1, costConfidence: "CatalogApproximate") };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Equal("CatalogApproximate", turn.CostConfidence);
    }
}

