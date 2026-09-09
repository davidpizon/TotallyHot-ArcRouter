using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Bridges a request's prompt text - already extracted on the hot path for
/// <see cref="Quality.Ingress.QualityIngestContext"/> - to every later-arriving background grading job (the
/// G-Eval shadow judge, and Phase Q3's CodeJudge/ICE-Score/RACE portfolio), keyed by the same correlation id
/// <see cref="PendingResponseTextCache"/> uses. Mirrors <see cref="PendingResponseTextCache"/>'s design
/// exactly, including its Phase Q3 move from single-take to multi-read (see that type's remarks) - down to
/// sharing its <see cref="JudgeOptions"/> bounds, since a prompt is no larger a retention concern than the
/// response it produced.
/// </summary>
public sealed class PendingPromptCache
{
    private readonly int _capacity;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();
    private readonly int _maxTextChars;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    /// <summary>Initializes a new instance of the <see cref="PendingPromptCache"/> class.</summary>
    /// <param name="options">Supplies the capacity, TTL, and per-entry text-size bounds - the same ones <see cref="PendingResponseTextCache"/> uses.</param>
    /// <param name="timeProvider">
    /// Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for
    /// deterministic tests.
    /// </param>
    public PendingPromptCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _ttl = TimeSpan.FromSeconds(options.Value.CacheTtlSeconds);
        _capacity = options.Value.CacheCapacity;
        _maxTextChars = options.Value.MaxCachedTextChars;
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
    /// Records <paramref name="prompt"/> under <paramref name="correlationId"/>, truncating to the
    /// configured per-entry character cap first, then evicting expired entries and finally the oldest
    /// entries beyond capacity.
    /// </summary>
    public void Set(string correlationId, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(prompt);

        var bounded = prompt.Length > _maxTextChars ? prompt[.._maxTextChars] : prompt;

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (!_entries.ContainsKey(correlationId)) _insertionOrder.Enqueue(correlationId);

            _entries[correlationId] = new Entry(Text: bounded, ExpiresAtUtc: _timeProvider.GetUtcNow() + _ttl);

            while (_entries.Count > _capacity && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Removes and returns the prompt text recorded under <paramref name="correlationId"/>, if present and
    /// not yet expired. Whether or not this returns <see langword="true"/>, the slot is gone afterward.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out string? prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            // EvictExpiredAndStale performs a full sweep, so no remaining entry is expired afterward.
            EvictExpiredAndStale();

            if (_entries.Remove(key: correlationId, value: out var entry))
            {
                prompt = entry.Text;
                return true;
            }
        }

        prompt = null;
        return false;
    }

    /// <summary>
    /// Reads the prompt text recorded under <paramref name="correlationId"/>, if present and not yet
    /// expired, without removing it - so another concurrently-dispatched grader for the same request can
    /// still read it too. See <see cref="PendingResponseTextCache.TryPeek"/>'s remarks for why this exists.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found.</returns>
    public bool TryPeek(string correlationId, out string? prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (_entries.TryGetValue(key: correlationId, value: out var entry))
            {
                prompt = entry.Text;
                return true;
            }
        }

        prompt = null;
        return false;
    }

    /// <summary>
    /// Drops expired entries and any stale queue entries left behind by a prior <see cref="TryTake"/> or
    /// capacity eviction. Must be called under <see cref="_lock"/>. A re-<see cref="Set"/> on an existing
    /// key refreshes that entry's expiry without moving its queue position, so insertion order can no
    /// longer be trusted as TTL order - this is a full bounded sweep (each queued key visited once, live
    /// keys re-enqueued) rather than a prefix scan that stops at the first unexpired entry. Mirrors
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

    /// <summary>A single cached prompt awaiting judging, with the absolute time it expires.</summary>
    /// <param name="Text">The (possibly truncated) prompt text.</param>
    /// <param name="ExpiresAtUtc">The UTC instant after which this entry is treated as evicted.</param>
    private sealed record Entry(string Text, DateTimeOffset ExpiresAtUtc);
}
