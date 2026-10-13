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

    private static Conversation MakeConversation(
        string sessionId, int turn, bool isLive, decimal? cost = 0.01m, string? request = "req", string? response = "resp")
    {
        var t = MakeTurn(sessionId, turn, isLive, cost, request, response);
        return new Conversation(
            Id: sessionId,
            Title: $"Session {sessionId}",
            FirstTimestamp: t.Timestamp,
            LastTimestamp: t.Timestamp,
            TotalCost: cost ?? 0m,
            TotalPromptTokens: t.PromptTokens ?? 0,
            TotalCompletionTokens: t.CompletionTokens ?? 0,
            HasFallbackTurns: false,
            Turns: [t],
            UnpricedTurns: cost is null ? 1 : 0,
            IsLive: isLive,
            IsSessionSynthesized: isLive ? false : null);
    }

    private static ConversationTurn MakeTurn(
        string sessionId, int turn, bool isLive, decimal? cost, string? request, string? response)
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
            TimestampUtc: utc,
            IsLive: isLive);
    }
}
