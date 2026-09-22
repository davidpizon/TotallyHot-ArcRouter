using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter;

/// <summary>
/// A correlation-id-keyed, TTL- and capacity-bounded in-process cache. One implementation covers every
/// pending-* bridge on the request path (embeddings, cost, provenance, response length, and the
/// text/prompt/backbone wrappers) so those clones cannot drift apart again. <see cref="Set"/> may be called
/// repeatedly for the same key: each call replaces the value (or, with a merge function, combines into it)
/// and refreshes its TTL. Reads are separate from that: <see cref="TryTake"/> removes the entry so a value
/// is taken at most once, while <see cref="TryPeek"/> leaves it in place for concurrent readers.
/// </summary>
/// <typeparam name="T">The cached value type.</typeparam>
public sealed class PendingValueCache<T>
{
    private readonly int _capacity;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Initializes a new instance of the <see cref="PendingValueCache{T}"/> class from already-validated
    /// bounds. Private: every caller sizes a cache from <see cref="RoutingOptions"/> or
    /// <see cref="JudgeOptions"/> through the public constructors, which null-check the options first.
    /// </summary>
    /// <param name="capacity">Maximum live entries; oldest keys are evicted first once exceeded.</param>
    /// <param name="ttl">How long an unclaimed entry is retained.</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    private PendingValueCache(int capacity, TimeSpan ttl, TimeProvider? timeProvider)
    {
        _capacity = capacity;
        _ttl = ttl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Initializes a cache bounded by <see cref="RoutingOptions.PendingEmbeddingCacheCapacity"/> and
    /// <see cref="RoutingOptions.PendingEmbeddingCacheTtlSeconds"/>.
    /// </summary>
    /// <param name="options">Supplies the shared embedding-cache capacity and TTL.</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    public PendingValueCache(IOptions<RoutingOptions> options, TimeProvider? timeProvider = null)
        : this(BoundsFromRouting(options), timeProvider)
    {
    }

    /// <summary>
    /// Initializes a cache bounded by <see cref="JudgeOptions.CacheCapacity"/> and
    /// <see cref="JudgeOptions.CacheTtlSeconds"/>.
    /// </summary>
    /// <param name="options">Supplies the shared judge-cache capacity and TTL.</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    public PendingValueCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
        : this(BoundsFromJudge(options), timeProvider)
    {
    }

    /// <summary>Unpacks routing-option bounds so the public constructors can null-check before reading them.</summary>
    private PendingValueCache((int Capacity, TimeSpan Ttl) bounds, TimeProvider? timeProvider)
        : this(bounds.Capacity, bounds.Ttl, timeProvider)
    {
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
    /// Records <paramref name="value"/> under <paramref name="correlationId"/>, evicting expired entries
    /// first and then the oldest entries beyond capacity. When <paramref name="merge"/> is provided and
    /// the key already holds an unexpired value, that existing value is passed to <paramref name="merge"/>
    /// and the result stored instead of replacing it outright.
    /// </summary>
    /// <param name="correlationId">The request correlation id that later readers will look up.</param>
    /// <param name="value">The value to store, or the seed value when <paramref name="merge"/> runs.</param>
    /// <param name="merge">
    /// Optional combiner for additive caches (grader backbones). Invoked only when the key already exists.
    /// </param>
    public void Set(string correlationId, T value, Func<T, T>? merge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (value is null) throw new ArgumentNullException(nameof(value));

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (merge is not null && _entries.TryGetValue(correlationId, out var existing))
            {
                value = merge(existing.Value);
            }
            else if (!_entries.ContainsKey(correlationId))
            {
                _insertionOrder.Enqueue(correlationId);
            }

            _entries[correlationId] = new Entry(Value: value, ExpiresAtUtc: _timeProvider.GetUtcNow() + _ttl);

            while (_entries.Count > _capacity && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Reads the value recorded under <paramref name="correlationId"/>, if present and not yet expired,
    /// without removing it.
    /// </summary>
    /// <param name="correlationId">The request correlation id to look up.</param>
    /// <param name="value">The cached value when this returns <see langword="true"/>; otherwise default.</param>
    /// <returns><see langword="true"/> if an unexpired entry was found.</returns>
    public bool TryPeek(string correlationId, [MaybeNullWhen(false)] out T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (_entries.TryGetValue(key: correlationId, value: out var entry))
            {
                value = entry.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Removes and returns the value recorded under <paramref name="correlationId"/>, if present and not
    /// yet expired.
    /// </summary>
    /// <param name="correlationId">The request correlation id to look up.</param>
    /// <param name="value">The cached value when this returns <see langword="true"/>; otherwise default.</param>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, [MaybeNullWhen(false)] out T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (_entries.Remove(key: correlationId, value: out var entry))
            {
                value = entry.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Drops expired entries and any stale queue entries left behind by a prior <see cref="TryTake"/> or
    /// capacity eviction. Must be called under <see cref="_lock"/>. A re-<see cref="Set"/> on an existing
    /// key refreshes that entry's expiry without moving its queue position, so this is a full bounded sweep
    /// rather than a prefix scan that stops at the first unexpired entry.
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

    /// <summary>Reads capacity and TTL from <paramref name="options"/> after a null check.</summary>
    private static (int Capacity, TimeSpan Ttl) BoundsFromRouting(IOptions<RoutingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (options.Value.PendingEmbeddingCacheCapacity,
            TimeSpan.FromSeconds(options.Value.PendingEmbeddingCacheTtlSeconds));
    }

    /// <summary>Reads capacity and TTL from <paramref name="options"/> after a null check.</summary>
    private static (int Capacity, TimeSpan Ttl) BoundsFromJudge(IOptions<JudgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (options.Value.CacheCapacity, TimeSpan.FromSeconds(options.Value.CacheTtlSeconds));
    }

    /// <summary>A single cached value awaiting a later reader, with the absolute time it expires.</summary>
    /// <param name="Value">The cached payload.</param>
    /// <param name="ExpiresAtUtc">The UTC instant after which this entry is treated as evicted.</param>
    private sealed record Entry(T Value, DateTimeOffset ExpiresAtUtc);
}
