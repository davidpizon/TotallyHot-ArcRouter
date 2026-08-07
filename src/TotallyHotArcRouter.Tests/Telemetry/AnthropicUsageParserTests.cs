using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="AnthropicUsageParser"/>.</summary>
public class AnthropicUsageParserTests
{
    [Fact]
    public void TryExtractFromNonStreamingBody_ValidUsage_ReturnsTrueWithValues()
    {
        const string json = """{"id":"msg_1","type":"message","usage":{"input_tokens":200,"output_tokens":50}}""";

        var result = AnthropicUsageParser.TryExtractFromNonStreamingBody(json, out var usage);

        Assert.True(result);
        Assert.Equal(200, usage.PromptTokens);
        Assert.Equal(50, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_CacheTokensPresent_PopulatesCacheFieldsAndTotalInputTokens()
    {
        const string json = """
            {"id":"msg_1","type":"message","usage":{"input_tokens":50,"output_tokens":10,"cache_creation_input_tokens":1000,"cache_read_input_tokens":200000}}
            """;

        var result = AnthropicUsageParser.TryExtractFromNonStreamingBody(json, out var usage);

        Assert.True(result);
        Assert.Equal(50, usage.PromptTokens);
        Assert.Equal(10, usage.CompletionTokens);
        Assert.Equal(1000, usage.CacheCreationTokens);
        Assert.Equal(200000, usage.CacheReadTokens);
        Assert.Equal(201050, usage.TotalInputTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_CacheTokensAbsent_DefaultToZero()
    {
        const string json = """{"id":"msg_1","type":"message","usage":{"input_tokens":200,"output_tokens":50}}""";

        var result = AnthropicUsageParser.TryExtractFromNonStreamingBody(json, out var usage);

        Assert.True(result);
        Assert.Equal(0, usage.CacheCreationTokens);
        Assert.Equal(0, usage.CacheReadTokens);
        Assert.Equal(200, usage.TotalInputTokens);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MissingUsage_ReturnsFalse()
    {
        const string json = """{"id":"msg_1","type":"message"}""";

        var result = AnthropicUsageParser.TryExtractFromNonStreamingBody(json, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MalformedJson_ReturnsFalse()
    {
        var result = AnthropicUsageParser.TryExtractFromNonStreamingBody("not json", out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_MessageStartAndDelta_CombinesInputAndFinalOutput()
    {
        var sse =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":150,\"output_tokens\":1}}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"Hi\"}}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":37}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";

        var result = AnthropicUsageParser.TryExtractFromStreamingBuffer(sse, out var usage);

        Assert.True(result);
        Assert.Equal(150, usage.PromptTokens);
        Assert.Equal(37, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_CacheTokensInMessageStart_ArePropagatedWhenDeltaOmitsThem()
    {
        var sse =
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":150,\"output_tokens\":1,\"cache_creation_input_tokens\":500,\"cache_read_input_tokens\":9000}}}\n\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":37}}\n\n";

        var result = AnthropicUsageParser.TryExtractFromStreamingBuffer(sse, out var usage);

        Assert.True(result);
        Assert.Equal(500, usage.CacheCreationTokens);
        Assert.Equal(9000, usage.CacheReadTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_CacheTokensInFinalMessageDelta_WinOverMessageStart()
    {
        // Newer API versions send cumulative full usage (including cache fields) on the final message_delta;
        // those values are final and must win over message_start's initial ones.
        var sse =
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":150,\"output_tokens\":1,\"cache_creation_input_tokens\":500,\"cache_read_input_tokens\":9000}}}\n\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":37,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":9500}}\n\n";

        var result = AnthropicUsageParser.TryExtractFromStreamingBuffer(sse, out var usage);

        Assert.True(result);
        Assert.Equal(0, usage.CacheCreationTokens);
        Assert.Equal(9500, usage.CacheReadTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_MultipleMessageDeltaEvents_TakesTheLastOutputTokens()
    {
        var sse =
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":100,\"output_tokens\":0}}}\n\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":10}}\n\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":25}}\n\n";

        var result = AnthropicUsageParser.TryExtractFromStreamingBuffer(sse, out var usage);

        Assert.True(result);
        Assert.Equal(25, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_MessageStartOnly_ReturnsTrueWithInitialOutputTokens()
    {
        var sse = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":75,\"output_tokens\":0}}}\n\n";

        var result = AnthropicUsageParser.TryExtractFromStreamingBuffer(sse, out var usage);

        Assert.True(result);
        Assert.Equal(75, usage.PromptTokens);
        Assert.Equal(0, usage.CompletionTokens);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_NoMessageStart_ReturnsFalse()
    {
        var sse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"Hi\"}}\n\n";

        var result = AnthropicUsageParser.TryExtractFromStreamingBuffer(sse, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_EmptyBuffer_ReturnsFalse()
    {
        var result = AnthropicUsageParser.TryExtractFromStreamingBuffer(string.Empty, out _);

        Assert.False(result);
    }
}

