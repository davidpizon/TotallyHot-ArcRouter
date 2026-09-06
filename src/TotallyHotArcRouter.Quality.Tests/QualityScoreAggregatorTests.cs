using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Quality.Scoring;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers <see cref="QualityScoreAggregator"/>, whose whole reason for existing is a counting invariant:
/// <b>exactly one observation reaches router memory per request</b>, whichever graders contributed to it.
/// Router memory keeps a running sum and count per (dimension, model) pair, so a second write would not
/// merely duplicate a number - it would inflate the sample size the voters trust, and do so invisibly.
/// Every path below therefore asserts the count, not just the value.
/// </summary>
public class QualityScoreAggregatorTests
{
    private static QualityOptions Options_(int capacity = 100, int timeoutMs = 60_000)
    {
        return new QualityOptions
        {
            JudgeJoinCapacity = capacity,
            JudgeJoinTimeoutMs = timeoutMs,
            DimensionWeights =
            {
                ["d"] = new DimensionWeightOptions { Syntax = 0.5, Analysis = 0.0, Judge = 0.5 }
            }
        };
    }

    private static QualityScoreAggregator Create(
        IQualityScoreObserver observer,
        bool willJudge,
        ManualTimeProvider? clock = null,
        QualityOptions? options = null,
        IAsyncGraderDispatcher? dispatcher = null)
    {
        var opts = options ?? Options_();
        return new QualityScoreAggregator(
            observer: observer,
            scorer: new QualityScorer(Options.Create(opts)),
            judgeAvailability: new StubJudge(willJudge),
            // Accepts every requested key by default, matching every pre-existing test's expectation that
            // a result nominally expecting a judge (StubJudge(true)) is actually held rather than written
            // immediately with a "not dispatched" reason.
            asyncGraderDispatcher: dispatcher ?? new StubDispatcher(),
            options: Options.Create(opts),
            logger: NullLogger<QualityScoreAggregator>.Instance,
            timeProvider: clock ?? new ManualTimeProvider(DateTimeOffset.UtcNow));
    }

    private static QualityResult Result(string correlationId = "corr-1", bool syntaxValid = true)
    {
        return new QualityResult
        {
            RequestCorrelationId = correlationId,
            SessionId = "sess-1",
            Dimension = "d",
            Model = "model-a",
            Language = nameof(CodeLanguage.CSharp),
            SyntaxValid = syntaxValid,
            SyntaxAuthoritative = true,
            UnifiedScore = syntaxValid ? 1.0 : 0.0
        };
    }

    [Fact]
    public async Task SubmitAsync_RejectsNullResult()
    {
        var aggregator = Create(observer: new RecordingObserver(), false);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            aggregator.SubmitAsync(result: null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_NoJudgeExpected_WritesOnceImmediately()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, false);

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        var written = Assert.Single(observer.Observed);
        Assert.Null(written.JudgeScore);
        Assert.Equal(0, actual: aggregator.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_JudgeExpected_WritesNothingUntilTheJudgeAnswers()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true);

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(observer.Observed);
        Assert.Equal(1, actual: aggregator.PendingCount);
    }

    [Fact]
    public async Task CompleteWithJudgeAsync_BlendsAndWritesExactlyOnce()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true);
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        var completed = await aggregator.CompleteWithJudgeAsync(correlationId: "corr-1", 0.0,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(completed);
        var written = Assert.Single(observer.Observed);
        Assert.Equal(0.0, actual: written.JudgeScore);

        // Syntax 1.0 at weight 0.5, judge 0.0 at weight 0.5 -> 0.5. The static score alone was 1.0, so the
        // judge's opinion demonstrably moved the number the router learns from.
        Assert.Equal(0.5, actual: written.UnifiedScore);
        Assert.Equal(0, actual: aggregator.PendingCount);
    }

    // The race this design exists to prevent: a judge grade arriving after a sweep already wrote the
    // result must be discarded, not written as a second observation.
    [Fact]
    public async Task CompleteWithJudgeAsync_AfterTheJoinClosed_WritesNothingMore()
    {
        var observer = new RecordingObserver();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var aggregator = Create(observer: observer, true, clock: clock, options: Options_(timeoutMs: 1_000));
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(2));
        await aggregator.SweepExpiredAsync(TestContext.Current.CancellationToken);
        Assert.Single(observer.Observed);

        var completed = await aggregator.CompleteWithJudgeAsync(correlationId: "corr-1", 0.9,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(completed);
        Assert.Single(observer.Observed);
    }

    [Fact]
    public async Task CompleteWithJudgeAsync_UnknownCorrelationId_WritesNothing()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true);

        Assert.False(await aggregator.CompleteWithJudgeAsync(correlationId: "never-seen", 0.9,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(observer.Observed);
    }

    [Fact]
    public async Task SweepExpiredAsync_UnexpiredEntries_AreLeftAlone()
    {
        var observer = new RecordingObserver();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var aggregator = Create(observer: observer, true, clock: clock, options: Options_(timeoutMs: 60_000));
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(5));
        var written = await aggregator.SweepExpiredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: written);
        Assert.Empty(observer.Observed);
        Assert.Equal(1, actual: aggregator.PendingCount);
    }

    [Fact]
    public async Task SweepExpiredAsync_ExpiredEntry_WritesTheStaticScoreOnceWithATimeoutReason()
    {
        var observer = new RecordingObserver();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var aggregator = Create(observer: observer, true, clock: clock, options: Options_(timeoutMs: 1_000));
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(2));
        var written = await aggregator.SweepExpiredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: written);
        var observed = Assert.Single(observer.Observed);
        Assert.Null(observed.JudgeScore);
        Assert.Equal(expected: "judge-join-timeout", actual: observed.DegradedReason);
        Assert.Equal(1.0, actual: observed.UnifiedScore);
    }

    [Fact]
    public async Task AbandonJudgeAsync_ReleasesTheHeldResultOnceWithTheGivenReason()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true);
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        var released = await aggregator.AbandonJudgeAsync(correlationId: "corr-1", reason: "judge-abstained",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(released);
        var observed = Assert.Single(observer.Observed);
        Assert.Equal(expected: "judge-abstained", actual: observed.DegradedReason);
        Assert.Null(observed.JudgeScore);
    }

    // Phase Q1: the per-grader reasons map must record the judge's own reason independently of the
    // single legacy field, which a later write could otherwise overwrite for a different cause.
    [Fact]
    public async Task AbandonJudgeAsync_StampsThePerGraderReasonAlongsideTheLegacyField()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true);
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        await aggregator.AbandonJudgeAsync(correlationId: "corr-1", reason: "judge-disabled",
            cancellationToken: TestContext.Current.CancellationToken);

        var observed = Assert.Single(observer.Observed);
        Assert.True(observed.GraderDegradedReasons.TryGetValue(key: "judge", value: out var reason));
        Assert.Equal(expected: "judge-disabled", actual: reason);
    }

    [Fact]
    public async Task SweepExpiredAsync_ExpiredEntry_StampsThePerGraderTimeoutReason()
    {
        var observer = new RecordingObserver();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var aggregator = Create(observer: observer, true, clock: clock, options: Options_(timeoutMs: 1_000));
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(2));
        await aggregator.SweepExpiredAsync(TestContext.Current.CancellationToken);

        var observed = Assert.Single(observer.Observed);
        Assert.True(observed.GraderDegradedReasons.TryGetValue(key: "judge", value: out var reason));
        Assert.Equal(expected: "judge-join-timeout", actual: reason);
    }

    [Fact]
    public async Task SubmitAsync_BeyondCapacity_StampsThePerGraderEvictedReason()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true, options: Options_(capacity: 1));

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);
        await aggregator.SubmitAsync(result: Result("corr-2"),
            cancellationToken: TestContext.Current.CancellationToken);

        var evicted = Assert.Single(observer.Observed);
        Assert.True(evicted.GraderDegradedReasons.TryGetValue(key: "judge", value: out var reason));
        Assert.Equal(expected: "judge-join-evicted", actual: reason);
    }

    [Fact]
    public async Task AbandonJudgeAsync_ThenJudgeArrives_StillOnlyOneObservation()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true);
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        await aggregator.AbandonJudgeAsync(correlationId: "corr-1", reason: "judge-abstained",
            cancellationToken: TestContext.Current.CancellationToken);
        await aggregator.CompleteWithJudgeAsync(correlationId: "corr-1", 0.9,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(observer.Observed);
    }

    // Capacity pressure must cost the judge's contribution, never the score itself: dropping outright
    // would lose signal the verifier had already computed, and would do it exactly when load is highest.
    [Fact]
    public async Task SubmitAsync_BeyondCapacity_WritesTheEvictedResultRatherThanDroppingIt()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true, options: Options_(capacity: 2));

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);
        await aggregator.SubmitAsync(result: Result("corr-2"),
            cancellationToken: TestContext.Current.CancellationToken);
        await aggregator.SubmitAsync(result: Result("corr-3"),
            cancellationToken: TestContext.Current.CancellationToken);

        var evicted = Assert.Single(observer.Observed);
        Assert.Equal(expected: "corr-1", actual: evicted.RequestCorrelationId);
        Assert.Equal(expected: "judge-join-evicted", actual: evicted.DegradedReason);
        Assert.Equal(2, actual: aggregator.PendingCount);
    }

    // Without a correlation id nothing could ever join to this result, so holding it would only guarantee
    // a timeout. It is written immediately even though a judge was nominally expected.
    [Fact]
    public async Task SubmitAsync_NoCorrelationId_WritesImmediatelyEvenWhenAJudgeIsExpected()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true);

        await aggregator.SubmitAsync(result: Result(correlationId: string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(observer.Observed);
        Assert.Equal(0, actual: aggregator.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_ObserverThrows_DoesNotEscape()
    {
        var aggregator = Create(observer: new ThrowingObserver(), false);

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);
    }

    // The bug this dispatch step exists to fix (docs/router/judge-join-deadlock-fix-plan.md): a judge that
    // is expected but never actually started must not sit held for the full join timeout. A dispatcher
    // that declines the key it was asked for is released exactly as eagerly as an explicit abandon.
    [Fact]
    public async Task SubmitAsync_DispatcherDeclines_WritesImmediatelyWithNotDispatchedReason()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true, dispatcher: new StubDispatcher(acceptAll: false));

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        var written = Assert.Single(observer.Observed);
        Assert.Null(written.JudgeScore);
        Assert.Equal(expected: "judge-not-dispatched", actual: written.DegradedReason);
        Assert.Equal(0, actual: aggregator.PendingCount);
    }

    // A throwing dispatcher must degrade exactly like an explicit decline, never escape into
    // QualityGradingService (mirroring SubmitAsync_ObserverThrows_DoesNotEscape for the write side).
    [Fact]
    public async Task SubmitAsync_DispatcherThrows_WritesImmediatelyWithNotDispatchedReason()
    {
        var observer = new RecordingObserver();
        var aggregator = Create(observer: observer, true, dispatcher: new ThrowingDispatcher());

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        var written = Assert.Single(observer.Observed);
        Assert.Equal(expected: "judge-not-dispatched", actual: written.DegradedReason);
        Assert.Equal(0, actual: aggregator.PendingCount);
    }

    // The ordering SubmitAsync depends on: the entry must already be visible in the pending table before
    // dispatch is called, so a dispatcher whose grader resolves synchronously (or races ahead of
    // DispatchAsync returning) can complete the join rather than losing the race against its own creation.
    [Fact]
    public async Task SubmitAsync_DispatchesAfterEntryIsVisible_ReentrantCompletionSucceeds()
    {
        var observer = new RecordingObserver();
        var reentrant = new ReentrantDispatcher();
        var aggregator = Create(observer: observer, true, dispatcher: reentrant);
        reentrant.Aggregator = aggregator;

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(reentrant.CompletedSynchronously);
        var written = Assert.Single(observer.Observed);
        Assert.Equal(0.4, actual: written.JudgeScore);
        Assert.Equal(0, actual: aggregator.PendingCount);
    }

    [Fact]
    public async Task ConcurrentCompletionAndSweep_StillWriteExactlyOnce()
    {
        var observer = new RecordingObserver();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var aggregator = Create(observer: observer, true, clock: clock, options: Options_(timeoutMs: 1_000));
        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));

        // Both paths race for the same held entry; only the one that wins the removal may write.
        await Task.WhenAll(
            aggregator.CompleteWithJudgeAsync(correlationId: "corr-1", 0.9,
                cancellationToken: TestContext.Current.CancellationToken),
            aggregator.SweepExpiredAsync(TestContext.Current.CancellationToken));

        Assert.Single(observer.Observed);
    }

    /// <summary>Records every result the aggregator writes, so "how many times" is directly assertable.</summary>
    private sealed class RecordingObserver : IQualityScoreObserver
    {
        public List<QualityResult> Observed { get; } = [];

        public Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            Observed.Add(result);
            return Task.CompletedTask;
        }
    }

    /// <summary>A judge-availability stub with a fixed answer.</summary>
    private sealed class StubJudge(bool willJudge) : IJudgeAvailability
    {
        public bool WillJudge(QualityResult result)
        {
            return willJudge;
        }
    }

    /// <summary>An observer that always throws, used to prove a failing observer cannot escape the aggregator.</summary>
    private sealed class ThrowingObserver : IQualityScoreObserver
    {
        public Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("observer is down");
        }
    }

    /// <summary>
    /// An <see cref="IAsyncGraderDispatcher"/> stub. Defaults to accepting every requested key (the
    /// production dispatcher's ordinary path); construct with <c>acceptAll: false</c> to decline everything.
    /// </summary>
    private sealed class StubDispatcher(bool acceptAll = true) : IAsyncGraderDispatcher
    {
        public Task<IReadOnlySet<string>> DispatchAsync(
            QualityResult result,
            IReadOnlySet<string> pendingGraderKeys,
            CancellationToken cancellationToken = default)
        {
            IReadOnlySet<string> accepted = acceptAll
                ? new HashSet<string>(pendingGraderKeys, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(accepted);
        }
    }

    /// <summary>A dispatcher that always throws, used to prove a throwing dispatcher degrades the same way a decline does.</summary>
    private sealed class ThrowingDispatcher : IAsyncGraderDispatcher
    {
        public Task<IReadOnlySet<string>> DispatchAsync(
            QualityResult result,
            IReadOnlySet<string> pendingGraderKeys,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("dispatcher is down");
        }
    }

    /// <summary>
    /// A dispatcher that calls back into <see cref="Aggregator"/>'s <see cref="QualityScoreAggregator.CompleteWithJudgeAsync"/>
    /// synchronously, before returning its own accepted set - proving <see cref="QualityScoreAggregator.SubmitAsync"/>
    /// makes the held entry visible before dispatching, since this callback would otherwise find nothing to
    /// complete.
    /// </summary>
    private sealed class ReentrantDispatcher : IAsyncGraderDispatcher
    {
        public QualityScoreAggregator? Aggregator { get; set; }

        public bool CompletedSynchronously { get; private set; }

        public async Task<IReadOnlySet<string>> DispatchAsync(
            QualityResult result,
            IReadOnlySet<string> pendingGraderKeys,
            CancellationToken cancellationToken = default)
        {
            CompletedSynchronously = Aggregator is not null && await Aggregator
                .CompleteWithJudgeAsync(correlationId: result.RequestCorrelationId, 0.4,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            return new HashSet<string>(pendingGraderKeys, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A hand-driven clock, matching <c>PendingResponseTextCacheTests</c>'s convention for the router's
    /// other TTL-bounded caches. Advancing it explicitly is what keeps the timeout tests instant rather
    /// than making them wait out a real join window.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
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