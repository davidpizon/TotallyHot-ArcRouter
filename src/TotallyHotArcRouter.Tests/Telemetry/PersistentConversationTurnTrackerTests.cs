using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="PersistentConversationTurnTracker"/>.</summary>
public class PersistentConversationTurnTrackerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NextTurn_NullOrWhitespaceSessionId_Throws(string? sessionId)
    {
        var tracker = new PersistentConversationTurnTracker(new StubLedger());

        Assert.Throws<ArgumentException>(() => tracker.NextTurn(sessionId!));
    }

    [Fact]
    public void NextTurn_FirstCallForUnseenSession_SeedsFromLedgerAndReturnsNextTurn()
    {
        var ledger = new StubLedger();
        ledger.MaxTurnBySession["sess-1"] = 4; // simulates a restart mid-conversation

        var tracker = new PersistentConversationTurnTracker(ledger);

        Assert.Equal(5, tracker.NextTurn("sess-1"));
        Assert.Equal(6, tracker.NextTurn("sess-1"));
    }

    [Fact]
    public void NextTurn_NoLedgerHistory_FirstCallReturnsOne()
    {
        var tracker = new PersistentConversationTurnTracker(new StubLedger());

        Assert.Equal(1, tracker.NextTurn("sess-fresh"));
    }

    [Fact]
    public void NextTurn_AfterFirstSeed_DoesNotReconsultLedger()
    {
        var ledger = new StubLedger();
        ledger.MaxTurnBySession["sess-1"] = 4;
        var tracker = new PersistentConversationTurnTracker(ledger);

        tracker.NextTurn("sess-1"); // seeds from ledger (5), one call recorded
        ledger.MaxTurnBySession["sess-1"] = 999; // a later restart would seed differently
        var next = tracker.NextTurn("sess-1"); // must count in memory, not reconsult

        Assert.Equal(6, next);
        Assert.Equal(1, ledger.CallCount);
    }

    [Fact]
    public void NextTurn_DifferentSessions_HaveIndependentCounters()
    {
        var tracker = new PersistentConversationTurnTracker(new StubLedger());

        Assert.Equal(1, tracker.NextTurn("sess-a"));
        Assert.Equal(1, tracker.NextTurn("sess-b"));
        Assert.Equal(2, tracker.NextTurn("sess-a"));
        Assert.Equal(2, tracker.NextTurn("sess-b"));
    }

    [Fact]
    public void NextTurn_SessionEvictedAfterIdlePeriod_ReSeedsFromLedgerOnResume()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new StubLedger();
        var tracker = new PersistentConversationTurnTracker(ledger, clock);

        Assert.Equal(1, tracker.NextTurn("sess-1"));

        // The tracker recorded turn 1 in memory. Advance the ledger independently (as if a different
        // process instance recorded turns 2 and 3 for this session while this tracker's entry went idle)
        // and jump the clock past the idle-eviction window.
        ledger.MaxTurnBySession["sess-1"] = 3;
        clock.Advance(TimeSpan.FromHours(13));

        var resumed = tracker.NextTurn("sess-1");

        Assert.Equal(4, resumed);
    }

    [Fact]
    public void NextTurn_SessionActiveWithinIdlePeriod_IsNotEvicted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new StubLedger();
        var tracker = new PersistentConversationTurnTracker(ledger, clock);

        Assert.Equal(1, tracker.NextTurn("sess-1"));

        ledger.MaxTurnBySession["sess-1"] = 999; // would change the outcome if re-seeded
        clock.Advance(TimeSpan.FromHours(11));

        Assert.Equal(2, tracker.NextTurn("sess-1"));
    }

    [Fact]
    public async Task NextTurn_ConcurrentCallsForSameSession_ProduceAGaplessUniqueSequence()
    {
        var tracker = new PersistentConversationTurnTracker(new StubLedger());
        const int callCount = 200;

        var tasks = Enumerable.Range(0, callCount)
            .Select(_ => Task.Run(() => tracker.NextTurn("sess-concurrent")))
            .ToArray();

        await Task.WhenAll(tasks);

        var results = tasks.Select(t => t.Result).OrderBy(v => v).ToArray();
        var expected = Enumerable.Range(1, callCount).ToArray();
        Assert.Equal(expected, results);
    }

    private sealed class StubLedger : IUsageLedger
    {
        public Dictionary<string, int> MaxTurnBySession { get; } = new(StringComparer.Ordinal);

        public int CallCount { get; private set; }

        public Task RecordAsync(UsageLedgerEntry entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by the turn tracker.");

        public int GetMaxTurnNumber(string sessionId)
        {
            CallCount++;
            return MaxTurnBySession.GetValueOrDefault(sessionId, 0);
        }

        public int DeleteOlderThan(DateTimeOffset cutoffUtc) =>
            throw new NotSupportedException("Not exercised by the turn tracker.");

        public decimal SumEstimatedCostUsd(string provider, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
            throw new NotSupportedException("Not exercised by the turn tracker.");
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
