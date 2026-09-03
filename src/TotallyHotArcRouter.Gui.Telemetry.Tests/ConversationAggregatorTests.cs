namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>Covers <see cref="ConversationAggregator.Aggregate"/>: grouping, ordering, and totals.</summary>
public class ConversationAggregatorTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 8, 12, 0, 0, offset: TimeSpan.Zero);

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
        var events = new[] { CreateEvent(sessionId: "session-1") };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(expected: "session-1", actual: conversation.SessionId);
        var turn = Assert.Single(conversation.Turns);
        Assert.Equal(1, actual: turn.TurnNumber);
    }

    [Fact]
    public void Aggregate_MultipleEventsSameSession_GroupsOrdersAndSumsCorrectly()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", promptTokens: 100, completionTokens: 20, estimatedCostUsd: 0.01m),
            CreateEvent(sessionId: "session-1", 2, promptTokens: 150, completionTokens: 30, estimatedCostUsd: 0.02m),
            CreateEvent(sessionId: "session-1", 3, promptTokens: 200, completionTokens: 40, estimatedCostUsd: 0.03m)
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(expected: "session-1", actual: conversation.SessionId);
        Assert.Equal(3, actual: conversation.Turns.Count);
        Assert.Equal(expected: [1, 2, 3], actual: conversation.Turns.Select(t => t.TurnNumber));
        Assert.Equal(450, actual: conversation.TotalPromptTokens);
        Assert.Equal(90, actual: conversation.TotalCompletionTokens);
        Assert.Equal(0.06m, actual: conversation.TotalCost);
    }

    [Fact]
    public void Aggregate_MultipleSessions_ReturnsIndependentConversationsOrderedByLastTimestampDescending()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "older", timestampUtc: BaseTime),
            CreateEvent(sessionId: "newer", timestampUtc: BaseTime.AddHours(1)),
            CreateEvent(sessionId: "middle", timestampUtc: BaseTime.AddMinutes(30))
        };

        var result = ConversationAggregator.Aggregate(events);

        Assert.Equal(expected: ["newer", "middle", "older"], actual: result.Select(c => c.SessionId));
    }

    [Fact]
    public void Aggregate_UnsortedByTurnNumberInput_StillProducesCorrectlyOrderedTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", 3, timestampUtc: BaseTime.AddMinutes(3)),
            CreateEvent(sessionId: "session-1", timestampUtc: BaseTime.AddMinutes(1)),
            CreateEvent(sessionId: "session-1", 2, timestampUtc: BaseTime.AddMinutes(2))
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(expected: [1, 2, 3], actual: conversation.Turns.Select(t => t.TurnNumber));
        Assert.Equal(expected: BaseTime.AddMinutes(1), actual: conversation.FirstTimestampUtc);
        Assert.Equal(expected: BaseTime.AddMinutes(3), actual: conversation.LastTimestampUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Aggregate_IsSessionSynthesized_PropagatesFromFirstTurn(bool isSynthesized)
    {
        var events = new[] { CreateEvent(sessionId: "session-1", isSessionSynthesized: isSynthesized) };

        var result = ConversationAggregator.Aggregate(events);

        Assert.Equal(expected: isSynthesized, actual: result[0].IsSessionSynthesized);
    }

    [Fact]
    public void Aggregate_NoFallbackTurns_HasFallbackTurnsIsFalse()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1"),
            CreateEvent(sessionId: "session-1", 2, isFallback: false)
        };

        var result = ConversationAggregator.Aggregate(events);

        Assert.False(result[0].HasFallbackTurns);
    }

    [Fact]
    public void Aggregate_OneFallbackTurnAmongMany_HasFallbackTurnsIsTrue()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1"),
            CreateEvent(sessionId: "session-1", 2, isFallback: true)
        };

        var result = ConversationAggregator.Aggregate(events);

        Assert.True(result[0].HasFallbackTurns);
    }

    [Fact]
    public void Aggregate_NullTokenAndCostFields_TreatedAsZeroInTotalsAndTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", promptTokens: null, completionTokens: null, estimatedCostUsd: null)
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(0, actual: conversation.TotalPromptTokens);
        Assert.Equal(0, actual: conversation.TotalCompletionTokens);
        Assert.Equal(0m, actual: conversation.TotalCost);
        var turn = Assert.Single(conversation.Turns);
        Assert.Equal(0, actual: turn.PromptTokens);
        Assert.Equal(0, actual: turn.CompletionTokens);
        Assert.Equal(0m, actual: turn.EstimatedCostUsd);
    }

    [Fact]
    public void Aggregate_RequestAndResponseSummaries_PassThroughUnchanged()
    {
        var events = new[]
        {
            CreateEvent(
                sessionId: "session-1",
                requestSummary: "What is the capital of France?",
                responseSummary: "The capital of France is Paris.")
        };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Equal(expected: "What is the capital of France?", actual: turn.RequestSummary);
        Assert.Equal(expected: "The capital of France is Paris.", actual: turn.ResponseSummary);
    }

    [Fact]
    public void Aggregate_NoRequestOrResponseSummary_TurnFieldsAreNull()
    {
        var events = new[] { CreateEvent(sessionId: "session-1") };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Null(turn.RequestSummary);
        Assert.Null(turn.ResponseSummary);
    }

    [Fact]
    public void Aggregate_TurnAgentAndModel_BothSetToResolvedModel()
    {
        var events = new[] { CreateEvent(sessionId: "session-1", resolvedModel: "claude-sonnet-5") };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Equal(expected: "claude-sonnet-5", actual: turn.Agent);
        Assert.Equal(expected: "claude-sonnet-5", actual: turn.Model);
    }

    [Fact]
    public void Aggregate_RequestedRoutedAndSubstitutionReason_PropagateThroughToTheTurn()
    {
        var events = new[]
        {
            CreateEvent(
                sessionId: "session-1",
                requestedModel: "auto",
                routedModel: "claude-sonnet-5",
                substitutionReason: "AutoSelect")
        };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Equal(expected: "auto", actual: turn.RequestedModel);
        Assert.Equal(expected: "claude-sonnet-5", actual: turn.RoutedModel);
        Assert.Equal(expected: "AutoSelect", actual: turn.SubstitutionReason);
    }

    [Fact]
    public void Aggregate_NullCacheTokenFields_TreatedAsZeroInTotalsAndTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1")
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(0, actual: conversation.TotalCacheCreationTokens);
        Assert.Equal(0, actual: conversation.TotalCacheReadTokens);
        var turn = Assert.Single(conversation.Turns);
        Assert.Equal(0, actual: turn.CacheCreationTokens);
        Assert.Equal(0, actual: turn.CacheReadTokens);
    }

    [Fact]
    public void Aggregate_CacheTokenFields_SummedAcrossTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1", cacheCreationTokens: 30, cacheReadTokens: 500),
            CreateEvent(sessionId: "session-1", 2, cacheCreationTokens: 10, cacheReadTokens: 200)
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(40, actual: conversation.TotalCacheCreationTokens);
        Assert.Equal(700, actual: conversation.TotalCacheReadTokens);
    }

    [Fact]
    public void Aggregate_NoUnpricedTurns_UnpricedTurnsIsZero()
    {
        var events = new[] { CreateEvent(sessionId: "session-1") };

        var result = ConversationAggregator.Aggregate(events);

        Assert.Equal(0, actual: result[0].UnpricedTurns);
    }

    [Fact]
    public void Aggregate_SomeTurnsWithNullCost_CountsThemAsUnpriced_WithoutTaintingPricedTurns()
    {
        var events = new[]
        {
            CreateEvent(sessionId: "session-1"),
            CreateEvent(sessionId: "session-1", 2, estimatedCostUsd: null),
            CreateEvent(sessionId: "session-1", 3, estimatedCostUsd: null)
        };

        var result = ConversationAggregator.Aggregate(events);

        var conversation = Assert.Single(result);
        Assert.Equal(2, actual: conversation.UnpricedTurns);
        Assert.Equal(0.01m, actual: conversation.TotalCost);
    }

    [Fact]
    public void Aggregate_CostConfidence_PassesThroughToTurn()
    {
        var events = new[] { CreateEvent(sessionId: "session-1", costConfidence: "CatalogApproximate") };

        var result = ConversationAggregator.Aggregate(events);

        var turn = Assert.Single(result[0].Turns);
        Assert.Equal(expected: "CatalogApproximate", actual: turn.CostConfidence);
    }
}