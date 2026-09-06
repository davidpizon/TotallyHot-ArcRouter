using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Bridges a request's raw response text - already extracted on the hot path by
/// <see cref="Telemetry.ResponseTextExtractor"/> - to every later-arriving background grading job (the
/// G-Eval shadow judge, and Phase Q3's CodeJudge/ICE-Score/RACE portfolio), keyed by the same correlation id
/// <see cref="Router.Embeddings.PendingTaskEmbeddingCache"/> uses (docs/router/geval-shadow-scoring-plan.md
/// §Raw-text preservation). Mirrors <see cref="Router.Embeddings.PendingTaskEmbeddingCache"/>'s design:
/// TTL-bounded, capacity-bounded, a <see cref="Dictionary{TKey,TValue}"/> plus a <see cref="Queue{T}"/> for
/// insertion order, and an injectable <see cref="TimeProvider"/> for deterministic tests. Nothing is ever
/// written to disk: an entry is gone from process memory once its TTL elapses or it is evicted for capacity
/// - the router's memory must not become a transcript store (docs/router/live-feedback-learning-plan.md's
/// standing decision).
/// </summary>
/// <remarks>
/// <b>Phase Q3 changed this from single-take to multi-read.</b> Before Q3, the G-Eval judge was the only
/// consumer, so <see cref="TryTake"/> removed an entry the instant it was read - "gone the moment it is
/// taken" was both the privacy commitment and the whole eviction mechanism. With up to four independent
/// async graders now reading the same cached text for one request, a remove-on-first-read policy would
/// starve every grader but whichever ran first. <see cref="TryPeek"/> reads without removing; entries are
/// now bounded only by <see cref="JudgeOptions.CacheTtlSeconds"/> and <see cref="JudgeOptions.CacheCapacity"/>,
/// the same bounds as before - just enforced by expiry/eviction alone rather than by an explicit take as
/// well. <see cref="TryTake"/> is kept for callers that still want single-consumer removal.
/// </remarks>
public sealed class PendingResponseTextCache
{
    private readonly int _capacity;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();
    private readonly int _maxTextChars;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    /// <summary>Initializes a new instance of the <see cref="PendingResponseTextCache"/> class.</summary>
    /// <param name="options">Supplies the capacity, TTL, and per-entry text-size bounds.</param>
    /// <param name="timeProvider">
    /// Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for
    /// deterministic tests.
    /// </param>
    public PendingResponseTextCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
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
    /// Records <paramref name="text"/> under <paramref name="correlationId"/>, truncating to the
    /// configured per-entry character cap first, then evicting expired entries and finally the oldest
    /// entries beyond capacity.
    /// </summary>
    public void Set(string correlationId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(text);

        var bounded = text.Length > _maxTextChars ? text[.._maxTextChars] : text;

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
    /// Removes and returns the response text recorded under <paramref name="correlationId"/>, if present
    /// and not yet expired. Whether or not this returns <see langword="true"/>, the slot is gone
    /// afterward.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            // EvictExpiredAndStale performs a full sweep, so no remaining entry is expired afterward.
            EvictExpiredAndStale();

            if (_entries.Remove(key: correlationId, value: out var entry))
            {
                text = entry.Text;
                return true;
            }
        }

        text = null;
        return false;
    }

    /// <summary>
    /// Reads the response text recorded under <paramref name="correlationId"/>, if present and not yet
    /// expired, without removing it - so another concurrently-dispatched grader for the same request can
    /// still read it too. The entry is removed only by TTL expiry or capacity eviction (both still enforced
    /// here, via <see cref="EvictExpiredAndStale"/>), never by this call itself.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found.</returns>
    public bool TryPeek(string correlationId, out string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (_entries.TryGetValue(key: correlationId, value: out var entry))
            {
                text = entry.Text;
                return true;
            }
        }

        text = null;
        return false;
    }

    /// <summary>
    /// Drops expired entries and any stale queue entries left behind by a prior <see cref="TryTake"/> or
    /// capacity eviction. Must be called under <see cref="_lock"/>. A re-<see cref="Set"/> on an existing
    /// key refreshes that entry's expiry without moving its queue position, so insertion order can no
    /// longer be trusted as TTL order - this is a full bounded sweep (each queued key visited once, live
    /// keys re-enqueued) rather than a prefix scan that stops at the first unexpired entry. Mirrors
    /// <see cref="Router.Embeddings.PendingTaskEmbeddingCache.EvictExpiredAndStale"/> exactly.
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

    /// <summary>A single cached response text awaiting judging, with the absolute time it expires.</summary>
    /// <param name="Text">The (possibly truncated) response text.</param>
    /// <param name="ExpiresAtUtc">The UTC instant after which this entry is treated as evicted.</param>
    private sealed record Entry(string Text, DateTimeOffset ExpiresAtUtc);
}