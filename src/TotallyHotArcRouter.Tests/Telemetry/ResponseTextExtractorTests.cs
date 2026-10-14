using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="ResponseTextExtractor"/>'s provider dispatch.</summary>
public class ResponseTextExtractorTests
{
    private readonly ResponseTextExtractor _extractor = new();

    [Fact]
    public void TryExtractText_OpenAiProvider_NonStreaming_DispatchesToOpenAiParser()
    {
        var body = """{"choices":[{"message":{"content":"Hi!"}}]}"""u8.ToArray();

        var result =
            _extractor.TryExtractText(provider: "openai", false, bufferedResponseBody: body, text: out var text, hasToolCalls: out _);

        Assert.True(result);
        Assert.Equal(expected: "Hi!", actual: text);
    }

    [Fact]
    public void TryExtractText_AnthropicProvider_NonStreaming_DispatchesToAnthropicParser()
    {
        var body = """{"content":[{"type":"text","text":"Hi!"}]}"""u8.ToArray();

        var result = _extractor.TryExtractText(provider: "anthropic", false, bufferedResponseBody: body,
            text: out var text, hasToolCalls: out _);

        Assert.True(result);
        Assert.Equal(expected: "Hi!", actual: text);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OPENAI")]
    [InlineData("openai")]
    public void TryExtractText_ProviderKeyIsCaseInsensitive(string providerKey)
    {
        var body = """{"choices":[{"message":{"content":"hi"}}]}"""u8.ToArray();

        var result = _extractor.TryExtractText(provider: providerKey, false, bufferedResponseBody: body, text: out _, hasToolCalls: out _);

        Assert.True(result);
    }

    [Fact]
    public void TryExtractText_OllamaProvider_NonStreaming_DispatchesToOpenAiParser()
    {
        // Regression test: this branch previously omitted "ollama" even though IUsageExtractor's
        // equivalent branch already included it, so response-text extraction silently failed for every
        // Ollama request despite Ollama's OpenAI-compatible routes answering in OpenAI's own
        // choices[].message shape with no translator in front of them.
        var body = """{"choices":[{"message":{"content":"Hi from Ollama!"}}]}"""u8.ToArray();

        var result =
            _extractor.TryExtractText(provider: "ollama", false, bufferedResponseBody: body, text: out var text, hasToolCalls: out _);

        Assert.True(result);
        Assert.Equal(expected: "Hi from Ollama!", actual: text);
    }

    [Theory]
    [InlineData("bedrock-titan")]
    [InlineData("bedrock-llama")]
    [InlineData("bedrock-anthropic")]
    public void TryExtractText_BedrockRoutedProvider_NonStreaming_DispatchesToOpenAiParser(string providerKey)
    {
        var body = """{"choices":[{"message":{"content":"Hi from Bedrock!"}}]}"""u8.ToArray();

        var result = _extractor.TryExtractText(provider: providerKey, false, bufferedResponseBody: body,
            text: out var text, hasToolCalls: out _);

        Assert.True(result);
        Assert.Equal(expected: "Hi from Bedrock!", actual: text);
    }

    [Fact]
    public void TryExtractText_UnknownProvider_ReturnsFalseWithHasToolCallsNull()
    {
        var body = """{"choices":[{"message":{"content":"hi"}}]}"""u8.ToArray();

        var result = _extractor.TryExtractText(provider: "alibaba", false, bufferedResponseBody: body, text: out _,
            hasToolCalls: out var hasToolCalls);

        Assert.False(result);
        Assert.Null(hasToolCalls);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("ollama")]
    [InlineData("gemini")]
    [InlineData("bedrock-titan")]
    [InlineData("bedrock-llama")]
    [InlineData("bedrock-anthropic")]
    public void TryExtractText_RegisteredOpenAiShapeProviders_ToolCallsOnly_ReportsTrue(string providerKey)
    {
        var body =
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"search","arguments":"{}"}}]}}]}"""u8
                .ToArray();

        var result = _extractor.TryExtractText(provider: providerKey, false, bufferedResponseBody: body, text: out _,
            hasToolCalls: out var hasToolCalls);

        Assert.False(result);
        Assert.True(hasToolCalls);
    }

    [Fact]
    public void TryExtractText_Anthropic_ToolUseOnly_ReportsTrue()
    {
        var body = """{"content":[{"type":"tool_use","id":"t1","name":"search"}]}"""u8.ToArray();

        var result = _extractor.TryExtractText(provider: "anthropic", false, bufferedResponseBody: body, text: out _,
            hasToolCalls: out var hasToolCalls);

        Assert.False(result);
        Assert.True(hasToolCalls);
    }

    [Fact]
    public void TryExtractText_EmptyBuffer_ReturnsFalse()
    {
        var result = _extractor.TryExtractText(provider: "openai", false,
            bufferedResponseBody: ReadOnlyMemory<byte>.Empty, text: out _, hasToolCalls: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractText_StreamingFlag_UsesStreamingParsePath()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n"u8.ToArray();

        var result = _extractor.TryExtractText(provider: "openai", true, bufferedResponseBody: sse, text: out var text, hasToolCalls: out _);

        Assert.True(result);
        Assert.Equal(expected: "hi", actual: text);
    }

    [Fact]
    public void TryExtractText_NullProvider_FailsClosedInsteadOfThrowing()
    {
        var body = "{\"choices\":[{\"message\":{\"content\":\"hi\"}}]}"u8.ToArray();

        var result = _extractor.TryExtractText(provider: null!, false, bufferedResponseBody: body, text: out var text, hasToolCalls: out _);

        Assert.False(result);
        Assert.Equal(expected: string.Empty, actual: text);
    }
}