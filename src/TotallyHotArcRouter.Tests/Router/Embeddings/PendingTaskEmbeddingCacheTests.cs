using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Embeddings;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Router.Embeddings;

/// <summary>Covers <see cref="PendingTaskEmbeddingCache"/>.</summary>
public class PendingTaskEmbeddingCacheTests
{
    [Fact]
    public void TryTake_AfterSet_ReturnsTheSameEmbeddingAndRemovesTheEntry()
    {
        var cache = Create();
        var embedding = new float[] { 1f, 2f, 3f };

        cache.Set("corr-1", embedding);

        Assert.True(cache.TryTake("corr-1", out var taken));
        Assert.Same(embedding, taken);
        Assert.False(cache.TryTake("corr-1", out _));
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalse()
    {
        var cache = Create();

        Assert.False(cache.TryTake("never-set", out var embedding));
        Assert.Null(embedding);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set("corr-1", [1f]);
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake("corr-1", out _));
    }

    [Fact]
    public void TryTake_BeforeTtlExpires_StillReturnsTheEntry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set("corr-1", [1f]);
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.True(cache.TryTake("corr-1", out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set("corr-1", [1f]);
        cache.Set("corr-2", [2f]);
        cache.Set("corr-3", [3f]);

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryTake("corr-1", out _));
        Assert.True(cache.TryTake("corr-2", out _));
        Assert.True(cache.TryTake("corr-3", out _));
    }

    [Fact]
    public void Set_ExpiredEntriesAreEvictedOnNextAccess_NotJustOnCapacity()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 5, capacity: 100, timeProvider: clock);

        cache.Set("corr-1", [1f]);
        clock.Advance(TimeSpan.FromSeconds(6));
        cache.Set("corr-2", [2f]);

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Set_SameCorrelationIdTwice_DoesNotDuplicateInsertionOrder()
    {
        var cache = Create(capacity: 1);

        cache.Set("corr-1", [1f]);
        cache.Set("corr-1", [2f]);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryTake("corr-1", out var embedding));
        Assert.Equal(2f, embedding![0]);
    }

    private static PendingTaskEmbeddingCache Create(int capacity = 2_000, int ttlSeconds = 300, TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new RoutingOptions
        {
            PendingEmbeddingCacheCapacity = capacity,
            PendingEmbeddingCacheTtlSeconds = ttlSeconds,
        });

        return new PendingTaskEmbeddingCache(options, timeProvider);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
