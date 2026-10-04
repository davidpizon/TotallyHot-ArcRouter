using System.Text;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="OpenAiResponseTextParser"/>.</summary>
public class OpenAiResponseTextParserTests
{
    [Fact]
    public void TryExtractFromNonStreamingBody_ValidMessage_ReturnsTrueWithText()
    {
        const string json =
            """{"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"Hi there!"}}]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "Hi there!", actual: text);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_EmptyChoices_ReturnsFalse()
    {
        const string json = """{"id":"chatcmpl-1","choices":[]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MissingMessage_ReturnsFalse()
    {
        const string json = """{"choices":[{}]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MalformedJson_ReturnsFalse()
    {
        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json: "{ not json", text: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_ConcatenatesDeltasInOrder()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiResponseTextParser.TryExtractFromStreamingBuffer(sseText: sse, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "Hi there!", actual: text);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_NoContentDeltas_ReturnsFalse()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiResponseTextParser.TryExtractFromStreamingBuffer(sseText: sse, text: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_EmptyBuffer_ReturnsFalse()
    {
        var result = OpenAiResponseTextParser.TryExtractFromStreamingBuffer(sseText: string.Empty, text: out _);

        Assert.False(result);
    }

    // -- #189: whitespace-only deltas are reply content, not "no text" ----------------------------------

    /// <summary>
    /// Delta sequences whose whitespace-only chunks (paragraph breaks, indentation, the space between two
    /// words, tabs, CRLF) must survive extraction byte for byte.
    /// </summary>
    public static TheoryData<string[], string> WhitespaceDeltaSequences => new()
    {
        { ["Hello", "\n\n", "World", "\n", "    ", "x = 1"], "Hello\n\nWorld\n    x = 1" },
        { ["The", " ", "answer"], "The answer" },
        {
            ["```python", "\n", "def f():", "\n", "    ", "return 1", "\n", "```"],
            "```python\ndef f():\n    return 1\n```"
        },
        { ["a", "\t", "b", "\r\n", "c"], "a\tb\r\nc" }
    };

    [Theory]
    [MemberData(nameof(WhitespaceDeltaSequences))]
    public void TryExtractFromStreamingBuffer_WhitespaceOnlyDeltas_RoundTripExactly(string[] deltas, string expected)
    {
        var result = OpenAiResponseTextParser.TryExtractFromStreamingBuffer(sseText: BuildSse(deltas), text: out var text);

        Assert.True(result);
        Assert.Equal(expected: expected, actual: text);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_OnlyWhitespaceDeltas_ReturnsFalse()
    {
        // A blank whole reply (e.g. a "\n" streamed ahead of a tool call) is still "no text" (#189 D1).
        var result = OpenAiResponseTextParser.TryExtractFromStreamingBuffer(sseText: BuildSse("\n", "  ", "\n\n"),
            text: out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_ArrayContent_ConcatenatesPartsVerbatim()
    {
        // Some OpenAI-compatible servers return content as parts. They are fragments of one reply, so they
        // join with no separator and a whitespace-only part is kept.
        const string json =
            """{"choices":[{"message":{"role":"assistant","content":[{"type":"text","text":"Line one"},{"type":"text","text":"\n\n"},{"type":"text","text":"Line two"}]}}]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "Line one\n\nLine two", actual: text);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_StringWithInteriorWhitespace_ReturnsItUnchanged()
    {
        const string json =
            """{"choices":[{"message":{"role":"assistant","content":"Hello\n\nWorld\n    x = 1"}}]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out var text);

        Assert.True(result);
        Assert.Equal(expected: "Hello\n\nWorld\n    x = 1", actual: text);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_WhitespaceOnlyContent_ReturnsFalse()
    {
        const string json = """{"choices":[{"message":{"role":"assistant","content":" \n "}}]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json: json, text: out _);

        Assert.False(result);
    }

    /// <summary>
    /// Builds an OpenAI chat-completion SSE stream: a role-only delta, one content delta per entry of
    /// <paramref name="deltas"/>, then <c>[DONE]</c>. Each event is serialized as JSON, so newlines and tabs
    /// inside a delta are escaped exactly as a real upstream escapes them.
    /// </summary>
    internal static string BuildSse(params string[] deltas)
    {
        var builder = new StringBuilder("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n");
        foreach (var delta in deltas)
        {
            var evt = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = delta } })
            };
            builder.Append("data: ").Append(evt.ToJsonString()).Append("\n\n");
        }

        return builder.Append("data: [DONE]\n\n").ToString();
    }
}