using System.Text;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="ResponseTextExtractor"/>'s provider dispatch.</summary>
public class ResponseTextExtractorTests
{
    private readonly ResponseTextExtractor _extractor = new();

    [Fact]
    public void TryExtractText_OpenAiProvider_NonStreaming_DispatchesToOpenAiParser()
    {
        var body = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"Hi!"}}]}""");

        var result = _extractor.TryExtractText("openai", isStreaming: false, body, out var text);

        Assert.True(result);
        Assert.Equal("Hi!", text);
    }

    [Fact]
    public void TryExtractText_AnthropicProvider_NonStreaming_DispatchesToAnthropicParser()
    {
        var body = Encoding.UTF8.GetBytes("""{"content":[{"type":"text","text":"Hi!"}]}""");

        var result = _extractor.TryExtractText("anthropic", isStreaming: false, body, out var text);

        Assert.True(result);
        Assert.Equal("Hi!", text);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OPENAI")]
    [InlineData("openai")]
    public void TryExtractText_ProviderKeyIsCaseInsensitive(string providerKey)
    {
        var body = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"hi"}}]}""");

        var result = _extractor.TryExtractText(providerKey, isStreaming: false, body, out _);

        Assert.True(result);
    }

    [Fact]
    public void TryExtractText_OllamaProvider_NonStreaming_DispatchesToOpenAiParser()
    {
        // Regression test: this branch previously omitted "ollama" even though IUsageExtractor's
        // equivalent branch already included it, so response-text extraction silently failed for every
        // Ollama request despite Ollama's OpenAI-compatible routes answering in OpenAI's own
        // choices[].message shape with no translator in front of them.
        var body = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"Hi from Ollama!"}}]}""");

        var result = _extractor.TryExtractText("ollama", isStreaming: false, body, out var text);

        Assert.True(result);
        Assert.Equal("Hi from Ollama!", text);
    }

    [Theory]
    [InlineData("bedrock-titan")]
    [InlineData("bedrock-llama")]
    [InlineData("bedrock-anthropic")]
    public void TryExtractText_BedrockRoutedProvider_NonStreaming_DispatchesToOpenAiParser(string providerKey)
    {
        var body = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"Hi from Bedrock!"}}]}""");

        var result = _extractor.TryExtractText(providerKey, isStreaming: false, body, out var text);

        Assert.True(result);
        Assert.Equal("Hi from Bedrock!", text);
    }

    [Fact]
    public void TryExtractText_UnknownProvider_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"hi"}}]}""");

        var result = _extractor.TryExtractText("alibaba", isStreaming: false, body, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractText_EmptyBuffer_ReturnsFalse()
    {
        var result = _extractor.TryExtractText("openai", isStreaming: false, ReadOnlyMemory<byte>.Empty, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractText_StreamingFlag_UsesStreamingParsePath()
    {
        var sse = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n");

        var result = _extractor.TryExtractText("openai", isStreaming: true, sse, out var text);

        Assert.True(result);
        Assert.Equal("hi", text);
    }
}

