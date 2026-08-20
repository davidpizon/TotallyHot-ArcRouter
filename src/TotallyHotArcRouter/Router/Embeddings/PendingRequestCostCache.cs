using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// Bridges a request's estimated dollar cost - computed once the response completes, in
/// <c>ProxyMiddleware</c> - to its later-arriving verifier score, which carries only
/// <see cref="TotallyHot.ArcRouter.Sandbox.SandboxResult.RequestCorrelationId"/>
/// (docs/router/self-organizing-classification-plan.md Phase T1c). Mirrors
/// <see cref="PendingTaskEmbeddingCache"/>'s shape exactly - same TTL/capacity/eviction semantics,
/// same correlation-id-keyed set-once/take-once contract - because it exists to close the same gap for
/// a different value: <see cref="EmbeddingMemoryScoreObserver"/> currently writes <c>cost: 0.0</c>
/// unconditionally, and this is how the real, already-computed cost reaches it. Deliberately reuses
/// <see cref="RoutingOptions.PendingEmbeddingCacheCapacity"/>/<see cref="RoutingOptions.PendingEmbeddingCacheTtlSeconds"/>
/// rather than adding a second pair of options: both caches are keyed by the same correlation id, live
/// for the same request lifecycle (until the same verifier score arrives), and a second, independently
/// configurable pair would be a distinction with no behavioral difference to justify the extra surface.
/// </summary>
public sealed class PendingRequestCostCache
{
    /// <summary>A single cached cost awaiting its verifier score, with the absolute time it expires.</summary>
    /// <param name="Cost">The request's estimated dollar cost.</param>
    /// <param name="ExpiresAtUtc">The UTC instant after which this entry is treated as evicted.</param>
    private sealed record Entry(decimal Cost, DateTimeOffset ExpiresAtUtc);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="PendingRequestCostCache"/> class.</summary>
    /// <param name="options">Supplies the capacity and TTL bounds (shared with <see cref="PendingTaskEmbeddingCache"/>).</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for deterministic tests.</param>
    public PendingRequestCostCache(IOptions<RoutingOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _ttl = TimeSpan.FromSeconds(options.Value.PendingEmbeddingCacheTtlSeconds);
        _capacity = options.Value.PendingEmbeddingCacheCapacity;
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
    /// Records <paramref name="cost"/> under <paramref name="correlationId"/>, evicting expired entries
    /// first and then the oldest entries beyond capacity.
    /// </summary>
    public void Set(string correlationId, decimal cost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (!_entries.ContainsKey(correlationId))
            {
                _insertionOrder.Enqueue(correlationId);
            }

            _entries[correlationId] = new Entry(cost, _timeProvider.GetUtcNow() + _ttl);

            while (_entries.Count > _capacity && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Removes and returns the cost recorded under <paramref name="correlationId"/>, if present and not
    /// yet expired.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out decimal cost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            // EvictExpiredAndStale performs a full sweep, so no remaining entry is expired afterward.
            EvictExpiredAndStale();

            if (_entries.Remove(correlationId, out var entry))
            {
                cost = entry.Cost;
                return true;
            }
        }

        cost = 0m;
        return false;
    }

    /// <summary>
    /// Drops expired entries and any stale queue entries left behind by a prior <see cref="TryTake"/> or
    /// capacity eviction. Must be called under <see cref="_lock"/>. A re-<see cref="Set"/> on an existing
    /// key refreshes that entry's expiry without moving its queue position, so insertion order can no
    /// longer be trusted as TTL order - this is a full bounded sweep (each queued key visited once, live
    /// keys re-enqueued) rather than a prefix scan that stops at the first unexpired entry.
    /// </summary>
    private void EvictExpiredAndStale()
    {
        var now = _timeProvider.GetUtcNow();
        var remaining = _insertionOrder.Count;
        for (var i = 0; i < remaining; i++)
        {
            var key = _insertionOrder.Dequeue();
            if (!_entries.TryGetValue(key, out var entry))
            {
                continue;
            }

            if (entry.ExpiresAtUtc > now)
            {
                _insertionOrder.Enqueue(key);
                continue;
            }

            _entries.Remove(key);
        }
    }
}
