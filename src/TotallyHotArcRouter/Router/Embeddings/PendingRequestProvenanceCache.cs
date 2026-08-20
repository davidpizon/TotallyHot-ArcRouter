using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// Bridges a request's exploration provenance - <see cref="ModelRouteResolutionResult.IsExploratory"/>
/// and <see cref="ModelRouteResolutionResult.Propensity"/>, both known once routing resolves - to its
/// later-arriving verifier score (docs/router/self-organizing-classification-plan.md Phase T1c). A third
/// cache alongside <see cref="PendingTaskEmbeddingCache"/> and <see cref="PendingRequestCostCache"/>,
/// mirroring their exact shape, rather than folding provenance into either of them: the embedding cache
/// already has dedicated test coverage and callers keyed purely on <c>float[]?</c>, and keeping the three
/// values in three parallel caches (all keyed by the same correlation id, all set at the same point in
/// <c>ProxyMiddleware</c>) is simpler than widening a shared entry type three separate consumers would
/// then need to partially deconstruct.
/// </summary>
public sealed class PendingRequestProvenanceCache
{
    /// <summary>A single cached provenance record awaiting its verifier score, with the absolute time it expires.</summary>
    /// <param name="IsExploratory">Whether the request's routing decision was an epsilon-greedy exploratory pick.</param>
    /// <param name="Propensity">The propensity of the arm actually chosen.</param>
    /// <param name="Dimension">
    /// The heuristic classifier's dimension label for this request
    /// (docs/router/self-organizing-classification-plan.md Phase T2e), or <see langword="null"/> when
    /// unavailable.
    /// </param>
    /// <param name="ExpiresAtUtc">The UTC instant after which this entry is treated as evicted.</param>
    private sealed record Entry(bool IsExploratory, double Propensity, string? Dimension, DateTimeOffset ExpiresAtUtc);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="PendingRequestProvenanceCache"/> class.</summary>
    /// <param name="options">Supplies the capacity and TTL bounds (shared with <see cref="PendingTaskEmbeddingCache"/>).</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for deterministic tests.</param>
    public PendingRequestProvenanceCache(IOptions<RoutingOptions> options, TimeProvider? timeProvider = null)
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
    /// Records <paramref name="isExploratory"/>/<paramref name="propensity"/>/<paramref name="dimension"/>
    /// under <paramref name="correlationId"/>, evicting expired entries first and then the oldest entries
    /// beyond capacity.
    /// </summary>
    public void Set(string correlationId, bool isExploratory, double propensity, string? dimension = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (!_entries.ContainsKey(correlationId))
            {
                _insertionOrder.Enqueue(correlationId);
            }

            _entries[correlationId] = new Entry(isExploratory, propensity, dimension, _timeProvider.GetUtcNow() + _ttl);

            while (_entries.Count > _capacity && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Removes and returns the provenance recorded under <paramref name="correlationId"/>, if present and
    /// not yet expired.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out bool isExploratory, out double propensity, out string? dimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            // EvictExpiredAndStale performs a full sweep, so no remaining entry is expired afterward.
            EvictExpiredAndStale();

            if (_entries.Remove(correlationId, out var entry))
            {
                isExploratory = entry.IsExploratory;
                propensity = entry.Propensity;
                dimension = entry.Dimension;
                return true;
            }
        }

        isExploratory = false;
        propensity = 1.0;
        dimension = null;
        return false;
    }

    /// <summary>
    /// Overload of <see cref="TryTake(string, out bool, out double, out string)"/> for callers that do not
    /// need the dimension label.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out bool isExploratory, out double propensity) =>
        TryTake(correlationId, out isExploratory, out propensity, out _);

    /// <summary>
    /// Drops expired entries and any stale queue entries left behind by a prior <see cref="TryTake(string, out bool, out double)"/> or
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
