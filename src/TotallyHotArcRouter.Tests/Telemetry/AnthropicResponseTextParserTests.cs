using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="AnthropicResponseTextParser"/>.</summary>
public class AnthropicResponseTextParserTests
{
    [Fact]
    public void TryExtractFromNonStreamingBody_ValidContent_ReturnsTrueWithText()
    {
        const string json = """{"id":"msg_1","content":[{"type":"text","text":"Hi there!"}]}""";

        var result = AnthropicResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "Hi there!", actual: text);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MultipleTextBlocks_ConcatenatesWithSpace()
    {
        const string json =
            """{"id":"msg_1","content":[{"type":"text","text":"Hello,"},{"type":"text","text":"world."}]}""";

        var result = AnthropicResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "Hello, world.", actual: text);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_OnlyToolUseBlocks_ReturnsFalse()
    {
        const string json = """{"id":"msg_1","content":[{"type":"tool_use","id":"t1","name":"search"}]}""";

        var result = AnthropicResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MalformedJson_ReturnsFalse()
    {
        var result = AnthropicResponseTextParser.TryExtractFromNonStreamingBody(json: "{ not json", text: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_ConcatenatesTextDeltasInOrder()
    {
        var sse =
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n" +
            "data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\" there\"}}\n\n" +
            "data: {\"type\":\"content_block_stop\"}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";

        var result = AnthropicResponseTextParser.TryExtractFromStreamingBuffer(sseText: sse, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "Hi there", actual: text);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_IgnoresNonTextDeltaTypes()
    {
        // input_json_delta (tool-use argument streaming) must not be treated as reply text.
        var sse =
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"x\\\":\"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"actual reply\"}}\n\n";

        var result = AnthropicResponseTextParser.TryExtractFromStreamingBuffer(sseText: sse, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "actual reply", actual: text);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_NoTextDeltas_ReturnsFalse()
    {
        var sse = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n";

        var result = AnthropicResponseTextParser.TryExtractFromStreamingBuffer(sseText: sse, text: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_EmptyBuffer_ReturnsFalse()
    {
        var result = AnthropicResponseTextParser.TryExtractFromStreamingBuffer(sseText: string.Empty, text: out _);

        Assert.False(result);
    }
}