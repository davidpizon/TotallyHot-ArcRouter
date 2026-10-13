using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Covers <see cref="SessionMerger"/> (#176 §5.1): per-turn union of live and persisted history.</summary>
public sealed class SessionMergerTests
{
    [Fact]
    public void Merge_EmptyPersisted_ReturnsLiveUnchanged()
    {
        var live = new[] { MakeConversation("s1", turn: 1, isLive: true) };

        var result = SessionMerger.Merge(live, []);

        Assert.Same(live, result);
    }

    [Fact]
    public void Merge_EmptyLive_ReturnsPersistedUnchanged()
    {
        var persisted = new[] { MakeConversation("s1", turn: 1, isLive: false) };

        var result = SessionMerger.Merge([], persisted);

        Assert.Same(persisted, result);
    }

    [Fact]
    public void Merge_SameSession_UnionsTurnsByNumber_LiveMetricsWin()
    {
        var live = MakeConversation("s1", turn: 2, isLive: true, cost: 0.02m, request: "live-req…", response: "live-resp…");
        var persisted = MakeConversation("s1", turn: 1, isLive: false, cost: 0.01m, request: "full request", response: "full response");
        // Add turn 2 on persisted with different cost — live turn 2 must win metrics.
        persisted = persisted with
        {
            Turns =
            [
                persisted.Turns[0],
                MakeTurn(sessionId: "s1", turn: 2, isLive: false, cost: 9.99m, request: "persisted-req", response: "persisted-resp")
            ]
        };

        var merged = Assert.Single(SessionMerger.Merge([live], [persisted]));

        Assert.Equal(2, merged.Turns.Count);
        Assert.Equal(1, merged.Turns[0].TurnNumber);
        Assert.False(merged.Turns[0].IsLive);
        Assert.Equal(2, merged.Turns[1].TurnNumber);
        Assert.True(merged.Turns[1].IsLive);
        Assert.Equal(0.02m, merged.Turns[1].TotalCost);
        Assert.Equal("persisted-req", merged.Turns[1].RequestSummary);
        Assert.Equal("persisted-resp", merged.Turns[1].ResponseSummary);
        Assert.True(merged.IsLive);
        Assert.Equal(0.03m, merged.TotalCost);
    }

    [Fact]
    public void Merge_DisjointSessions_KeepsBoth()
    {
        var live = MakeConversation("live-only", turn: 1, isLive: true);
        var persisted = MakeConversation("hist-only", turn: 1, isLive: false);

        var merged = SessionMerger.Merge([live], [persisted]);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, c => c.Id == "live-only");
        Assert.Contains(merged, c => c.Id == "hist-only");
    }

    [Fact]
    public void Merge_RestartCase_Persisted1Through5_PlusLive6_GivesTurns1Through6()
    {
        var persistedTurns = Enumerable.Range(1, 5)
            .Select(n => MakeTurn("s1", n, isLive: false, cost: 0.01m * n, request: $"p-req-{n}", response: $"p-resp-{n}"))
            .ToList();
        var persisted = MakeConversationFromTurns("s1", isLive: false, persistedTurns);
        var live = MakeConversation("s1", turn: 6, isLive: true, cost: 0.06m, request: "live-6", response: "live-6-resp");

        var merged = Assert.Single(SessionMerger.Merge([live], [persisted]));

        Assert.Equal(Enumerable.Range(1, 6), merged.Turns.Select(t => t.TurnNumber));
        Assert.All(merged.Turns.Take(5), t => Assert.False(t.IsLive));
        Assert.True(merged.Turns[5].IsLive);
        Assert.Equal("live-6", merged.Turns[5].RequestSummary);
        Assert.True(merged.IsLive);
    }

    [Fact]
    public void Merge_GapCase_Persisted1And3_PlusLive2_KeepsTurn2()
    {
        var persisted = MakeConversationFromTurns("s1", isLive: false,
        [
            MakeTurn("s1", 1, isLive: false, cost: 0.01m, request: "p1", response: "r1"),
            MakeTurn("s1", 3, isLive: false, cost: 0.03m, request: "p3", response: "r3")
        ]);
        var live = MakeConversation("s1", turn: 2, isLive: true, cost: 0.02m, request: "live-2", response: "live-2-resp");

        var merged = Assert.Single(SessionMerger.Merge([live], [persisted]));

        Assert.Equal([1, 2, 3], merged.Turns.Select(t => t.TurnNumber));
        Assert.Equal("live-2", merged.Turns[1].RequestSummary);
        Assert.True(merged.Turns[1].IsLive);
        Assert.False(merged.Turns[0].IsLive);
        Assert.False(merged.Turns[2].IsLive);
    }

    [Fact]
    public void Merge_TruncatedLiveSummary_ReplacedByPersistedText()
    {
        var live = MakeConversation("s1", turn: 1, isLive: true, cost: 0.01m,
            request: "truncated live request...", response: "truncated live response…");
        var persisted = MakeConversation("s1", turn: 1, isLive: false, cost: 9.99m,
            request: "full persisted request", response: "full persisted response");

        var merged = Assert.Single(SessionMerger.Merge([live], [persisted]));
        var turn = Assert.Single(merged.Turns);

        Assert.True(turn.IsLive);
        Assert.Equal(0.01m, turn.TotalCost);
        Assert.Equal("full persisted request", turn.RequestSummary);
        Assert.Equal("full persisted response", turn.ResponseSummary);
    }

    [Fact]
    public void Merge_MissingLiveSummary_ReplacedByPersistedText()
    {
        var live = MakeConversation("s1", turn: 1, isLive: true, cost: 0.01m, request: null, response: "");
        var persisted = MakeConversation("s1", turn: 1, isLive: false, cost: 0.01m,
            request: "persisted request", response: "persisted response");

        var merged = Assert.Single(SessionMerger.Merge([live], [persisted]));
        var turn = Assert.Single(merged.Turns);

        Assert.Equal("persisted request", turn.RequestSummary);
        Assert.Equal("persisted response", turn.ResponseSummary);
    }

    [Fact]
    public void Merge_UnpricedTurns_Recounted_NullCounts_ZeroDoesNot_TotalIsFloor()
    {
        // Persisted turn 1: unknown cost (null) → unpriced.
        // Persisted turn 3: known $0 → priced, does not count as unpriced.
        // Live turn 2: known cost → priced.
        var persisted = MakeConversationFromTurns("s1", isLive: false,
        [
            MakeTurn("s1", 1, isLive: false, cost: null, request: "p1", response: "r1"),
            MakeTurn("s1", 3, isLive: false, cost: 0m, request: "p3", response: "r3")
        ]);
        var live = MakeConversation("s1", turn: 2, isLive: true, cost: 0.05m, request: "l2", response: "lr2");

        var merged = Assert.Single(SessionMerger.Merge([live], [persisted]));

        Assert.Equal(0.05m, merged.TotalCost);
        Assert.Equal(1, merged.UnpricedTurns);
        Assert.Null(merged.Turns[0].TotalCost);
        Assert.Equal(0.05m, merged.Turns[1].TotalCost);
        Assert.Equal(0m, merged.Turns[2].TotalCost);
    }

    [Fact]
    public void Merge_LiveOnly_PassesThroughUnchanged_IncludingFallback()
    {
        var live = new[]
        {
            MakeConversation("live-only", turn: 1, isLive: true, isFallback: true) with
            {
                HasFallbackTurns = true,
                IsSessionSynthesized = false
            }
        };

        var result = SessionMerger.Merge(live, []);

        Assert.Same(live, result);
        Assert.True(result[0].HasFallbackTurns);
        Assert.True(result[0].IsLive);
        Assert.False(result[0].IsSessionSynthesized);
    }

    [Fact]
    public void Merge_PersistedOnly_PassesThrough_HasFallbackTurnsFalse_SynthesizedUnknown()
    {
        // PersistedSessionMapper always writes HasFallbackTurns=false and IsSessionSynthesized=null;
        // SessionMerger leaves a one-source session unchanged, so the UI treats fallback as "—"
        // (no live turns) rather than a confident zero.
        var persisted = new[]
        {
            MakeConversation("hist-only", turn: 1, isLive: false, isFallback: true) with
            {
                HasFallbackTurns = false,
                IsSessionSynthesized = null,
                IsUsedForTraining = true
            }
        };

        var result = SessionMerger.Merge([], persisted);

        Assert.Same(persisted, result);
        Assert.False(result[0].IsLive);
        Assert.False(result[0].HasFallbackTurns);
        Assert.Null(result[0].IsSessionSynthesized);
        Assert.True(result[0].IsUsedForTraining);
    }

    [Fact]
    public void Merge_HasFallbackTurns_CountsLiveTurnsOnly()
    {
        var persisted = MakeConversationFromTurns("s1", isLive: false,
        [
            MakeTurn("s1", 1, isLive: false, cost: 0.01m, request: "p1", response: "r1", isFallback: true)
        ]);
        var liveNoFallback = MakeConversation("s1", turn: 2, isLive: true, cost: 0.02m, isFallback: false);
        var liveWithFallback = MakeConversation("s1", turn: 2, isLive: true, cost: 0.02m, isFallback: true);

        var without = Assert.Single(SessionMerger.Merge([liveNoFallback], [persisted]));
        Assert.False(without.HasFallbackTurns);

        var with = Assert.Single(SessionMerger.Merge([liveWithFallback], [persisted]));
        Assert.True(with.HasFallbackTurns);
    }

    [Fact]
    public void Merge_SessionFlagsAndTimestamps_ComeFromTheRightSide()
    {
        var persistedTurns = new[]
        {
            MakeTurn("s1", 1, isLive: false, cost: 0.01m, request: "p1", response: "r1"),
            MakeTurn("s1", 2, isLive: false, cost: 0.02m, request: "p2", response: "r2")
        };
        var persisted = MakeConversationFromTurns("s1", isLive: false, persistedTurns) with
        {
            IsUsedForTraining = true,
            IsSessionSynthesized = null
        };
        var liveTurn = MakeTurn("s1", 3, isLive: true, cost: 0.03m, request: "l3", response: "lr3");
        var live = MakeConversationFromTurns("s1", isLive: true, [liveTurn]) with
        {
            IsUsedForTraining = false,
            IsSessionSynthesized = true
        };

        var merged = Assert.Single(SessionMerger.Merge([live], [persisted]));

        Assert.True(merged.IsLive);
        Assert.True(merged.IsSessionSynthesized);
        Assert.True(merged.IsUsedForTraining);
        Assert.Equal(persistedTurns[0].Timestamp, merged.FirstTimestamp);
        Assert.Equal(liveTurn.Timestamp, merged.LastTimestamp);
        Assert.False(merged.Turns[0].IsLive);
        Assert.False(merged.Turns[1].IsLive);
        Assert.True(merged.Turns[2].IsLive);
    }

    private static Conversation MakeConversation(
        string sessionId,
        int turn,
        bool isLive,
        decimal? cost = 0.01m,
        string? request = "req",
        string? response = "resp",
        bool isFallback = false)
    {
        var t = MakeTurn(sessionId, turn, isLive, cost, request, response, isFallback);
        return MakeConversationFromTurns(sessionId, isLive, [t]);
    }

    private static Conversation MakeConversationFromTurns(
        string sessionId, bool isLive, IReadOnlyList<ConversationTurn> turns)
    {
        var ordered = turns.OrderBy(t => t.TurnNumber).ToList();
        return new Conversation(
            Id: sessionId,
            Title: $"Session {sessionId}",
            FirstTimestamp: ordered[0].Timestamp,
            LastTimestamp: ordered[^1].Timestamp,
            TotalCost: ordered.Sum(t => t.TotalCost ?? 0m),
            TotalPromptTokens: ordered.Sum(t => t.PromptTokens ?? 0),
            TotalCompletionTokens: ordered.Sum(t => t.CompletionTokens ?? 0),
            HasFallbackTurns: ordered.Any(t => t.IsLive && t.IsFallback),
            Turns: ordered,
            UnpricedTurns: ordered.Count(t => t.TotalCost is null),
            IsLive: isLive,
            IsSessionSynthesized: isLive ? false : null);
    }

    private static ConversationTurn MakeTurn(
        string sessionId,
        int turn,
        bool isLive,
        decimal? cost,
        string? request,
        string? response,
        bool isFallback = false)
    {
        var utc = new DateTimeOffset(2026, 10, 12, 12, turn, 0, TimeSpan.Zero);
        return new ConversationTurn(
            Id: $"{sessionId}-t{turn}",
            Agent: "model",
            Model: "model",
            TurnNumber: turn,
            PromptTokens: 10,
            CompletionTokens: 20,
            0m,
            TotalCost: cost,
            0,
            0m,
            0,
            0m,
            Timestamp: utc.ToString("HH:mm:ss"),
            RoutingSteps: [],
            RequestSummary: request,
            ResponseSummary: response,
            IsFallback: isFallback,
            TimestampUtc: utc,
            IsLive: isLive);
    }
}
