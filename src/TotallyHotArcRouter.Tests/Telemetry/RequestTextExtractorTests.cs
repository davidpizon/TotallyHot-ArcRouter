using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="RequestTextExtractor"/>: pulling the newest user message out of a request body.</summary>
public class RequestTextExtractorTests
{
    [Fact]
    public void ExtractNewestUserMessage_NullBody_ReturnsNull()
    {
        Assert.Null(RequestTextExtractor.ExtractNewestUserMessage(null));
    }

    [Fact]
    public void ExtractNewestUserMessage_NoMessagesField_ReturnsNull()
    {
        var body = new JsonObject { ["model"] = "gpt-5.4" };

        Assert.Null(RequestTextExtractor.ExtractNewestUserMessage(body));
    }

    [Fact]
    public void ExtractNewestUserMessage_MessagesNotAnArray_ReturnsNull()
    {
        var body = new JsonObject { ["messages"] = "not an array" };

        Assert.Null(RequestTextExtractor.ExtractNewestUserMessage(body));
    }

    [Fact]
    public void ExtractNewestUserMessage_SingleUserMessage_ReturnsItsContent()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "Hello there" }),
        };

        Assert.Equal("Hello there", RequestTextExtractor.ExtractNewestUserMessage(body));
    }

    [Fact]
    public void ExtractNewestUserMessage_GrowingHistory_ReturnsOnlyTheLastUserMessage()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "You are helpful." },
                new JsonObject { ["role"] = "user", ["content"] = "First question" },
                new JsonObject { ["role"] = "assistant", ["content"] = "First answer" },
                new JsonObject { ["role"] = "user", ["content"] = "Second question" }),
        };

        Assert.Equal("Second question", RequestTextExtractor.ExtractNewestUserMessage(body));
    }

    [Fact]
    public void ExtractNewestUserMessage_TrailingNonUserMessages_StillFindsMostRecentUserMessage()
    {
        // Agentic pattern: user asks, assistant calls a tool, tool result gets appended - the newest
        // user message isn't the last array element.
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "Run the tests" },
                new JsonObject { ["role"] = "assistant", ["content"] = null },
                new JsonObject { ["role"] = "tool", ["content"] = "3 passed, 0 failed" }),
        };

        Assert.Equal("Run the tests", RequestTextExtractor.ExtractNewestUserMessage(body));
    }

    [Fact]
    public void ExtractNewestUserMessage_NoUserMessage_ReturnsNull()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "You are helpful." }),
        };

        Assert.Null(RequestTextExtractor.ExtractNewestUserMessage(body));
    }

    [Fact]
    public void ExtractNewestUserMessage_EmptyOrWhitespaceContent_ReturnsNull()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "   " }),
        };

        Assert.Null(RequestTextExtractor.ExtractNewestUserMessage(body));
    }

    [Fact]
    public void ExtractNewestUserMessage_MultiPartContent_ConcatenatesTextPartsOnly()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "Look at this:" },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:..." } },
                        new JsonObject { ["type"] = "text", ["text"] = "what is it?" }),
                }),
        };

        Assert.Equal("Look at this: what is it?", RequestTextExtractor.ExtractNewestUserMessage(body));
    }

    [Fact]
    public void ExtractNewestUserMessage_MultiPartContentWithNoTextParts_ReturnsNull()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:..." } }),
                }),
        };

        Assert.Null(RequestTextExtractor.ExtractNewestUserMessage(body));
    }
}

