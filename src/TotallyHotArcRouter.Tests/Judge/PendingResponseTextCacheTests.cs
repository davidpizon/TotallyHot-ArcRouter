using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="PendingResponseTextCache"/>, mirroring
/// <see cref="TotallyHot.ArcRouter.Tests.Router.Embeddings.PendingTaskEmbeddingCacheTests"/>'s shape for
/// docs/router/geval-shadow-scoring-plan.md §Raw-text preservation's TTL and capacity guarantees.
/// </summary>
public class PendingResponseTextCacheTests
{
    [Fact]
    public void TryTake_AfterSet_ReturnsTheSameTextAndRemovesTheEntry()
    {
        var cache = Create();

        cache.Set(correlationId: "corr-1", text: "hello world");

        Assert.True(cache.TryTake(correlationId: "corr-1", text: out var taken));
        Assert.Equal(expected: "hello world", actual: taken);
        Assert.False(cache.TryTake(correlationId: "corr-1", text: out _));
    }

    [Fact]
    public void TryTake_UnknownCorrelationId_ReturnsFalse()
    {
        var cache = Create();

        Assert.False(cache.TryTake(correlationId: "never-set", text: out var text));
        Assert.Null(text);
    }

    [Fact]
    public void TryTake_AfterTtlExpires_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set(correlationId: "corr-1", text: "text");
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(cache.TryTake(correlationId: "corr-1", text: out _));
    }

    [Fact]
    public void TryTake_BeforeTtlExpires_StillReturnsTheEntry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = Create(ttlSeconds: 10, timeProvider: clock);

        cache.Set(correlationId: "corr-1", text: "text");
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.True(cache.TryTake(correlationId: "corr-1", text: out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);

        cache.Set(correlationId: "corr-1", text: "one");
        cache.Set(correlationId: "corr-2", text: "two");
        cache.Set(correlationId: "corr-3", text: "three");

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryTake(correlationId: "corr-1", text: out _));
        Assert.True(cache.TryTake(correlationId: "corr-2", text: out _));
        Assert.True(cache.TryTake(correlationId: "corr-3", text: out _));
    }

    [Fact]
    public void Set_TextLongerThanMaxChars_IsTruncatedBeforeStorage()
    {
        var cache = Create(maxTextChars: 10);

        cache.Set(correlationId: "corr-1", text: "this text is definitely longer than ten characters");

        Assert.True(cache.TryTake(correlationId: "corr-1", text: out var taken));
        Assert.Equal(10, actual: taken!.Length);
    }

    [Fact]
    public void Set_SameCorrelationIdTwice_DoesNotDuplicateInsertionOrder()
    {
        var cache = Create(capacity: 1);

        cache.Set(correlationId: "corr-1", text: "first");
        cache.Set(correlationId: "corr-1", text: "second");

        Assert.Equal(1, actual: cache.Count);
        Assert.True(cache.TryTake(correlationId: "corr-1", text: out var text));
        Assert.Equal(expected: "second", actual: text);
    }

    private static PendingResponseTextCache Create(
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

        return new PendingResponseTextCache(options: options, timeProvider: timeProvider);
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