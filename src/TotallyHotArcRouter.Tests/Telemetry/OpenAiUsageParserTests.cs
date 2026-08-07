using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="OpenAiUsageParser"/>.</summary>
public class OpenAiUsageParserTests
{
    [Fact]
    public void TryExtractFromNonStreamingBody_ValidUsage_ReturnsTrueWithValues()
    {
        const string json = """{"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":120,"completion_tokens":45,"total_tokens":165}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json, out var usage);

        Assert.True(result);
        Assert.Equal(120, usage.PromptTokens);
        Assert.Equal(45, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MissingUsage_ReturnsFalse()
    {
        const string json = """{"id":"chatcmpl-1","choices":[]}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MalformedJson_ReturnsFalse()
    {
        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody("{ not json", out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_UsageMissingCompletionTokens_ReturnsFalse()
    {
        const string json = """{"usage":{"prompt_tokens":120}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_FinalChunkWithUsage_ReturnsTrueWithValues()
    {
        var sse =
            "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\n" +
            "data: {\"id\":\"1\",\"choices\":[],\"usage\":{\"prompt_tokens\":80,\"completion_tokens\":12,\"total_tokens\":92}}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(sse, out var usage);

        Assert.True(result);
        Assert.Equal(80, usage.PromptTokens);
        Assert.Equal(12, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_NoUsageAnywhere_ReturnsFalse()
    {
        // The common case: client didn't request stream_options.include_usage, so OpenAI never sends usage.
        var sse =
            "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(sse, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_MultipleUsageChunks_TakesTheLastOne()
    {
        var sse =
            "data: {\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}\n\n" +
            "data: {\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":99}}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(sse, out var usage);

        Assert.True(result);
        Assert.Equal(99, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_EmptyBuffer_ReturnsFalse()
    {
        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(string.Empty, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_CachedTokens_NormalizedOutOfPromptTokens()
    {
        // OpenAI's cached_tokens is inclusive (a subset of prompt_tokens); UsageInfo is additive, so it
        // must be subtracted back out rather than piled on top - docs/router/openai-format-usage-accuracy-plan.md §6.1.
        const string json = """{"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":80}}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json, out var usage);

        Assert.True(result);
        Assert.Equal(20, usage.PromptTokens);
        Assert.Equal(80, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheCreationTokens);
        Assert.Equal(100, usage.TotalInputTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_NoPromptTokensDetails_CacheFieldsDefaultToZero()
    {
        const string json = """{"usage":{"prompt_tokens":100,"completion_tokens":20}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json, out var usage);

        Assert.True(result);
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(0, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheCreationTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_EnrichedTranslatedAnthropicBody_ReadsCacheCreationExtensionField()
    {
        // The shape AnthropicPayloadTranslator.BuildEnrichedUsage emits for an OpenAI-format client
        // routed to Anthropic (docs/router/openai-format-usage-accuracy-plan.md §5.1).
        const string json = """
            {"usage":{"prompt_tokens":130,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":80},"cache_creation_input_tokens":30,"cache_read_input_tokens":80}}
            """;

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json, out var usage);

        Assert.True(result);
        Assert.Equal(20, usage.PromptTokens);
        Assert.Equal(30, usage.CacheCreationTokens);
        Assert.Equal(80, usage.CacheReadTokens);
        Assert.Equal(130, usage.TotalInputTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MalformedCachedExceedsPrompt_ClampsToZeroRatherThanNegative()
    {
        const string json = """{"usage":{"prompt_tokens":10,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":999}}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json, out var usage);

        Assert.True(result);
        Assert.Equal(0, usage.PromptTokens);
        Assert.Equal(999, usage.CacheReadTokens);
    }
}

