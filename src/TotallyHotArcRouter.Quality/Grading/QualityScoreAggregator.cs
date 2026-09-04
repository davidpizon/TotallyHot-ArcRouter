using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// The default <see cref="IQualityScoreAggregator"/>. Holds static verdicts in a bounded, TTL-evicting
/// table keyed by correlation id - the same Dictionary-plus-Queue-plus-<see cref="TimeProvider"/> shape
/// the router's other pending caches use - and writes each result exactly once, whether it was completed
/// by the judge, aged out, or pushed out by capacity.
/// </summary>
/// <remarks>
/// <b>Exactly-once is enforced by removal, not by a flag.</b> Every path that writes must first win the
/// race to remove the entry under the lock; the loser observes an empty slot and does nothing. That makes
/// a double write structurally impossible rather than merely unlikely, which matters because the failure
/// it prevents - a silently double-counted observation - is invisible in the resulting average.
/// <para>
/// A capacity eviction still <em>writes</em> the static score rather than dropping it. Dropping would lose
/// signal the verifier had already computed, and would do so precisely under load, when the router most
/// needs evidence. Only the judge's contribution is forfeited.
/// </para>
/// </remarks>
public sealed class QualityScoreAggregator : IQualityScoreAggregator
{
    private readonly int _capacity;
    private readonly Queue<string> _insertionOrder = new();
    private readonly TimeSpan _joinTimeout;
    private readonly IJudgeAvailability _judgeAvailability;

    /// <summary>
    /// Guards <see cref="_pending"/> and <see cref="_insertionOrder"/>. All five public methods
    /// (<see cref="SubmitAsync"/>, <see cref="CompleteWithJudgeAsync"/>, <see cref="AbandonJudgeAsync"/>,
    /// <see cref="SweepExpiredAsync"/>, and the <see cref="PendingCount"/> diagnostic) currently serialize
    /// through this single lock. That is a deliberate simplicity choice, not an oversight: the pending
    /// table is small (bounded by <see cref="_capacity"/>) and contention is expected to be low, so a
    /// single lock is easier to reason about correctly than finer-grained locking. Revisit only if
    /// judge-scoring volume grows enough to make this lock a measurable bottleneck.
    /// </summary>
    private readonly Lock _lock = new();

    private readonly ILogger<QualityScoreAggregator> _logger;

    private readonly IQualityScoreObserver _observer;

    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    private readonly IQualityScorer _scorer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="QualityScoreAggregator"/> class.</summary>
    /// <param name="observer">The observer that receives the single score per request.</param>
    /// <param name="scorer">The scorer, re-run once the judge axis is filled.</param>
    /// <param name="judgeAvailability">Decides whether a result is held for a judge grade.</param>
    /// <param name="options">Supplies the join timeout and held-result capacity.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">
    /// Clock used for expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for
    /// deterministic tests.
    /// </param>
    public QualityScoreAggregator(
        IQualityScoreObserver observer,
        IQualityScorer scorer,
        IJudgeAvailability judgeAvailability,
        IOptions<QualityOptions> options,
        ILogger<QualityScoreAggregator> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentNullException.ThrowIfNull(judgeAvailability);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _observer = observer;
        _scorer = scorer;
        _judgeAvailability = judgeAvailability;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _joinTimeout = TimeSpan.FromMilliseconds(options.Value.JudgeJoinTimeoutMs);
        _capacity = options.Value.JudgeJoinCapacity;
    }

    /// <summary>Gets the number of results currently held awaiting a judge grade (test/diagnostic use).</summary>
    internal int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <inheritdoc/>
    public async Task SubmitAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        // No correlation id means nothing could ever join to it, so holding it would only guarantee a
        // timeout later. Write it now.
        if (string.IsNullOrEmpty(result.RequestCorrelationId) || !_judgeAvailability.WillJudge(result))
        {
            await WriteAsync(result: result, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        List<QualityResult> evicted;
        lock (_lock)
        {
            if (!_pending.ContainsKey(result.RequestCorrelationId))
                _insertionOrder.Enqueue(result.RequestCorrelationId);

            _pending[result.RequestCorrelationId] =
                new Entry(Result: result, ExpiresAtUtc: _timeProvider.GetUtcNow() + _joinTimeout);
            evicted = TrimToCapacityLocked();
        }

        foreach (var stale in evicted)
        {
            _logger.LogDebug(
                message:
                "Judge join table at capacity; writing the static score for correlation {CorrelationId} without a judge grade.",
                stale.RequestCorrelationId);
            await WriteAsync(result: stale with { DegradedReason = "judge-join-evicted" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CompleteWithJudgeAsync(
        string correlationId,
        double judgeScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        QualityResult? held;
        lock (_lock)
        {
            held = TakeLocked(correlationId);
        }

        if (held is null)
        {
            // Already written by a sweep or an eviction. Not an error: the judge simply lost the race, and
            // the router already has an observation for this request.
            _logger.LogDebug(
                message: "Judge grade for correlation {CorrelationId} arrived after the join closed; discarding it.",
                correlationId);
            return false;
        }

        var blended = held with { JudgeScore = Math.Clamp(value: judgeScore, 0.0, 1.0) };
        blended = blended with { UnifiedScore = _scorer.Score(result: blended, dimension: blended.Dimension) };

        await WriteAsync(result: blended, cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> AbandonJudgeAsync(
        string correlationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        QualityResult? held;
        lock (_lock)
        {
            held = TakeLocked(correlationId);
        }

        if (held is null) return false;

        _logger.LogDebug(
            message: "Releasing correlation {CorrelationId} with its static score alone: {Reason}.",
            correlationId,
            reason);

        await WriteAsync(result: held with { DegradedReason = reason }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        List<QualityResult> expired;
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            expired = [];
            foreach (var key in _pending.Where(kv => kv.Value.ExpiresAtUtc <= now).Select(kv => kv.Key).ToList())
                if (TakeLocked(key) is { } result)
                    expired.Add(result);
        }

        foreach (var result in expired)
        {
            _logger.LogDebug(
                message:
                "Judge grade for correlation {CorrelationId} did not arrive within the join window; writing the static score alone.",
                result.RequestCorrelationId);
            await WriteAsync(result: result with { DegradedReason = "judge-join-timeout" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }

    /// <summary>Removes an entry and returns its result, or null when nothing was held under that key.</summary>
    /// <remarks>Must be called under <c>_lock</c>: winning this removal is what grants the right to write.</remarks>
    private QualityResult? TakeLocked(string correlationId)
    {
        if (!_pending.Remove(key: correlationId, value: out var entry)) return null;

        return entry.Result;
    }

    /// <summary>Drops the oldest held results beyond capacity and returns them so the caller can still write them.</summary>
    /// <remarks>Must be called under <c>_lock</c>.</remarks>
    private List<QualityResult> TrimToCapacityLocked()
    {
        var evicted = new List<QualityResult>();

        while (_pending.Count > _capacity && _insertionOrder.Count > 0)
        {
            var oldest = _insertionOrder.Dequeue();
            if (TakeLocked(oldest) is { } result) evicted.Add(result);
        }

        return evicted;
    }

    /// <summary>Hands one final result to the observer, never letting an observer failure escape.</summary>
    private async Task WriteAsync(QualityResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _observer.ObserveAsync(result: result, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception: ex,
                message:
                "Observing the quality score for correlation {CorrelationId} failed; the routed response was unaffected.",
                result.RequestCorrelationId);
        }
    }

    /// <summary>One held static result and the instant its judge wait expires.</summary>
    /// <param name="Result">The static result awaiting a judge grade.</param>
    /// <param name="ExpiresAtUtc">The UTC instant after which the wait is abandoned.</param>
    private sealed record Entry(QualityResult Result, DateTimeOffset ExpiresAtUtc);
}