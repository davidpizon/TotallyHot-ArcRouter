using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers the truncating, multi-read wrappers over <see cref="PendingValueCache{T}"/>. TTL/capacity live
/// on the generic; these tests pin the wrappers' only extra behavior (character cap, TryPeek does not
/// remove).
/// </summary>
public class PendingBoundedTextCacheTests
{
    [Fact]
    public void TryPeek_AfterSet_ReturnsTheSameTextWithoutRemovingIt()
    {
        var cache = CreateText();
        cache.Set(correlationId: "corr-1", text: "hello world");

        Assert.True(cache.TryPeek(correlationId: "corr-1", text: out var taken));
        Assert.Equal(expected: "hello world", actual: taken);
        Assert.True(cache.TryPeek(correlationId: "corr-1", text: out _));
        Assert.Equal(1, actual: cache.Count);
    }

    [Fact]
    public void Set_TextLongerThanMaxChars_IsTruncatedBeforeStorage()
    {
        var cache = CreateText(maxTextChars: 10);
        cache.Set(correlationId: "corr-1", text: "this text is definitely longer than ten characters");

        Assert.True(cache.TryPeek(correlationId: "corr-1", text: out var taken));
        Assert.Equal(10, actual: taken!.Length);
    }

    [Fact]
    public void Set_PromptLongerThanMaxChars_IsTruncatedBeforeStorage()
    {
        var cache = CreatePrompt(maxTextChars: 10);
        cache.Set(correlationId: "corr-1", prompt: "this prompt is definitely longer than ten characters");

        Assert.True(cache.TryPeek(correlationId: "corr-1", prompt: out var taken));
        Assert.Equal(10, actual: taken!.Length);
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsTheOldestEntryFirst()
    {
        var cache = CreateText(capacity: 2);
        cache.Set(correlationId: "corr-1", text: "one");
        cache.Set(correlationId: "corr-2", text: "two");
        cache.Set(correlationId: "corr-3", text: "three");

        Assert.Equal(2, actual: cache.Count);
        Assert.False(cache.TryPeek(correlationId: "corr-1", text: out _));
        Assert.True(cache.TryPeek(correlationId: "corr-2", text: out _));
        Assert.True(cache.TryPeek(correlationId: "corr-3", text: out _));
    }

    private static PendingResponseTextCache CreateText(int capacity = 2_000, int maxTextChars = 65_536)
    {
        return new PendingResponseTextCache(Options.Create(new JudgeOptions
        {
            CacheCapacity = capacity,
            CacheTtlSeconds = 300,
            MaxCachedTextChars = maxTextChars
        }));
    }

    private static PendingPromptCache CreatePrompt(int maxTextChars = 65_536)
    {
        return new PendingPromptCache(Options.Create(new JudgeOptions
        {
            CacheCapacity = 2_000,
            CacheTtlSeconds = 300,
            MaxCachedTextChars = maxTextChars
        }));
    }
}
