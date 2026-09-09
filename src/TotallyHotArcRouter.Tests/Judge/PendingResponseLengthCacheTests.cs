using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="PendingResponseLengthCache"/>, mirroring <see cref="PendingResponseTextCacheTests"/>'s
/// shape for docs/router/grader-reliability-plan.md's TTL and capacity guarantees.
/// </summary>
public class PendingResponseLengthCacheTests
{
    [Fact]
    public void TryTake_AfterSet_ReturnsTheSameLengthAndRemovesTheEntry()
    {
        var cache = Create();

        cache.Set(correlationId: "corr-1", length: 42);

        Assert.True(cache.TryTake(correlationId: "corr-1", length: out var taken));
        Assert.Equal(42, actual: taken);
        Assert.False(cache.TryTake(correlationId: "corr-1", length: out _));
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalse()
    {
        var cache = Create();

        Assert.False(cache.TryTake(correlationId: "never-set", length: out var length));
        Assert.Equal(0, actual: length);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set(correlationId: "corr-1", length: 10);
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake(correlationId: "corr-1", length: out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set(correlationId: "corr-1", length: 1);
        cache.Set(correlationId: "corr-2", length: 2);
        cache.Set(correlationId: "corr-3", length: 3);

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryTake(correlationId: "corr-1", length: out _));
        Assert.True(cache.TryTake(correlationId: "corr-2", length: out _));
        Assert.True(cache.TryTake(correlationId: "corr-3", length: out _));
    }

    [Fact]
    public void Set_SameCorrelationIdTwice_DoesNotDuplicateInsertionOrder()
    {
        var cache = Create(capacity: 1);

        cache.Set(correlationId: "corr-1", length: 1);
        cache.Set(correlationId: "corr-1", length: 2);

        Assert.Equal(1, actual: cache.Count);
        Assert.True(cache.TryTake(correlationId: "corr-1", length: out var length));
        Assert.Equal(2, actual: length);
    }

    private static PendingResponseLengthCache Create(
        int capacity = 2_000,
        int ttlSeconds = 300,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new JudgeOptions { CacheCapacity = capacity, CacheTtlSeconds = ttlSeconds });
        return new PendingResponseLengthCache(options: options, timeProvider: timeProvider);
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
