using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.Router.Embeddings;

/// <summary>Covers <see cref="PendingTaskEmbeddingCache"/>.</summary>
public class PendingTaskEmbeddingCacheTests
{
    [Fact]
    public void TryTake_AfterSet_ReturnsTheSameEmbeddingAndRemovesTheEntry()
    {
        var cache = Create();
        var embedding = new[] { 1f, 2f, 3f };

        cache.Set(correlationId: "corr-1", embedding: embedding);

        Assert.True(cache.TryTake(correlationId: "corr-1", embedding: out var taken));
        Assert.Same(expected: embedding, actual: taken);
        Assert.False(cache.TryTake(correlationId: "corr-1", embedding: out _));
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalse()
    {
        var cache = Create();

        Assert.False(cache.TryTake(correlationId: "never-set", embedding: out var embedding));
        Assert.Null(embedding);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set(correlationId: "corr-1", embedding: [1f]);
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake(correlationId: "corr-1", embedding: out _));
    }

    [Fact]
    public void TryTake_BeforeTtlExpires_StillReturnsTheEntry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set(correlationId: "corr-1", embedding: [1f]);
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.True(cache.TryTake(correlationId: "corr-1", embedding: out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set(correlationId: "corr-1", embedding: [1f]);
        cache.Set(correlationId: "corr-2", embedding: [2f]);
        cache.Set(correlationId: "corr-3", embedding: [3f]);

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryTake(correlationId: "corr-1", embedding: out _));
        Assert.True(cache.TryTake(correlationId: "corr-2", embedding: out _));
        Assert.True(cache.TryTake(correlationId: "corr-3", embedding: out _));
    }

    [Fact]
    public void Set_ExpiredEntriesAreEvictedOnNextAccess_NotJustOnCapacity()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 5, capacity: 100, timeProvider: clock);

        cache.Set(correlationId: "corr-1", embedding: [1f]);
        clock.Advance(TimeSpan.FromSeconds(6));
        cache.Set(correlationId: "corr-2", embedding: [2f]);

        Assert.Equal(1, actual: cache.Count);
    }

    [Fact]
    public void EvictExpiredAndStale_ExpiredEntryBehindARefreshedHead_IsStillEvicted()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 5, capacity: 100, timeProvider: clock);

        cache.Set(correlationId: "corr-1", embedding: [1f]);
        cache.Set(correlationId: "corr-2", embedding: [2f]);

        clock.Advance(TimeSpan.FromSeconds(3));
        cache.Set(correlationId: "corr-1",
            embedding: [3f]); // Refreshes corr-1's expiry without moving it ahead of corr-2 in the queue.

        clock.Advance(TimeSpan.FromSeconds(3)); // corr-2 (TTL from t=0) is now expired; corr-1 (TTL from t=3) is not.
        cache.Set(correlationId: "corr-3",
            embedding: [4f]); // Any Set/TryTake call sweeps - corr-2 must not survive behind corr-1's refreshed head.

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryTake(correlationId: "corr-2", embedding: out _));
        Assert.True(cache.TryTake(correlationId: "corr-1", embedding: out var refreshed));
        Assert.Equal(3f, actual: refreshed![0]);
    }

    [Fact]
    public void Set_SameCorrelationIdTwice_DoesNotDuplicateInsertionOrder()
    {
        var cache = Create(capacity: 1);

        cache.Set(correlationId: "corr-1", embedding: [1f]);
        cache.Set(correlationId: "corr-1", embedding: [2f]);

        Assert.Equal(1, actual: cache.Count);
        Assert.True(cache.TryTake(correlationId: "corr-1", embedding: out var embedding));
        Assert.Equal(2f, actual: embedding![0]);
    }

    private static PendingTaskEmbeddingCache Create(int capacity = 2_000, int ttlSeconds = 300,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new RoutingOptions
        {
            PendingEmbeddingCacheCapacity = capacity,
            PendingEmbeddingCacheTtlSeconds = ttlSeconds
        });

        return new PendingTaskEmbeddingCache(options: options, timeProvider: timeProvider);
    }

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