using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="PendingGraderBackboneCache"/>, in particular the additive-Set behavior that sets it
/// apart from every other pending-* cache (docs/router/grader-reliability-plan.md, Phase Q4).
/// </summary>
public class PendingGraderBackboneCacheTests
{
    [Fact]
    public void Set_TwoGradersSameCorrelationId_TryTakeReturnsBothEntries()
    {
        var cache = Create();

        cache.Set(correlationId: "corr-1", graderKey: "judge", backboneModel: "judge-model");
        cache.Set(correlationId: "corr-1", graderKey: "codejudge", backboneModel: "codejudge-model");

        Assert.True(cache.TryTake(correlationId: "corr-1", backboneByGraderKey: out var map));
        Assert.Equal(expected: "judge-model", actual: map["judge"]);
        Assert.Equal(expected: "codejudge-model", actual: map["codejudge"]);
    }

    [Fact]
    public void TryTake_RemovesTheEntry()
    {
        var cache = Create();
        cache.Set(correlationId: "corr-1", graderKey: "judge", backboneModel: "judge-model");

        Assert.True(cache.TryTake(correlationId: "corr-1", backboneByGraderKey: out _));
        Assert.False(cache.TryTake(correlationId: "corr-1", backboneByGraderKey: out var second));
        Assert.Empty(second);
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalseWithAnEmptyMap()
    {
        var cache = Create();

        Assert.False(cache.TryTake(correlationId: "never-set", backboneByGraderKey: out var map));
        Assert.Empty(map);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);
        cache.Set(correlationId: "corr-1", graderKey: "judge", backboneModel: "judge-model");

        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake(correlationId: "corr-1", backboneByGraderKey: out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set(correlationId: "corr-1", graderKey: "judge", backboneModel: "m1");
        cache.Set(correlationId: "corr-2", graderKey: "judge", backboneModel: "m2");
        cache.Set(correlationId: "corr-3", graderKey: "judge", backboneModel: "m3");

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryTake(correlationId: "corr-1", backboneByGraderKey: out _));
        Assert.True(cache.TryTake(correlationId: "corr-2", backboneByGraderKey: out _));
        Assert.True(cache.TryTake(correlationId: "corr-3", backboneByGraderKey: out _));
    }

    private static PendingGraderBackboneCache Create(
        int capacity = 2_000,
        int ttlSeconds = 300,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new JudgeOptions { CacheCapacity = capacity, CacheTtlSeconds = ttlSeconds });
        return new PendingGraderBackboneCache(options: options, timeProvider: timeProvider);
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
