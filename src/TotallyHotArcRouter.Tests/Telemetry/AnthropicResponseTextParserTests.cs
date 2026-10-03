using System.Text;
using System.Text.Json.Nodes;
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
    public void TryExtractFromNonStreamingBody_MultipleTextBlocks_ConcatenatesVerbatim()
    {
        // #189 D2: blocks are fragments of one reply (citations split a sentence across blocks), so they join
        // with no separator, exactly as the streaming path and AnthropicPayloadTranslator join them, and a
        // whitespace-only block is kept.
        const string json =
            """{"id":"msg_1","content":[{"type":"text","text":"The answer is"},{"type":"text","text":" "},{"type":"text","text":"42."},{"type":"text","text":"\n\n"},{"type":"text","text":"Done."}]}""";

        var result = AnthropicResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "The answer is 42.\n\nDone.", actual: text);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_OnlyWhitespaceTextBlocks_ReturnsFalse()
    {
        const string json = """{"id":"msg_1","content":[{"type":"text","text":"\n"},{"type":"text","text":"  "}]}""";

        var result = AnthropicResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out _);

        Assert.False(result);
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

    [Fact]
    public void TryExtractFromStreamingBuffer_WhitespaceOnlyDeltasAmongText_RoundTripExactly()
    {
        var result = AnthropicResponseTextParser.TryExtractFromStreamingBuffer(
            sseText: BuildSse("Hello", "\n\n", "World", "\n", "    ", "x = 1"), text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "Hello\n\nWorld\n    x = 1", actual: text);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_OnlyWhitespaceDeltas_ReturnsFalse()
    {
        // #189 D1: a blank whole reply is "no text" on every response path, this one included.
        var result = AnthropicResponseTextParser.TryExtractFromStreamingBuffer(sseText: BuildSse("\n", "  "),
            text: out _);

        Assert.False(result);
    }

    /// <summary>
    /// Builds an Anthropic Messages SSE stream: <c>message_start</c> with usage, one <c>text_delta</c> per entry
    /// of <paramref name="deltas"/>, then <c>message_stop</c>. Each event is serialized as JSON, so newlines
    /// inside a delta are escaped exactly as the real upstream escapes them.
    /// </summary>
    internal static string BuildSse(params string[] deltas)
    {
        var builder = new StringBuilder(
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
        foreach (var delta in deltas)
        {
            var evt = new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = delta }
            };
            builder.Append("data: ").Append(evt.ToJsonString()).Append("\n\n");
        }

        return builder.Append("data: {\"type\":\"message_stop\"}\n\n").ToString();
    }
}