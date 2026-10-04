using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="MessageContentTextExtractor"/>: shared string-or-parts content extraction.</summary>
public class MessageContentTextExtractorTests
{
    [Fact]
    public void ExtractText_Null_ReturnsNull()
    {
        Assert.Null(MessageContentTextExtractor.ExtractText(null));
    }

    [Fact]
    public void ExtractText_PlainString_ReturnsIt()
    {
        JsonNode content = "hello";

        Assert.Equal(expected: "hello", actual: MessageContentTextExtractor.ExtractText(content));
    }

    [Fact]
    public void ExtractText_WhitespaceString_ReturnsNull()
    {
        JsonNode content = "   ";

        Assert.Null(MessageContentTextExtractor.ExtractText(content));
    }

    [Fact]
    public void ExtractText_ArrayOfTextParts_ConcatenatesWithSpace()
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "hello" },
            new JsonObject { ["type"] = "text", ["text"] = "world" });

        Assert.Equal(expected: "hello world", actual: MessageContentTextExtractor.ExtractText(content));
    }

    [Fact]
    public void ExtractText_ArrayMixedWithNonTextParts_SkipsNonTextParts()
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "hello" },
            new JsonObject { ["type"] = "tool_use", ["id"] = "t1" },
            new JsonObject { ["type"] = "text", ["text"] = "world" });

        Assert.Equal(expected: "hello world", actual: MessageContentTextExtractor.ExtractText(content));
    }

    [Fact]
    public void ExtractText_ArrayWithNoTextParts_ReturnsNull()
    {
        var content = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "t1" });

        Assert.Null(MessageContentTextExtractor.ExtractText(content));
    }

    [Fact]
    public void ExtractText_EmptyArray_ReturnsNull()
    {
        Assert.Null(MessageContentTextExtractor.ExtractText(new JsonArray()));
    }

    [Fact]
    public void ExtractText_NeitherStringNorArray_ReturnsNull()
    {
        JsonNode content = 42;

        Assert.Null(MessageContentTextExtractor.ExtractText(content));
    }

    // -- ExtractVerbatimText: the reply-side contract (#189) --------------------------------------------

    [Theory]
    [InlineData("hello")]
    [InlineData("\n\n")]
    [InlineData("    ")]
    [InlineData("")]
    public void ExtractVerbatimText_String_ReturnsItUnchanged(string value)
    {
        JsonNode content = value;

        Assert.Equal(expected: value, actual: MessageContentTextExtractor.ExtractVerbatimText(content));
    }

    [Fact]
    public void ExtractVerbatimText_ArrayWithWhitespaceOnlyParts_ConcatenatesWithNoSeparator()
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "hello" },
            new JsonObject { ["type"] = "text", ["text"] = "\n\n" },
            new JsonObject { ["type"] = "text", ["text"] = "    world" });

        Assert.Equal(expected: "hello\n\n    world", actual: MessageContentTextExtractor.ExtractVerbatimText(content));
    }

    [Fact]
    public void ExtractVerbatimText_ArrayMixedWithNonTextParts_SkipsNonTextParts()
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "hello " },
            new JsonObject { ["type"] = "tool_use", ["id"] = "t1" },
            new JsonObject { ["type"] = 7, ["text"] = "not a text part" },
            new JsonObject { ["type"] = "text", ["text"] = 42 },
            new JsonObject { ["type"] = "text", ["text"] = "world" });

        Assert.Equal(expected: "hello world", actual: MessageContentTextExtractor.ExtractVerbatimText(content));
    }

    [Fact]
    public void ExtractVerbatimText_ArrayWithNoTextParts_ReturnsNull()
    {
        var content = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "t1" });

        Assert.Null(MessageContentTextExtractor.ExtractVerbatimText(content));
    }

    [Fact]
    public void ExtractVerbatimText_Null_ReturnsNull()
    {
        Assert.Null(MessageContentTextExtractor.ExtractVerbatimText(null));
    }

    [Fact]
    public void ExtractVerbatimText_NeitherStringNorArray_ReturnsNull()
    {
        JsonNode content = 42;

        Assert.Null(MessageContentTextExtractor.ExtractVerbatimText(content));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" \n\t ", false)]
    [InlineData(" x ", true)]
    public void HasReplyText_TreatsOnlyNonBlankRepliesAsText(string? reply, bool expected)
    {
        Assert.Equal(expected: expected, actual: MessageContentTextExtractor.HasReplyText(reply));
    }
}