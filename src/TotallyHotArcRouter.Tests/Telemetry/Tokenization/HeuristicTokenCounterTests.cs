using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry.Tokenization;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="HeuristicTokenCounter"/>: the character-length floor, its upward rounding, and its
/// refusal to label itself as anything better than <see cref="TokenCountSource.Heuristic"/>.
/// </summary>
public class HeuristicTokenCounterTests
{
    private static readonly ModelKey AnyModel = new(ModelName: "some-unknown-model", Provider: "somewhere");

    [Theory]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("abcdefgh", 2)]
    [InlineData("a", 1)]
    public void TryCountPromptTokens_RoundsUpwards(string text, int expected)
    {
        // Upward: a partial token is still billed as a token, and truncating would bias every counterfactual
        // low - which systematically overstates routing savings.
        var counter = new HeuristicTokenCounter();

        var counted = counter.TryCountPromptTokens(text: text, key: AnyModel, tokens: out var tokens, source: out _);

        Assert.True(counted);
        Assert.Equal(expected: expected, actual: tokens);
    }

    [Fact]
    public void TryCountPromptTokens_AlwaysLabelsItselfHeuristic()
    {
        var counter = new HeuristicTokenCounter();

        counter.TryCountPromptTokens(text: "some prompt text", key: AnyModel, tokens: out _, source: out var source);

        Assert.Equal(expected: TokenCountSource.Heuristic, actual: source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryCountPromptTokens_AbsentText_Refuses(string? text)
    {
        var counter = new HeuristicTokenCounter();

        var counted = counter.TryCountPromptTokens(text: text, key: AnyModel, tokens: out var tokens, source: out var source);

        Assert.False(counted);
        Assert.Equal(expected: 0, actual: tokens);
        Assert.Equal(expected: TokenCountSource.Unavailable, actual: source);
    }
}
