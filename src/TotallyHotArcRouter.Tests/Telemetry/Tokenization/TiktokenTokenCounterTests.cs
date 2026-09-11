using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry.Tokenization;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="TiktokenTokenCounter"/>: encoding selection per model family, and the counting
/// contract (<see cref="TokenCountSource.LocalProxy"/> on success, refusal on absent text).
/// </summary>
public class TiktokenTokenCounterTests
{
    [Theory]
    [InlineData("gpt-4o", "openai")]
    [InlineData("gpt-4o-2024-08-06", "openai")]
    [InlineData("gpt-5", "openai")]
    public void ResolveEncodingName_GptFourOAndLaterFamilies_SelectO200k(string model, string provider)
    {
        var encoding = TiktokenTokenCounter.ResolveEncodingName(new ModelKey(ModelName: model, Provider: provider));

        Assert.Equal(expected: TiktokenTokenCounter.O200KBaseEncoding, actual: encoding);
    }

    [Theory]
    [InlineData("gpt-4", "openai")]
    [InlineData("gpt-3.5-turbo", "openai")]
    [InlineData("claude-opus-5", "anthropic")]
    [InlineData("gemini-2.5-pro", "gemini")]
    public void ResolveEncodingName_EverythingElse_FallsBackToCl100k(string model, string provider)
    {
        // For the non-OpenAI vendors this is explicitly a proxy encoding, not their real tokenizer - the
        // bias it carries is what CalibratedTokenCounter exists to correct.
        var encoding = TiktokenTokenCounter.ResolveEncodingName(new ModelKey(ModelName: model, Provider: provider));

        Assert.Equal(expected: TiktokenTokenCounter.Cl100KBaseEncoding, actual: encoding);
    }

    [Fact]
    public void ResolveEncodingName_BlankModelName_IsNull()
    {
        var encoding = TiktokenTokenCounter.ResolveEncodingName(new ModelKey(ModelName: "  ", Provider: "openai"));

        Assert.Null(encoding);
    }

    [Fact]
    public void TryCountPromptTokens_RealText_ReturnsPositiveCountLabelledWithItsProvenance()
    {
        var counter = new TiktokenTokenCounter();

        var counted = counter.TryCountPromptTokens(
            text: "The quick brown fox jumps over the lazy dog.",
            key: new ModelKey(ModelName: "gpt-4", Provider: "openai"),
            tokens: out var tokens,
            source: out var source);

        Assert.True(counted);
        Assert.True(tokens > 0);
        // gpt-4's real tokenizer *is* cl100k_base, so this count is native rather than a stand-in.
        Assert.Equal(expected: TokenCountSource.LocalNative, actual: source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCountPromptTokens_AbsentText_RefusesRatherThanReturningZero(string? text)
    {
        // Absent is not zero: returning a count of 0 would let a caller price an uncharacterized request
        // as costing nothing, which is the exact conflation TokenCountSource.Unavailable exists to prevent.
        var counter = new TiktokenTokenCounter();

        var counted = counter.TryCountPromptTokens(
            text: text,
            key: new ModelKey(ModelName: "gpt-4", Provider: "openai"),
            tokens: out var tokens,
            source: out var source);

        Assert.False(counted);
        Assert.Equal(expected: 0, actual: tokens);
        Assert.Equal(expected: TokenCountSource.Unavailable, actual: source);
    }

    [Theory]
    [InlineData("gpt-4", "openai")]
    [InlineData("gpt-4o", "openai")]
    public void TryCountPromptTokens_ModelWhoseEncodingThisActuallyIs_ReportsNative(string model, string provider)
    {
        var counter = new TiktokenTokenCounter();

        counter.TryCountPromptTokens(text: "some prompt",
            key: new ModelKey(ModelName: model, Provider: provider), tokens: out _, source: out var source);

        Assert.Equal(expected: TokenCountSource.LocalNative, actual: source);
    }

    [Theory]
    [InlineData("claude-opus-5", "anthropic")]
    [InlineData("gemini-2.5-pro", "gemini")]
    [InlineData("mistral-large", "mistral")]
    public void TryCountPromptTokens_ModelUsingAStandInEncoding_ReportsProxy(string model, string provider)
    {
        // These vendors have their own tokenizers; cl100k_base is standing in. Saying so is what stops a
        // ratio between two of them from being mistaken for a measurement.
        var counter = new TiktokenTokenCounter();

        counter.TryCountPromptTokens(text: "some prompt",
            key: new ModelKey(ModelName: model, Provider: provider), tokens: out _, source: out var source);

        Assert.Equal(expected: TokenCountSource.LocalProxy, actual: source);
    }

    [Fact]
    public void TryCountPromptTokens_SameTextTwice_IsDeterministic()
    {
        var counter = new TiktokenTokenCounter();
        var key = new ModelKey(ModelName: "gpt-4", Provider: "openai");
        const string text = "Determinism matters: the ROI chart must not move when nothing changed.";

        counter.TryCountPromptTokens(text: text, key: key, tokens: out var first, source: out _);
        counter.TryCountPromptTokens(text: text, key: key, tokens: out var second, source: out _);

        Assert.Equal(expected: first, actual: second);
    }

    [Fact]
    public void TryCountPromptTokens_LongerText_CountsMoreTokens()
    {
        // The property the whole plan turns on: a bigger prompt must produce a bigger number. The previous
        // global-average estimator could not do this at all.
        var counter = new TiktokenTokenCounter();
        var key = new ModelKey(ModelName: "claude-opus-5", Provider: "anthropic");

        counter.TryCountPromptTokens(text: "short", key: key, tokens: out var small, source: out _);
        counter.TryCountPromptTokens(text: string.Join(separator: " ", values: Enumerable.Repeat(element: "considerably longer prompt", count: 200)),
            key: key, tokens: out var large, source: out _);

        Assert.True(large > small * 10);
    }
}
