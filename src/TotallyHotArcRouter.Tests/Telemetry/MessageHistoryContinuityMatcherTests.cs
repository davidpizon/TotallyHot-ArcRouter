using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="MessageHistoryContinuityMatcher"/>: heuristic session grouping by message-array prefix matching.</summary>
public class MessageHistoryContinuityMatcherTests
{
    /// <summary>A settable-time <see cref="TimeProvider"/>, for exercising staleness eviction deterministically.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static JsonObject Message(string role, string content) => new()
    {
        ["role"] = role,
        ["content"] = content,
    };

    private static JsonArray Messages(params JsonObject[] messages) => new(messages);

    [Fact]
    public void MatchOrTrack_NullMessages_ReturnsFreshIdEachCall()
    {
        var matcher = new MessageHistoryContinuityMatcher();

        var first = matcher.MatchOrTrack(null);
        var second = matcher.MatchOrTrack(null);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MatchOrTrack_EmptyMessages_ReturnsFreshIdEachCall()
    {
        var matcher = new MessageHistoryContinuityMatcher();

        var first = matcher.MatchOrTrack([]);
        var second = matcher.MatchOrTrack([]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MatchOrTrack_ExtendedMessages_MatchesPreviousSession()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var turn1 = Messages(Message("system", "You are helpful."), Message("user", "Hello"));
        var turn2 = Messages(Message("system", "You are helpful."), Message("user", "Hello"), Message("assistant", "Hi!"), Message("user", "Follow-up"));

        var sessionId1 = matcher.MatchOrTrack(turn1);
        var sessionId2 = matcher.MatchOrTrack(turn2);

        Assert.Equal(sessionId1, sessionId2);
    }

    [Fact]
    public void MatchOrTrack_ThreeSuccessiveTurns_AllMatchSameSession()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var turn1 = Messages(Message("user", "one"));
        var turn2 = Messages(Message("user", "one"), Message("assistant", "ok"), Message("user", "two"));
        var turn3 = Messages(Message("user", "one"), Message("assistant", "ok"), Message("user", "two"), Message("assistant", "ok2"), Message("user", "three"));

        var sessionId1 = matcher.MatchOrTrack(turn1);
        var sessionId2 = matcher.MatchOrTrack(turn2);
        var sessionId3 = matcher.MatchOrTrack(turn3);

        Assert.Equal(sessionId1, sessionId2);
        Assert.Equal(sessionId1, sessionId3);
    }

    [Fact]
    public void MatchOrTrack_UnrelatedOpeningMessage_DoesNotMatch()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var conversationA = Messages(Message("user", "About topic A"));
        var conversationB = Messages(Message("user", "About topic B"));

        var sessionIdA = matcher.MatchOrTrack(conversationA);
        var sessionIdB = matcher.MatchOrTrack(conversationB);

        Assert.NotEqual(sessionIdA, sessionIdB);
    }

    [Fact]
    public void MatchOrTrack_SameLengthResend_DoesNotSelfMatch()
    {
        // A prefix must be strictly shorter than the new array - an exact resend of the same request
        // (e.g. a client retry) isn't treated as "the next turn" of itself, since there's nothing new
        // appended to confirm continuation from.
        var matcher = new MessageHistoryContinuityMatcher();
        var messages = Messages(Message("user", "hello"));

        var sessionId1 = matcher.MatchOrTrack(messages);
        var sessionId2 = matcher.MatchOrTrack(messages);

        Assert.NotEqual(sessionId1, sessionId2);
    }

    [Fact]
    public void MatchOrTrack_ShorterFollowUp_DoesNotMatchLongerTrackedConversation()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var longer = Messages(Message("user", "one"), Message("assistant", "ok"), Message("user", "two"));
        var shorter = Messages(Message("user", "one"));

        var sessionIdLonger = matcher.MatchOrTrack(longer);
        var sessionIdShorter = matcher.MatchOrTrack(shorter);

        Assert.NotEqual(sessionIdLonger, sessionIdShorter);
    }

    [Fact]
    public void MatchOrTrack_SeveralUnrelatedTrackedConversations_MatchesTheCorrectOne()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var conversationA = Messages(Message("user", "topic A"));
        var conversationB = Messages(Message("user", "topic B"));
        var conversationC = Messages(Message("user", "topic C"));
        var continuesB = Messages(Message("user", "topic B"), Message("assistant", "ok"), Message("user", "follow-up"));

        var sessionIdA = matcher.MatchOrTrack(conversationA);
        var sessionIdB = matcher.MatchOrTrack(conversationB);
        var sessionIdC = matcher.MatchOrTrack(conversationC);
        var sessionIdContinuesB = matcher.MatchOrTrack(continuesB);

        Assert.Equal(sessionIdB, sessionIdContinuesB);
        Assert.NotEqual(sessionIdA, sessionIdContinuesB);
        Assert.NotEqual(sessionIdC, sessionIdContinuesB);
    }

    [Fact]
    public void MatchOrTrack_WithinStalenessWindow_StillMatches()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var matcher = new MessageHistoryContinuityMatcher(timeProvider);
        var turn1 = Messages(Message("user", "one"));
        var turn2 = Messages(Message("user", "one"), Message("assistant", "ok"), Message("user", "two"));

        var sessionId1 = matcher.MatchOrTrack(turn1);
        timeProvider.UtcNow += TimeSpan.FromMinutes(29);
        var sessionId2 = matcher.MatchOrTrack(turn2);

        Assert.Equal(sessionId1, sessionId2);
    }

    [Fact]
    public void MatchOrTrack_PastStalenessWindow_NoLongerMatches()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var matcher = new MessageHistoryContinuityMatcher(timeProvider);
        var turn1 = Messages(Message("user", "one"));
        var turn2 = Messages(Message("user", "one"), Message("assistant", "ok"), Message("user", "two"));

        var sessionId1 = matcher.MatchOrTrack(turn1);
        timeProvider.UtcNow += TimeSpan.FromMinutes(31);
        var sessionId2 = matcher.MatchOrTrack(turn2);

        Assert.NotEqual(sessionId1, sessionId2);
    }
}

