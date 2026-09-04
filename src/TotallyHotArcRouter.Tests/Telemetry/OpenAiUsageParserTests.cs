using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="OpenAiUsageParser"/>.</summary>
public class OpenAiUsageParserTests
{
    [Fact]
    public void TryExtractFromNonStreamingBody_ValidUsage_ReturnsTrueWithValues()
    {
        const string json =
            """{"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":120,"completion_tokens":45,"total_tokens":165}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(120, actual: usage.PromptTokens);
        Assert.Equal(45, actual: usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MissingUsage_ReturnsFalse()
    {
        const string json = """{"id":"chatcmpl-1","choices":[]}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MalformedJson_ReturnsFalse()
    {
        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: "{ not json", usage: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_UsageMissingCompletionTokens_ReturnsFalse()
    {
        const string json = """{"usage":{"prompt_tokens":120}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out _);

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

        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(sseText: sse, usage: out var usage);

        Assert.True(result);
        Assert.Equal(80, actual: usage.PromptTokens);
        Assert.Equal(12, actual: usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_NoUsageAnywhere_ReturnsFalse()
    {
        // The common case: client didn't request stream_options.include_usage, so OpenAI never sends usage.
        var sse =
            "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(sseText: sse, usage: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_MultipleUsageChunks_TakesTheLastOne()
    {
        var sse =
            "data: {\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}\n\n" +
            "data: {\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":99}}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(sseText: sse, usage: out var usage);

        Assert.True(result);
        Assert.Equal(99, actual: usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_EmptyBuffer_ReturnsFalse()
    {
        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(sseText: string.Empty, usage: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_CachedTokens_NormalizedOutOfPromptTokens()
    {
        // OpenAI's cached_tokens is inclusive (a subset of prompt_tokens); UsageInfo is additive, so it
        // must be subtracted back out rather than piled on top - docs/router/openai-format-usage-accuracy-plan.md §6.1.
        const string json =
            """{"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":80}}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(20, actual: usage.PromptTokens);
        Assert.Equal(80, actual: usage.CacheReadTokens);
        Assert.Equal(0, actual: usage.CacheCreationTokens);
        Assert.Equal(100, actual: usage.TotalInputTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_NoPromptTokensDetails_CacheFieldsDefaultToZero()
    {
        const string json = """{"usage":{"prompt_tokens":100,"completion_tokens":20}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(100, actual: usage.PromptTokens);
        Assert.Equal(0, actual: usage.CacheReadTokens);
        Assert.Equal(0, actual: usage.CacheCreationTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_EnrichedTranslatedAnthropicBody_ReadsCacheCreationExtensionField()
    {
        // The shape AnthropicPayloadTranslator.BuildEnrichedUsage emits for an OpenAI-format client
        // routed to Anthropic (docs/router/openai-format-usage-accuracy-plan.md §5.1).
        const string json = """
                            {"usage":{"prompt_tokens":130,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":80},"cache_creation_input_tokens":30,"cache_read_input_tokens":80}}
                            """;

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(20, actual: usage.PromptTokens);
        Assert.Equal(30, actual: usage.CacheCreationTokens);
        Assert.Equal(80, actual: usage.CacheReadTokens);
        Assert.Equal(130, actual: usage.TotalInputTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MalformedCachedExceedsPrompt_ClampsToZeroRatherThanNegative()
    {
        const string json =
            """{"usage":{"prompt_tokens":10,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":999}}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(0, actual: usage.PromptTokens);
        Assert.Equal(999, actual: usage.CacheReadTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_ReasoningTokens_ReadAsSubsetOfCompletionTokens()
    {
        const string json =
            """{"usage":{"prompt_tokens":14,"completion_tokens":678,"completion_tokens_details":{"reasoning_tokens":536}}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(678, actual: usage.CompletionTokens);
        Assert.Equal(536, actual: usage.ReasoningTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_NoCompletionTokensDetails_ReasoningTokensDefaultsToZero()
    {
        const string json = """{"usage":{"prompt_tokens":100,"completion_tokens":20}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(0, actual: usage.ReasoningTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_ReasoningTokensNegative_ClampedToZero()
    {
        const string json =
            """{"usage":{"prompt_tokens":14,"completion_tokens":100,"completion_tokens_details":{"reasoning_tokens":-5}}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(0, actual: usage.ReasoningTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_ReasoningTokensExceedsCompletionTokens_ClampedToCompletionTokens()
    {
        const string json =
            """{"usage":{"prompt_tokens":14,"completion_tokens":100,"completion_tokens_details":{"reasoning_tokens":9999}}}""";

        var result = OpenAiUsageParser.TryExtractFromNonStreamingBody(json: json, usage: out var usage);

        Assert.True(result);
        Assert.Equal(100, actual: usage.CompletionTokens);
        Assert.Equal(100, actual: usage.ReasoningTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_FinalChunkWithReasoningTokens_ReturnsReasoningTokens()
    {
        var sse =
            "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"id\":\"1\",\"choices\":[],\"usage\":{\"prompt_tokens\":80,\"completion_tokens\":300,\"completion_tokens_details\":{\"reasoning_tokens\":220}}}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiUsageParser.TryExtractFromStreamingBuffer(sseText: sse, usage: out var usage);

        Assert.True(result);
        Assert.Equal(300, actual: usage.CompletionTokens);
        Assert.Equal(220, actual: usage.ReasoningTokens);
    }
}