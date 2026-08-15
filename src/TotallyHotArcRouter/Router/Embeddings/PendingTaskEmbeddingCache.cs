using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// Bridges a request's task embedding - computed on the request path - to its later-arriving verifier
/// score, which carries only <see cref="TotallyHot.ArcRouter.Sandbox.SandboxResult.RequestCorrelationId"/>
/// (docs/router/live-feedback-learning-plan.md Phase 2c). A correlation id is set once and taken at most
/// once; entries older than <see cref="RoutingOptions.PendingEmbeddingCacheTtlSeconds"/>, or beyond
/// <see cref="RoutingOptions.PendingEmbeddingCacheCapacity"/> (oldest first), are evicted so a score that
/// never arrives - the sandbox is disabled, the evaluation is dropped, the request is aborted - cannot
/// hold its slot forever.
/// </summary>
public sealed class PendingTaskEmbeddingCache
{
    private sealed record Entry(float[] Embedding, DateTimeOffset ExpiresAtUtc);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="PendingTaskEmbeddingCache"/> class.</summary>
    /// <param name="options">Supplies the capacity and TTL bounds.</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for deterministic tests.</param>
    public PendingTaskEmbeddingCache(IOptions<RoutingOptions> options, TimeProvider? timeProvider = null)
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
    /// Records <paramref name="embedding"/> under <paramref name="correlationId"/>, evicting expired
    /// entries first and then the oldest entries beyond capacity.
    /// </summary>
    public void Set(string correlationId, float[] embedding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(embedding);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (!_entries.ContainsKey(correlationId))
            {
                _insertionOrder.Enqueue(correlationId);
            }

            _entries[correlationId] = new Entry(embedding, _timeProvider.GetUtcNow() + _ttl);

            while (_entries.Count > _capacity && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Removes and returns the embedding recorded under <paramref name="correlationId"/>, if present and
    /// not yet expired.
    /// </summary>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out float[]? embedding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_lock)
        {
            EvictExpiredAndStale();

            if (_entries.Remove(correlationId, out var entry))
            {
                embedding = entry.Embedding;
                return true;
            }
        }

        embedding = null;
        return false;
    }

    /// <summary>
    /// Drops expired entries and any stale queue entries left behind by a prior <see cref="TryTake"/> or
    /// capacity eviction. Must be called under <see cref="_lock"/>. Insertion order is also TTL order (a
    /// fixed TTL applied at insertion time), so this is a simple prefix scan, not a full sweep.
    /// </summary>
    private void EvictExpiredAndStale()
    {
        var now = _timeProvider.GetUtcNow();
        while (_insertionOrder.Count > 0)
        {
            var key = _insertionOrder.Peek();
            if (!_entries.TryGetValue(key, out var entry))
            {
                _insertionOrder.Dequeue();
                continue;
            }

            if (entry.ExpiresAtUtc > now)
            {
                break;
            }

            _insertionOrder.Dequeue();
            _entries.Remove(key);
        }
    }
}
