using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Bridges a request's response character length - never the response text itself - to the
/// later-arriving <see cref="GraderScoreRecordObserver"/> write, keyed by the same correlation id
/// <see cref="PendingResponseTextCache"/> uses (docs/router/grader-reliability-plan.md, Phase Q4). Recording
/// a length instead of extending <see cref="PendingResponseTextCache"/>'s own retention keeps "response
/// text is ephemeral until judged" exactly as tight as it already is: this cache never holds anything that
/// could be used to reconstruct the response.
/// </summary>
/// <remarks>
/// Mirrors <see cref="PendingResponseTextCache"/>'s TTL/capacity shape and <see cref="TryTake"/>/no-peek
/// choice: unlike the response text (read by up to four independent graders), the length is only ever
/// consumed once, by the single <see cref="GraderScoreRecordObserver.ObserveAsync"/> call for a given
/// correlation id, so a removing take is correct here.
/// </remarks>
public sealed class PendingResponseLengthCache
{
    private readonly int _capacity;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    /// <summary>Initializes a new instance of the <see cref="PendingResponseLengthCache"/> class.</summary>
    /// <param name="options">Supplies the capacity and TTL, shared with <see cref="PendingResponseTextCache"/>.</param>
    /// <param name="timeProvider">
    /// Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for
    /// deterministic tests.
    /// </param>
    public PendingResponseLengthCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _ttl = TimeSpan.FromSeconds(options.Value.CacheTtlSeconds);
        _capacity = options.Value.CacheCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the number of entries currently held (test/diagnostic use).</summary>
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
    /// Records <paramref name="length"/> under <paramref name="correlationId"/>, evicting expired entries
    /// and then the oldest entries beyond capacity.
    /// </summary>
    public void Set(string correlationId, int length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (!_entries.ContainsKey(correlationId)) _insertionOrder.Enqueue(correlationId);

            _entries[correlationId] = new Entry(Length: length, ExpiresAtUtc: _timeProvider.GetUtcNow() + _ttl);

            while (_entries.Count > _capacity && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Removes and returns the response length recorded under <paramref name="correlationId"/>, if present
    /// and not yet expired. Whether or not this returns <see langword="true"/>, the slot is gone afterward.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out int length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (_entries.Remove(key: correlationId, value: out var entry))
            {
                length = entry.Length;
                return true;
            }
        }

        length = 0;
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

    /// <summary>A single cached response length awaiting the grader-score write, with the absolute time it expires.</summary>
    /// <param name="Length">The response's character length.</param>
    /// <param name="ExpiresAtUtc">The UTC instant after which this entry is treated as evicted.</param>
    private sealed record Entry(int Length, DateTimeOffset ExpiresAtUtc);
}
