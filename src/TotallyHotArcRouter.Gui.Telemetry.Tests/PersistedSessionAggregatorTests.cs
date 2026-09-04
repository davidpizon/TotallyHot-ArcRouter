namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Covers <see cref="PersistedSessionAggregator.Aggregate"/>: grouping, turn-number parsing, ordering, and
/// totals.
/// </summary>
public class PersistedSessionAggregatorTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 8, 12, 0, 0, offset: TimeSpan.Zero);

    private static PersistedTranscriptDto CreateTranscript(
        string sessionId = "sess-1",
        int turnNumber = 1,
        string requestedModel = "gpt-5.4",
        string routedModel = "kimi-k2.5",
        string? promptText = "hello",
        string? responseText = "hi",
        decimal? costUsd = 0.01m,
        int? inputTokens = 100,
        int? outputTokens = 50,
        DateTimeOffset? createdAtUtc = null,
        long? memoryEntryId = null)
    {
        return new PersistedTranscriptDto(
            SessionId: sessionId,
            CorrelationId: $"{sessionId}:{turnNumber}",
            CreatedAtUtc: createdAtUtc ?? BaseTime.AddMinutes(turnNumber),
            RequestedModel: requestedModel,
            RoutedModel: routedModel,
            PromptText: promptText,
            ResponseText: responseText,
            CostUsd: costUsd,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            MemoryEntryId: memoryEntryId);
    }

    [Fact]
    public void Aggregate_NullTranscripts_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PersistedSessionAggregator.Aggregate(null!));
    }

    [Fact]
    public void Aggregate_GroupsRowsBySessionId()
    {
        var rows = new[]
        {
            CreateTranscript(sessionId: "sess-1"),
            CreateTranscript(sessionId: "sess-2"),
            CreateTranscript(sessionId: "sess-1", 2)
        };

        var result = PersistedSessionAggregator.Aggregate(rows);

        Assert.Equal(2, actual: result.Count);
        var sessOne = Assert.Single(collection: result, predicate: c => c.SessionId == "sess-1");
        Assert.Equal(2, actual: sessOne.Turns.Count);
    }

    [Fact]
    public void Aggregate_OrdersTurnsByParsedTurnNumberNotInsertOrder()
    {
        var rows = new[]
        {
            CreateTranscript(sessionId: "sess-1", 3),
            CreateTranscript(sessionId: "sess-1"),
            CreateTranscript(sessionId: "sess-1", 2)
        };

        var result = PersistedSessionAggregator.Aggregate(rows);

        var session = Assert.Single(result);
        Assert.Equal(expected: [1, 2, 3], actual: session.Turns.Select(t => t.TurnNumber));
    }

    [Fact]
    public void Aggregate_OrdersSessionsMostRecentlyActiveFirst()
    {
        var rows = new[]
        {
            CreateTranscript(sessionId: "old", createdAtUtc: BaseTime),
            CreateTranscript(sessionId: "new", createdAtUtc: BaseTime.AddHours(2))
        };

        var result = PersistedSessionAggregator.Aggregate(rows);

        Assert.Equal(expected: ["new", "old"], actual: result.Select(c => c.SessionId));
    }

    [Fact]
    public void Aggregate_CorrelationIdWithNoNumericSuffix_TurnNumberDefaultsToOne()
    {
        var row = CreateTranscript(sessionId: "sess-1") with { CorrelationId = "not-numeric-suffix:abc" };

        var session = Assert.Single(PersistedSessionAggregator.Aggregate([row]));

        Assert.Equal(1, actual: session.Turns[0].TurnNumber);
    }

    [Fact]
    public void Aggregate_SumsKnownCostsAndTokensTreatingNullAsZero()
    {
        var rows = new[]
        {
            CreateTranscript(sessionId: "sess-1", costUsd: 0.02m, inputTokens: 10, outputTokens: 5),
            CreateTranscript(sessionId: "sess-1", 2, costUsd: null, inputTokens: null, outputTokens: null)
        };

        var session = Assert.Single(PersistedSessionAggregator.Aggregate(rows));

        Assert.Equal(0.02m, actual: session.TotalCostUsd);
        Assert.Equal(10, actual: session.TotalInputTokens);
        Assert.Equal(5, actual: session.TotalOutputTokens);
    }

    [Fact]
    public void Aggregate_AnyTurnLinkedToMemoryEntry_MarksSessionUsedForTraining()
    {
        var rows = new[]
        {
            CreateTranscript(sessionId: "sess-1"),
            CreateTranscript(sessionId: "sess-1", 2, memoryEntryId: 42)
        };

        var session = Assert.Single(PersistedSessionAggregator.Aggregate(rows));

        Assert.True(session.IsUsedForTraining);
    }

    [Fact]
    public void Aggregate_NoTurnLinkedToMemoryEntry_SessionNotUsedForTraining()
    {
        var rows = new[] { CreateTranscript(sessionId: "sess-1") };

        var session = Assert.Single(PersistedSessionAggregator.Aggregate(rows));

        Assert.False(session.IsUsedForTraining);
    }
}