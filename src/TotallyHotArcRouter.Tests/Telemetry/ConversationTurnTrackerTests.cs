using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="ConversationTurnTracker"/>.</summary>
public class ConversationTurnTrackerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NextTurn_NullOrWhitespaceSessionId_Throws(string? sessionId)
    {
        var tracker = new ConversationTurnTracker();

        Assert.Throws<ArgumentException>(() => tracker.NextTurn(sessionId!));
    }

    [Fact]
    public void NextTurn_FirstCallForSession_ReturnsOne()
    {
        var tracker = new ConversationTurnTracker();

        var turn = tracker.NextTurn("sess-1");

        Assert.Equal(1, actual: turn);
    }

    [Fact]
    public void NextTurn_SubsequentCallsForSameSession_IncrementSequentially()
    {
        var tracker = new ConversationTurnTracker();

        Assert.Equal(1, actual: tracker.NextTurn("sess-1"));
        Assert.Equal(2, actual: tracker.NextTurn("sess-1"));
        Assert.Equal(3, actual: tracker.NextTurn("sess-1"));
    }

    [Fact]
    public void NextTurn_DifferentSessions_HaveIndependentCounters()
    {
        var tracker = new ConversationTurnTracker();

        Assert.Equal(1, actual: tracker.NextTurn("sess-a"));
        Assert.Equal(1, actual: tracker.NextTurn("sess-b"));
        Assert.Equal(2, actual: tracker.NextTurn("sess-a"));
        Assert.Equal(2, actual: tracker.NextTurn("sess-b"));
    }

    [Fact]
    public async Task NextTurn_ConcurrentCallsForSameSession_ProduceAGaplessUniqueSequence()
    {
        var tracker = new ConversationTurnTracker();
        const int callCount = 200;

        var tasks = Enumerable.Range(0, count: callCount)
            .Select(_ => Task.Run(() => tracker.NextTurn("sess-concurrent")))
            .ToArray();

        await Task.WhenAll(tasks);

        var results = tasks.Select(t => t.Result).OrderBy(v => v).ToArray();
        var expected = Enumerable.Range(1, count: callCount).ToArray();
        Assert.Equal(expected: expected, actual: results);
    }
}