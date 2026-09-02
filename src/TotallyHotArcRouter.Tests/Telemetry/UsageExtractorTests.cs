using System.Text;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="UsageExtractor"/>'s provider dispatch.</summary>
public class UsageExtractorTests
{
    private readonly UsageExtractor _extractor = new();

    [Fact]
    public void TryExtractUsage_OpenAiProvider_NonStreaming_DispatchesToOpenAiParser()
    {
        var body = Encoding.UTF8.GetBytes("""{"usage":{"prompt_tokens":10,"completion_tokens":5}}""");

        var result = _extractor.TryExtractUsage("openai", isStreaming: false, body, out var usage);

        Assert.True(result);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractUsage_AnthropicProvider_NonStreaming_DispatchesToAnthropicParser()
    {
        var body = Encoding.UTF8.GetBytes("""{"usage":{"input_tokens":20,"output_tokens":8}}""");

        var result = _extractor.TryExtractUsage("anthropic", isStreaming: false, body, out var usage);

        Assert.True(result);
        Assert.Equal(20, usage.PromptTokens);
        Assert.Equal(8, usage.CompletionTokens);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OPENAI")]
    [InlineData("openai")]
    public void TryExtractUsage_ProviderKeyIsCaseInsensitive(string providerKey)
    {
        var body = Encoding.UTF8.GetBytes("""{"usage":{"prompt_tokens":1,"completion_tokens":1}}""");

        var result = _extractor.TryExtractUsage(providerKey, isStreaming: false, body, out _);

        Assert.True(result);
    }

    [Theory]
    [InlineData("bedrock-titan")]
    [InlineData("bedrock-llama")]
    [InlineData("bedrock-anthropic")]
    public void TryExtractUsage_BedrockRoutedProvider_NonStreaming_DispatchesToOpenAiParser(string providerKey)
    {
        // All three Bedrock translators (TitanPayloadTranslator, LlamaPayloadTranslator,
        // AnthropicOnBedrockPayloadTranslator) convert AWS's native response into OpenAI's choices[]/usage
        // shape in TranslateResponse, so the bytes reaching this extractor for any of them are OpenAI-shaped
        // - this regression-tests the previously-missing dispatch branch for all three.
        var body = Encoding.UTF8.GetBytes("""{"usage":{"prompt_tokens":7,"completion_tokens":3}}""");

        var result = _extractor.TryExtractUsage(providerKey, isStreaming: false, body, out var usage);

        Assert.True(result);
        Assert.Equal(7, usage.PromptTokens);
        Assert.Equal(3, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractUsage_UnknownProvider_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"usage":{"prompt_tokens":1,"completion_tokens":1}}""");

        var result = _extractor.TryExtractUsage("alibaba", isStreaming: false, body, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractUsage_EmptyBuffer_ReturnsFalse()
    {
        var result = _extractor.TryExtractUsage("openai", isStreaming: false, ReadOnlyMemory<byte>.Empty, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractUsage_StreamingFlag_UsesStreamingParsePath()
    {
        var sse = Encoding.UTF8.GetBytes("data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}\n\ndata: [DONE]\n\n");

        var result = _extractor.TryExtractUsage("openai", isStreaming: true, sse, out var usage);

        Assert.True(result);
        Assert.Equal(3, usage.PromptTokens);
        Assert.Equal(4, usage.CompletionTokens);
    }

    [Theory]
    [InlineData("anthropic", true)]
    [InlineData("Anthropic", true)]
    [InlineData("ANTHROPIC", true)]
    [InlineData("openai", false)]
    [InlineData("gemini", false)]
    [InlineData("ollama", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void SupportsNativeShape_OnlyAnthropicHasARegisteredNativeParser(string? provider, bool expected)
    {
        Assert.Equal(expected, UsageExtractor.SupportsNativeShape(provider!));
    }

    [Fact]
    public void TryExtractUsage_NullProvider_FailsClosedInsteadOfThrowing()
    {
        var body = Encoding.UTF8.GetBytes("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}");

        var result = _extractor.TryExtractUsage(null!, isStreaming: false, body, out var usage);

        Assert.False(result);
        Assert.Equal(default, usage);
    }
}

