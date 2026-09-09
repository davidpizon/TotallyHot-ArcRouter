using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Bridges each async grader's resolved backbone model - the G-Eval judge's, and Phase Q3's
/// CodeJudge/ICE-Score/RACE portfolio's - to <see cref="GraderScoreRecordObserver"/>'s later write,
/// keyed by correlation id then grader key (docs/router/grader-reliability-plan.md, Phase Q4). Additive,
/// unlike every other pending-* cache in this namespace: up to four graders finish at different times for
/// the same request, each contributing one entry to the same correlation id's map, so <see cref="Set"/>
/// merges into the existing entry rather than replacing it. <see cref="TryTake"/> removes and returns the
/// whole accumulated map, correct because <see cref="Quality.Grading.IQualityScoreAggregator"/> guarantees
/// exactly one final write per request - by the time anything reads this cache, every grader that was going
/// to contribute already has.
/// </summary>
public sealed class PendingGraderBackboneCache
{
    private readonly int _capacity;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    /// <summary>Initializes a new instance of the <see cref="PendingGraderBackboneCache"/> class.</summary>
    /// <param name="options">Supplies the capacity and TTL, shared with <see cref="PendingResponseTextCache"/>.</param>
    /// <param name="timeProvider">
    /// Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for
    /// deterministic tests.
    /// </param>
    public PendingGraderBackboneCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _ttl = TimeSpan.FromSeconds(options.Value.CacheTtlSeconds);
        _capacity = options.Value.CacheCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the number of correlation ids currently held (test/diagnostic use).</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Records that <paramref name="graderKey"/> resolved <paramref name="backboneModel"/> for
    /// <paramref name="correlationId"/>, merging into any existing entry for the same correlation id
    /// (from another grader that already finished) rather than replacing it.
    /// </summary>
    public void Set(string correlationId, string graderKey, string backboneModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(backboneModel);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (!_entries.TryGetValue(key: correlationId, value: out var entry))
            {
                _insertionOrder.Enqueue(correlationId);
                entry = new Entry(
                    BackboneByGraderKey: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ExpiresAtUtc: _timeProvider.GetUtcNow() + _ttl);
            }

            entry.BackboneByGraderKey[graderKey] = backboneModel;
            _entries[correlationId] = entry with { ExpiresAtUtc = _timeProvider.GetUtcNow() + _ttl };

            while (_entries.Count > _capacity && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Removes and returns the accumulated grader-key-to-backbone-model map recorded under
    /// <paramref name="correlationId"/>, if present and not yet expired. Whether or not this returns
    /// <see langword="true"/>, the slot is gone afterward. An empty (never <see langword="null"/>) map is
    /// returned on a miss, so a caller need not null-check before indexing it.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out IReadOnlyDictionary<string, string> backboneByGraderKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (_entries.Remove(key: correlationId, value: out var entry))
            {
                backboneByGraderKey = entry.BackboneByGraderKey;
                return true;
            }
        }

        backboneByGraderKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>
    /// Drops expired entries and any stale queue entries left behind by a prior <see cref="TryTake"/> or
    /// capacity eviction. Must be called under <see cref="_lock"/>. Mirrors
    /// <see cref="PendingResponseTextCache.EvictExpiredAndStale"/> exactly.
    /// </summary>
    private void EvictExpiredAndStale()
    {
        var now = _timeProvider.GetUtcNow();
        var remaining = _insertionOrder.Count;
        for (var i = 0; i < remaining; i++)
        {
            var key = _insertionOrder.Dequeue();
            if (!_entries.TryGetValue(key: key, value: out var entry)) continue;

            if (entry.ExpiresAtUtc > now)
            {
                _insertionOrder.Enqueue(key);
                continue;
            }

            _entries.Remove(key);
        }
    }

    /// <summary>One correlation id's accumulated grader backbones, with the absolute time the entry expires.</summary>
    /// <param name="BackboneByGraderKey">The backbone model resolved by each grader that has finished so far.</param>
    /// <param name="ExpiresAtUtc">The UTC instant after which this entry is treated as evicted.</param>
    private sealed record Entry(Dictionary<string, string> BackboneByGraderKey, DateTimeOffset ExpiresAtUtc);
}
