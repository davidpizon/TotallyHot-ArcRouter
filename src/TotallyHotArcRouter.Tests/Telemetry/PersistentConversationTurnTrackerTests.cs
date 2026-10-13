using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="PersistentConversationTurnTracker"/>, including the #176 §5.7 transcript seed.</summary>
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

        Assert.Equal(5, actual: tracker.NextTurn("sess-1"));
        Assert.Equal(6, actual: tracker.NextTurn("sess-1"));
    }

    [Fact]
    public void NextTurn_NoLedgerHistory_FirstCallReturnsOne()
    {
        var tracker = new PersistentConversationTurnTracker(new StubLedger());

        Assert.Equal(1, actual: tracker.NextTurn("sess-fresh"));
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

        Assert.Equal(6, actual: next);
        Assert.Equal(1, actual: ledger.CallCount);
    }

    [Fact]
    public void NextTurn_DifferentSessions_HaveIndependentCounters()
    {
        var tracker = new PersistentConversationTurnTracker(new StubLedger());

        Assert.Equal(1, actual: tracker.NextTurn("sess-a"));
        Assert.Equal(1, actual: tracker.NextTurn("sess-b"));
        Assert.Equal(2, actual: tracker.NextTurn("sess-a"));
        Assert.Equal(2, actual: tracker.NextTurn("sess-b"));
    }

    [Fact]
    public void NextTurn_SessionEvictedAfterIdlePeriod_ReSeedsFromLedgerOnResume()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new StubLedger();
        var tracker = new PersistentConversationTurnTracker(ledger: ledger, timeProvider: clock);

        Assert.Equal(1, actual: tracker.NextTurn("sess-1"));

        // The tracker recorded turn 1 in memory. Advance the ledger independently (as if a different
        // process instance recorded turns 2 and 3 for this session while this tracker's entry went idle)
        // and jump the clock past the idle-eviction window.
        ledger.MaxTurnBySession["sess-1"] = 3;
        clock.Advance(TimeSpan.FromHours(13));

        var resumed = tracker.NextTurn("sess-1");

        Assert.Equal(4, actual: resumed);
    }

    [Fact]
    public void NextTurn_SessionActiveWithinIdlePeriod_IsNotEvicted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new StubLedger();
        var tracker = new PersistentConversationTurnTracker(ledger: ledger, timeProvider: clock);

        Assert.Equal(1, actual: tracker.NextTurn("sess-1"));

        ledger.MaxTurnBySession["sess-1"] = 999; // would change the outcome if re-seeded
        clock.Advance(TimeSpan.FromHours(11));

        Assert.Equal(2, actual: tracker.NextTurn("sess-1"));
    }

    [Fact]
    public async Task NextTurn_ConcurrentCallsForSameSession_ProduceAGaplessUniqueSequence()
    {
        var tracker = new PersistentConversationTurnTracker(new StubLedger());
        const int callCount = 200;

        var tasks = Enumerable.Range(0, count: callCount)
            .Select(_ => Task.Run(() => tracker.NextTurn("sess-concurrent")))
            .ToArray();

        await Task.WhenAll(tasks);

        var results = tasks.Select(t => t.Result).OrderBy(v => v).ToArray();
        var expected = Enumerable.Range(1, count: callCount).ToArray();
        Assert.Equal(expected: expected, actual: results);
    }

    [Fact]
    public void NextTurn_TranscriptAheadOfLedger_SeedsFromTranscript()
    {
        // Restart case: ledger max 4, transcript for turn 5 with no ledger row → first call returns 6.
        var ledger = new StubLedger();
        ledger.MaxTurnBySession["sess-1"] = 4;
        var transcripts = new StubTranscriptStore();
        transcripts.MaxTurnBySession["sess-1"] = 5;

        var tracker = new PersistentConversationTurnTracker(ledger: ledger, transcripts: transcripts);

        Assert.Equal(6, actual: tracker.NextTurn("sess-1"));
    }

    [Fact]
    public void NextTurn_LedgerAheadOfTranscript_SeedsFromLedger()
    {
        var ledger = new StubLedger();
        ledger.MaxTurnBySession["sess-1"] = 7;
        var transcripts = new StubTranscriptStore();
        transcripts.MaxTurnBySession["sess-1"] = 3;

        var tracker = new PersistentConversationTurnTracker(ledger: ledger, transcripts: transcripts);

        Assert.Equal(8, actual: tracker.NextTurn("sess-1"));
    }

    [Fact]
    public void NextTurn_NullTranscriptStore_SeedsFromLedgerAlone()
    {
        var ledger = new StubLedger();
        ledger.MaxTurnBySession["sess-1"] = 4;

        var tracker = new PersistentConversationTurnTracker(ledger: ledger, transcripts: null);

        Assert.Equal(5, actual: tracker.NextTurn("sess-1"));
    }

    [Fact]
    public void NextTurn_TranscriptStoreReturningZero_SeedsFromLedgerAlone()
    {
        var ledger = new StubLedger();
        ledger.MaxTurnBySession["sess-1"] = 4;
        var transcripts = new StubTranscriptStore(); // empty → 0

        var tracker = new PersistentConversationTurnTracker(ledger: ledger, transcripts: transcripts);

        Assert.Equal(5, actual: tracker.NextTurn("sess-1"));
    }

    private sealed class StubLedger : IUsageLedger
    {
        public Dictionary<string, int> MaxTurnBySession { get; } = new(StringComparer.Ordinal);

        public int CallCount { get; private set; }

        public Task RecordAsync(UsageLedgerEntry entry, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Not exercised by the turn tracker.");
        }

        public int GetMaxTurnNumber(string sessionId)
        {
            CallCount++;
            return MaxTurnBySession.GetValueOrDefault(key: sessionId, 0);
        }

        public int DeleteOlderThan(DateTimeOffset cutoffUtc)
        {
            throw new NotSupportedException("Not exercised by the turn tracker.");
        }

        public decimal SumEstimatedCostUsd(string provider, DateTimeOffset fromUtc, DateTimeOffset toUtc)
        {
            throw new NotSupportedException("Not exercised by the turn tracker.");
        }
    }

    /// <summary>Minimal <see cref="ITranscriptStore"/> that only implements <see cref="ITranscriptStore.GetMaxTurnNumber"/>.</summary>
    private sealed class StubTranscriptStore : ITranscriptStore
    {
        public Dictionary<string, int> MaxTurnBySession { get; } = new(StringComparer.Ordinal);

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateOutcomeAsync(string correlationId, double? score, bool isJudgeScored = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int GetMaxTurnNumber(string sessionId) =>
            MaxTurnBySession.GetValueOrDefault(key: sessionId, 0);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
        }
    }
}