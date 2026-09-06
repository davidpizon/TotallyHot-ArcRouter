using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="PendingPromptCache"/>, mirroring
/// <see cref="PendingResponseTextCacheTests"/>'s shape exactly - the two caches share the same TTL/capacity
/// design and the same <see cref="JudgeOptions"/> bounds.
/// </summary>
public class PendingPromptCacheTests
{
    [Fact]
    public void TryTake_AfterSet_ReturnsTheSamePromptAndRemovesTheEntry()
    {
        var cache = Create();

        cache.Set(correlationId: "corr-1", prompt: "hello world");

        Assert.True(cache.TryTake(correlationId: "corr-1", prompt: out var taken));
        Assert.Equal(expected: "hello world", actual: taken);
        Assert.False(cache.TryTake(correlationId: "corr-1", prompt: out _));
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalse()
    {
        var cache = Create();

        Assert.False(cache.TryTake(correlationId: "never-set", prompt: out var prompt));
        Assert.Null(prompt);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set(correlationId: "corr-1", prompt: "text");
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake(correlationId: "corr-1", prompt: out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set(correlationId: "corr-1", prompt: "one");
        cache.Set(correlationId: "corr-2", prompt: "two");
        cache.Set(correlationId: "corr-3", prompt: "three");

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryTake(correlationId: "corr-1", prompt: out _));
        Assert.True(cache.TryTake(correlationId: "corr-2", prompt: out _));
        Assert.True(cache.TryTake(correlationId: "corr-3", prompt: out _));
    }

    [Fact]
    public void Set_PromptLongerThanMaxChars_IsTruncatedBeforeStorage()
    {
        var cache = Create(maxTextChars: 10);

        cache.Set(correlationId: "corr-1", prompt: "this prompt is definitely longer than ten characters");

        Assert.True(cache.TryTake(correlationId: "corr-1", prompt: out var taken));
        Assert.Equal(10, actual: taken!.Length);
    }

    private static PendingPromptCache Create(
        int capacity = 2_000,
        int ttlSeconds = 300,
        int maxTextChars = 65_536,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new JudgeOptions
        {
            CacheCapacity = capacity,
            CacheTtlSeconds = ttlSeconds,
            MaxCachedTextChars = maxTextChars
        });

        return new PendingPromptCache(options: options, timeProvider: timeProvider);
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
