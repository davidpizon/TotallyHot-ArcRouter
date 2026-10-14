using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Logging;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="ConversationWindow"/>: the bounded grading context for issue #293.</summary>
public class ConversationWindowTests
{
    [Fact]
    public void Extract_NullBody_ReturnsNull()
    {
        Assert.Null(ConversationWindow.Extract(null));
    }

    [Fact]
    public void Extract_OpenAiShape_KeepsNewestAndEarlierTurns()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "You are helpful." },
                new JsonObject { ["role"] = "user", ["content"] = "First question" },
                new JsonObject { ["role"] = "assistant", ["content"] = "First answer" },
                new JsonObject { ["role"] = "user", ["content"] = "no, the other one" })
        };

        var window = ConversationWindow.Extract(body);

        Assert.NotNull(window);
        Assert.Equal(expected: "no, the other one", actual: window.NewestUserMessage);
        Assert.Equal(expected: 2, actual: window.ContextMessages);
        Assert.Contains("First question", window.FormattedText, StringComparison.Ordinal);
        Assert.Contains("First answer", window.FormattedText, StringComparison.Ordinal);
        Assert.DoesNotContain("You are helpful.", window.FormattedText, StringComparison.Ordinal);
        Assert.False(window.Truncated);
    }

    [Fact]
    public void Extract_AnthropicMultipart_DropsToolResultsAndImages()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "Look at this" },
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject { ["type"] = "base64", ["data"] = "abc" }
                        })
                },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "I see it" },
                        new JsonObject { ["type"] = "tool_use", ["id"] = "1", ["name"] = "run" })
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = "1",
                            ["content"] = "secret tool output"
                        })
                },
                new JsonObject { ["role"] = "user", ["content"] = "now fix the bug" })
        };

        var window = ConversationWindow.Extract(body);

        Assert.NotNull(window);
        Assert.Equal(expected: "now fix the bug", actual: window.NewestUserMessage);
        Assert.Contains("Look at this", window.FormattedText, StringComparison.Ordinal);
        Assert.Contains("I see it", window.FormattedText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret tool output", window.FormattedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_DropsToolRoleMessages()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "Run the tests" },
                new JsonObject { ["role"] = "assistant", ["content"] = "Calling tool" },
                new JsonObject { ["role"] = "tool", ["content"] = "3 passed, 0 failed" },
                new JsonObject { ["role"] = "user", ["content"] = "great" })
        };

        var window = ConversationWindow.Extract(body);

        Assert.NotNull(window);
        Assert.DoesNotContain("3 passed", window.FormattedText, StringComparison.Ordinal);
        Assert.Equal(expected: "great", actual: window.NewestUserMessage);
    }

    [Fact]
    public void Extract_LongCodeInEarlierTurn_IsElided_NewestNeverDropped()
    {
        var longCode = new string('x', 2_500);
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = $"here:\n```\n{longCode}\n```\ndone"
                },
                new JsonObject { ["role"] = "user", ["content"] = "keep this newest instruction" })
        };

        var window = ConversationWindow.Extract(body);

        Assert.NotNull(window);
        Assert.Contains(ConversationWindowOptions.CodeBlockOmittedMarker, window.FormattedText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(longCode, window.FormattedText, StringComparison.Ordinal);
        Assert.Equal(expected: "keep this newest instruction", actual: window.NewestUserMessage);
        Assert.True(window.Truncated);
    }

    [Fact]
    public void Extract_LongNewestMessage_HeadAndTailCutKeepsFinalInstruction()
    {
        // Use plain words, not long high-entropy runs: SecretObscurer treats long base64-ish spans as keys.
        var prefix = string.Concat(Enumerable.Repeat("alpha ", 1_500));
        var middle = string.Concat(Enumerable.Repeat("bravo ", 1_500));
        const string suffix = "FINAL_INSTRUCTION_AT_END";
        var newest = prefix + middle + suffix;
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = newest })
        };

        var options = new ConversationWindowOptions { MaxNewestUserMessageChars = 200 };
        var window = ConversationWindow.Extract(requestBody: body, options: options);

        Assert.NotNull(window);
        Assert.Contains(ConversationWindowOptions.TruncationMarker.Trim(), window.NewestUserMessage,
            StringComparison.Ordinal);
        Assert.Contains("FINAL_INSTRUCTION_AT_END", window.NewestUserMessage, StringComparison.Ordinal);
        Assert.Contains("alpha", window.NewestUserMessage, StringComparison.Ordinal);
        Assert.True(window.Truncated);
        Assert.True(window.NewestUserMessage.Length <= 200);
    }

    [Fact]
    public void Extract_RespectsTotalBudget()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = new string('a', 400) },
                new JsonObject { ["role"] = "assistant", ["content"] = new string('b', 400) },
                new JsonObject { ["role"] = "user", ["content"] = new string('c', 400) },
                new JsonObject { ["role"] = "assistant", ["content"] = new string('d', 400) },
                new JsonObject { ["role"] = "user", ["content"] = "newest" })
        };

        var options = new ConversationWindowOptions
        {
            MaxGradingInputChars = 300,
            MaxNewestUserMessageChars = 300,
            MaxEarlierContextMessages = 6
        };
        var window = ConversationWindow.Extract(requestBody: body, options: options);

        Assert.NotNull(window);
        Assert.True(window.FormattedText.Length <= 300);
        Assert.Contains("newest", window.FormattedText, StringComparison.Ordinal);
        Assert.True(window.Truncated);
    }

    [Fact]
    public void Extract_ObscuresSecrets()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "use key sk-abcdefghijklmnopqrstuvwxyz0123456789"
                })
        };

        var window = ConversationWindow.Extract(body);

        Assert.NotNull(window);
        Assert.Contains(SecretObscurer.RedactedToken, window.FormattedText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz0123456789", window.FormattedText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_CapsEarlierAssistantMessage()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "assistant", ["content"] = new string('z', 800) },
                new JsonObject { ["role"] = "user", ["content"] = "ok" })
        };

        var options = new ConversationWindowOptions { MaxEarlierAssistantChars = 50 };
        var window = ConversationWindow.Extract(requestBody: body, options: options);

        Assert.NotNull(window);
        Assert.True(window.Truncated);
        Assert.DoesNotContain(new string('z', 51), window.FormattedText, StringComparison.Ordinal);
    }
}
