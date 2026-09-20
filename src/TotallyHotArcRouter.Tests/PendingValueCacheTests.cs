using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests;

/// <summary>Covers <see cref="PendingValueCache{T}"/>'s TTL, capacity, take-once, and refresh-behind-head eviction.</summary>
public class PendingValueCacheTests
{
    [Fact]
    public void TryTake_AfterSet_ReturnsTheSameValueAndRemovesTheEntry()
    {
        var cache = Create();
        var embedding = new[] { 1f, 2f, 3f };

        cache.Set(correlationId: "corr-1", value: embedding);

        Assert.True(cache.TryTake(correlationId: "corr-1", value: out var taken));
        Assert.Same(expected: embedding, actual: taken);
        Assert.False(cache.TryTake(correlationId: "corr-1", value: out _));
    }

    [Fact]
    public void TryPeek_AfterSet_ReturnsTheValueWithoutRemovingIt()
    {
        var cache = Create();
        cache.Set(correlationId: "corr-1", value: [1f]);

        Assert.True(cache.TryPeek(correlationId: "corr-1", value: out var peeked));
        Assert.True(cache.TryPeek(correlationId: "corr-1", value: out var peekedAgain));
        Assert.Same(expected: peeked, actual: peekedAgain);
        Assert.Equal(1, actual: cache.Count);
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalse()
    {
        var cache = Create();

        Assert.False(cache.TryTake(correlationId: "never-set", value: out var embedding));
        Assert.Null(embedding);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set(correlationId: "corr-1", value: [1f]);
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake(correlationId: "corr-1", value: out _));
    }

    [Fact]
    public void TryTake_BeforeTtlExpires_StillReturnsTheEntry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set(correlationId: "corr-1", value: [1f]);
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.True(cache.TryTake(correlationId: "corr-1", value: out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set(correlationId: "corr-1", value: [1f]);
        cache.Set(correlationId: "corr-2", value: [2f]);
        cache.Set(correlationId: "corr-3", value: [3f]);

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryTake(correlationId: "corr-1", value: out _));
        Assert.True(cache.TryTake(correlationId: "corr-2", value: out _));
        Assert.True(cache.TryTake(correlationId: "corr-3", value: out _));
    }

    [Fact]
    public void Set_ExpiredEntriesAreEvictedOnNextAccess_NotJustOnCapacity()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 5, capacity: 100, timeProvider: clock);

        cache.Set(correlationId: "corr-1", value: [1f]);
        clock.Advance(TimeSpan.FromSeconds(6));
        cache.Set(correlationId: "corr-2", value: [2f]);

        Assert.Equal(1, actual: cache.Count);
    }

    [Fact]
    public void EvictExpiredAndStale_ExpiredEntryBehindARefreshedHead_IsStillEvicted()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 5, capacity: 100, timeProvider: clock);

        cache.Set(correlationId: "corr-1", value: [1f]);
        cache.Set(correlationId: "corr-2", value: [2f]);

        clock.Advance(TimeSpan.FromSeconds(3));
        cache.Set(correlationId: "corr-1", value: [3f]);

        clock.Advance(TimeSpan.FromSeconds(3));
        cache.Set(correlationId: "corr-3", value: [4f]);

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryTake(correlationId: "corr-2", value: out _));
        Assert.True(cache.TryTake(correlationId: "corr-1", value: out var refreshed));
        Assert.Equal(3f, actual: refreshed[0]);
    }

    [Fact]
    public void Set_SameCorrelationIdTwice_DoesNotDuplicateInsertionOrder()
    {
        var cache = Create(capacity: 1);

        cache.Set(correlationId: "corr-1", value: [1f]);
        cache.Set(correlationId: "corr-1", value: [2f]);

        Assert.Equal(1, actual: cache.Count);
        Assert.True(cache.TryTake(correlationId: "corr-1", value: out var embedding));
        Assert.Equal(2f, actual: embedding[0]);
    }

    [Fact]
    public void Set_Merge_CombinesIntoTheExistingEntry()
    {
        var cache = new PendingValueCache<Dictionary<string, string>>(Options.Create(new RoutingOptions
        { PendingEmbeddingCacheCapacity = 10, PendingEmbeddingCacheTtlSeconds = 300 }));

        cache.Set("corr-1", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a"] = "1" });
        cache.Set("corr-1", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["b"] = "2" },
            merge: existing =>
            {
                existing["b"] = "2";
                return existing;
            });

        Assert.True(cache.TryTake("corr-1", out var map));
        Assert.Equal(expected: "1", actual: map["a"]);
        Assert.Equal(expected: "2", actual: map["b"]);
    }

    private static PendingValueCache<float[]> Create(int capacity = 2_000, int ttlSeconds = 300,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new RoutingOptions
        {
            PendingEmbeddingCacheCapacity = capacity,
            PendingEmbeddingCacheTtlSeconds = ttlSeconds
        });

        return new PendingValueCache<float[]>(options: options, timeProvider: timeProvider);
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
