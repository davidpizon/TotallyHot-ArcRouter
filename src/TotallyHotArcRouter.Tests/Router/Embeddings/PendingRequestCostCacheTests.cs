using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Embeddings;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Router.Embeddings;

/// <summary>Covers <see cref="PendingRequestCostCache"/>, mirroring <see cref="PendingTaskEmbeddingCacheTests"/>'s test list.</summary>
public class PendingRequestCostCacheTests
{
    [Fact]
    public void TryTake_AfterSet_ReturnsTheSameCostAndRemovesTheEntry()
    {
        var cache = Create();

        cache.Set("corr-1", 0.0042m);

        Assert.True(cache.TryTake("corr-1", out var taken));
        Assert.Equal(0.0042m, taken);
        Assert.False(cache.TryTake("corr-1", out _));
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalse()
    {
        var cache = Create();

        Assert.False(cache.TryTake("never-set", out var cost));
        Assert.Equal(0m, cost);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set("corr-1", 1m);
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake("corr-1", out _));
    }

    [Fact]
    public void TryTake_BeforeTtlExpires_StillReturnsTheEntry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set("corr-1", 1m);
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.True(cache.TryTake("corr-1", out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set("corr-1", 1m);
        cache.Set("corr-2", 2m);
        cache.Set("corr-3", 3m);

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

        cache.Set("corr-1", 1m);
        clock.Advance(TimeSpan.FromSeconds(6));
        cache.Set("corr-2", 2m);

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Set_SameCorrelationIdTwice_DoesNotDuplicateInsertionOrder()
    {
        var cache = Create(capacity: 1);

        cache.Set("corr-1", 1m);
        cache.Set("corr-1", 2m);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryTake("corr-1", out var cost));
        Assert.Equal(2m, cost);
    }

    private static PendingRequestCostCache Create(int capacity = 2_000, int ttlSeconds = 300, TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new RoutingOptions
        {
            PendingEmbeddingCacheCapacity = capacity,
            PendingEmbeddingCacheTtlSeconds = ttlSeconds,
        });

        return new PendingRequestCostCache(options, timeProvider);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
