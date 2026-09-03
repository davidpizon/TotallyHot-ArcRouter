using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.Router.Embeddings;

/// <summary>Covers <see cref="PendingRequestProvenanceCache"/>, mirroring <see cref="PendingTaskEmbeddingCacheTests"/>'s test list.</summary>
public class PendingRequestProvenanceCacheTests
{
    [Fact]
    public void TryTake_AfterSet_ReturnsTheSameProvenanceAndRemovesTheEntry()
    {
        var cache = Create();

        cache.Set("corr-1", isExploratory: true, propensity: 0.05);

        Assert.True(cache.TryTake("corr-1", out var isExploratory, out var propensity));
        Assert.True(isExploratory);
        Assert.Equal(0.05, propensity, precision: 6);
        Assert.False(cache.TryTake("corr-1", out _, out _));
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalseWithDefaults()
    {
        var cache = Create();

        Assert.False(cache.TryTake("never-set", out var isExploratory, out var propensity));
        Assert.False(isExploratory);
        Assert.Equal(1.0, propensity, precision: 6);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set("corr-1", isExploratory: false, propensity: 1.0);
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake("corr-1", out _, out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set("corr-1", true, 0.1);
        cache.Set("corr-2", true, 0.2);
        cache.Set("corr-3", true, 0.3);

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryTake("corr-1", out _, out _));
        Assert.True(cache.TryTake("corr-2", out _, out _));
        Assert.True(cache.TryTake("corr-3", out _, out _));
    }

    [Fact]
    public void Set_SameCorrelationIdTwice_DoesNotDuplicateInsertionOrder()
    {
        var cache = Create(capacity: 1);

        cache.Set("corr-1", false, 1.0);
        cache.Set("corr-1", true, 0.5);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryTake("corr-1", out var isExploratory, out var propensity));
        Assert.True(isExploratory);
        Assert.Equal(0.5, propensity, precision: 6);
    }

    private static PendingRequestProvenanceCache Create(int capacity = 2_000, int ttlSeconds = 300, TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new RoutingOptions
        {
            PendingEmbeddingCacheCapacity = capacity,
            PendingEmbeddingCacheTtlSeconds = ttlSeconds,
        });

        return new PendingRequestProvenanceCache(options, timeProvider);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
