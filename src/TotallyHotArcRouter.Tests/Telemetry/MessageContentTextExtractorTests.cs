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

        Assert.Equal("hello", MessageContentTextExtractor.ExtractText(content));
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

        Assert.Equal("hello world", MessageContentTextExtractor.ExtractText(content));
    }

    [Fact]
    public void ExtractText_ArrayMixedWithNonTextParts_SkipsNonTextParts()
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "hello" },
            new JsonObject { ["type"] = "tool_use", ["id"] = "t1" },
            new JsonObject { ["type"] = "text", ["text"] = "world" });

        Assert.Equal("hello world", MessageContentTextExtractor.ExtractText(content));
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
}

