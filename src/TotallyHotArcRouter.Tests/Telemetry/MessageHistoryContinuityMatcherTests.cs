using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="MessageHistoryContinuityMatcher"/>: heuristic session grouping by message-array prefix
/// matching.
/// </summary>
public class MessageHistoryContinuityMatcherTests
{
    private static JsonObject Message(string role, string content)
    {
        return new JsonObject
        {
            ["role"] = role,
            ["content"] = content
        };
    }

    private static JsonArray Messages(params JsonObject[] messages)
    {
        return [with(messages)];
    }

    [Fact]
    public void MatchOrTrack_NullMessages_ReturnsFreshIdEachCall()
    {
        var matcher = new MessageHistoryContinuityMatcher();

        var first = matcher.MatchOrTrack(null);
        var second = matcher.MatchOrTrack(null);

        Assert.NotEqual(expected: first, actual: second);
    }

    [Fact]
    public void MatchOrTrack_EmptyMessages_ReturnsFreshIdEachCall()
    {
        var matcher = new MessageHistoryContinuityMatcher();

        var first = matcher.MatchOrTrack([]);
        var second = matcher.MatchOrTrack([]);

        Assert.NotEqual(expected: first, actual: second);
    }

    [Fact]
    public void MatchOrTrack_ExtendedMessages_MatchesPreviousSession()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var turn1 = Messages(Message(role: "system", content: "You are helpful."),
            Message(role: "user", content: "Hello"));
        var turn2 = Messages(Message(role: "system", content: "You are helpful."),
            Message(role: "user", content: "Hello"), Message(role: "assistant", content: "Hi!"),
            Message(role: "user", content: "Follow-up"));

        var sessionId1 = matcher.MatchOrTrack(turn1);
        var sessionId2 = matcher.MatchOrTrack(turn2);

        Assert.Equal(expected: sessionId1, actual: sessionId2);
    }

    [Fact]
    public void MatchOrTrack_ThreeSuccessiveTurns_AllMatchSameSession()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var turn1 = Messages(Message(role: "user", content: "one"));
        var turn2 = Messages(Message(role: "user", content: "one"), Message(role: "assistant", content: "ok"),
            Message(role: "user", content: "two"));
        var turn3 = Messages(Message(role: "user", content: "one"), Message(role: "assistant", content: "ok"),
            Message(role: "user", content: "two"), Message(role: "assistant", content: "ok2"),
            Message(role: "user", content: "three"));

        var sessionId1 = matcher.MatchOrTrack(turn1);
        var sessionId2 = matcher.MatchOrTrack(turn2);
        var sessionId3 = matcher.MatchOrTrack(turn3);

        Assert.Equal(expected: sessionId1, actual: sessionId2);
        Assert.Equal(expected: sessionId1, actual: sessionId3);
    }

    [Fact]
    public void MatchOrTrack_UnrelatedOpeningMessage_DoesNotMatch()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var conversationA = Messages(Message(role: "user", content: "About topic A"));
        var conversationB = Messages(Message(role: "user", content: "About topic B"));

        var sessionIdA = matcher.MatchOrTrack(conversationA);
        var sessionIdB = matcher.MatchOrTrack(conversationB);

        Assert.NotEqual(expected: sessionIdA, actual: sessionIdB);
    }

    [Fact]
    public void MatchOrTrack_SameLengthResend_DoesNotSelfMatch()
    {
        // A prefix must be strictly shorter than the new array - an exact resend of the same request
        // (e.g. a client retry) isn't treated as "the next turn" of itself, since there's nothing new
        // appended to confirm continuation from.
        var matcher = new MessageHistoryContinuityMatcher();
        var messages = Messages(Message(role: "user", content: "hello"));

        var sessionId1 = matcher.MatchOrTrack(messages);
        var sessionId2 = matcher.MatchOrTrack(messages);

        Assert.NotEqual(expected: sessionId1, actual: sessionId2);
    }

    [Fact]
    public void MatchOrTrack_ShorterFollowUp_DoesNotMatchLongerTrackedConversation()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var longer = Messages(Message(role: "user", content: "one"), Message(role: "assistant", content: "ok"),
            Message(role: "user", content: "two"));
        var shorter = Messages(Message(role: "user", content: "one"));

        var sessionIdLonger = matcher.MatchOrTrack(longer);
        var sessionIdShorter = matcher.MatchOrTrack(shorter);

        Assert.NotEqual(expected: sessionIdLonger, actual: sessionIdShorter);
    }

    [Fact]
    public void MatchOrTrack_SeveralUnrelatedTrackedConversations_MatchesTheCorrectOne()
    {
        var matcher = new MessageHistoryContinuityMatcher();
        var conversationA = Messages(Message(role: "user", content: "topic A"));
        var conversationB = Messages(Message(role: "user", content: "topic B"));
        var conversationC = Messages(Message(role: "user", content: "topic C"));
        var continuesB = Messages(Message(role: "user", content: "topic B"), Message(role: "assistant", content: "ok"),
            Message(role: "user", content: "follow-up"));

        var sessionIdA = matcher.MatchOrTrack(conversationA);
        var sessionIdB = matcher.MatchOrTrack(conversationB);
        var sessionIdC = matcher.MatchOrTrack(conversationC);
        var sessionIdContinuesB = matcher.MatchOrTrack(continuesB);

        Assert.Equal(expected: sessionIdB, actual: sessionIdContinuesB);
        Assert.NotEqual(expected: sessionIdA, actual: sessionIdContinuesB);
        Assert.NotEqual(expected: sessionIdC, actual: sessionIdContinuesB);
    }

    [Fact]
    public void MatchOrTrack_WithinStalenessWindow_StillMatches()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var matcher = new MessageHistoryContinuityMatcher(timeProvider);
        var turn1 = Messages(Message(role: "user", content: "one"));
        var turn2 = Messages(Message(role: "user", content: "one"), Message(role: "assistant", content: "ok"),
            Message(role: "user", content: "two"));

        var sessionId1 = matcher.MatchOrTrack(turn1);
        timeProvider.UtcNow += TimeSpan.FromMinutes(29);
        var sessionId2 = matcher.MatchOrTrack(turn2);

        Assert.Equal(expected: sessionId1, actual: sessionId2);
    }

    [Fact]
    public void MatchOrTrack_PastStalenessWindow_NoLongerMatches()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var matcher = new MessageHistoryContinuityMatcher(timeProvider);
        var turn1 = Messages(Message(role: "user", content: "one"));
        var turn2 = Messages(Message(role: "user", content: "one"), Message(role: "assistant", content: "ok"),
            Message(role: "user", content: "two"));

        var sessionId1 = matcher.MatchOrTrack(turn1);
        timeProvider.UtcNow += TimeSpan.FromMinutes(31);
        var sessionId2 = matcher.MatchOrTrack(turn2);

        Assert.NotEqual(expected: sessionId1, actual: sessionId2);
    }

    /// <summary>A settable-time <see cref="TimeProvider"/>, for exercising staleness eviction deterministically.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }
}