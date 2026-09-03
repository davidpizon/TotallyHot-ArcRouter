using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Covers <see cref="ToolCallHistoryRenderer"/>'s read side directly: the incoming message history is
/// always OpenAI-shaped (<c>name</c>/<c>arguments</c>), regardless of which dialect wrote it back out on
/// the *write* side (docs/router/backlog.md item 3).
/// </summary>
public class ToolCallHistoryRendererTests
{
    [Fact]
    public void ADialectWithADifferingArgumentsKey_StillReadsThePriorTurnsArguments()
    {
        // Llama3Json writes "parameters" on the way out, but the incoming history it must read back is
        // still OpenAI-shaped ("arguments") - the client speaks OpenAI regardless of the model's dialect.
        // Reading with dialect.ArgumentsKey here used to look up a "parameters" key that is never present
        // on real history, silently emitting an empty arguments object.
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "weather?" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_1",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "get_weather",
                            ["arguments"] = """{"city":"Paris"}"""
                        }
                    }
                }
            },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_1", ["content"] = "17C" }
        };

        var rendered = ToolCallHistoryRenderer.Render(messages: messages, dialect: ToolCallDialectRegistry.Llama3Json,
            style: ToolCallHistoryStyle.Delimited);

        var assistant = Assert.IsType<JsonObject>(rendered[1]);
        var content = assistant["content"]!.GetValue<string>();

        Assert.Contains(expectedSubstring: "get_weather", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Paris", actualString: content, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "\"parameters\":{\"city\":\"Paris\"}", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ToolCallDialectRegistry.Emulated))]
    [InlineData(nameof(ToolCallDialectRegistry.Constrained))]
    public void EmulatedAndConstrained_RoundTripUnchanged(string dialectName)
    {
        // Both in-use emulation-prompt dialects key arguments as "arguments" on both read and write, so
        // the fix must be behavior-preserving for them: read and write happened to agree by coincidence
        // before, and must still agree now that read is fixed to the OpenAI names.
        var dialect = dialectName == nameof(ToolCallDialectRegistry.Emulated)
            ? ToolCallDialectRegistry.Emulated
            : ToolCallDialectRegistry.Constrained;

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_1",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "get_weather",
                            ["arguments"] = """{"city":"Paris"}"""
                        }
                    }
                }
            }
        };

        var style = dialect == ToolCallDialectRegistry.Constrained
            ? ToolCallHistoryStyle.JsonEnvelope
            : ToolCallHistoryStyle.Delimited;

        var rendered = ToolCallHistoryRenderer.Render(messages: messages, dialect: dialect, style: style);

        var content = Assert.IsType<JsonObject>(rendered[0])["content"]!.GetValue<string>();

        Assert.Contains(expectedSubstring: "get_weather", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "\"arguments\":{\"city\":\"Paris\"}", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }
}