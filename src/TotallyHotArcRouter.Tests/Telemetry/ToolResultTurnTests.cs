using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Issue #304 phase 6: the newest request turn is tool results, or it is not. The check does not
/// read tool-call ids. An older tool result under a new user message is a normal question.
/// </summary>
public class ToolResultTurnTests
{
    [Fact]
    public void NewestTurnIsToolResults_OpenAiTrailingToolRole_IsTrueEvenWhenTheIdMatchesNothing()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":"what is the weather"},
              {"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"get_weather","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"not-c1","content":"72F"}
            ]}
            """) as JsonObject;

        Assert.True(ToolResultTurn.NewestTurnIsToolResults(body));
    }

    [Fact]
    public void NewestTurnIsToolResults_SeveralTrailingToolMessages_IsTrue()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":"look up both"},
              {"role":"tool","tool_call_id":"a","content":"12:00"},
              {"role":"tool","tool_call_id":"b","content":"Tuesday"}
            ]}
            """) as JsonObject;

        Assert.True(ToolResultTurn.NewestTurnIsToolResults(body));
    }

    [Fact]
    public void NewestTurnIsToolResults_LegacyFunctionRole_IsTrue()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":"what is the weather"},
              {"role":"function","name":"get_weather","content":"72F"}
            ]}
            """) as JsonObject;

        Assert.True(ToolResultTurn.NewestTurnIsToolResults(body));
    }

    [Fact]
    public void NewestTurnIsToolResults_AnthropicToolResultBlocksOnly_IsTrueAndIgnoresThePayload()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":"what is the weather"},
              {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"get_weather"}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"other","content":"72F and the tool payload"}]}
            ]}
            """) as JsonObject;

        Assert.True(ToolResultTurn.NewestTurnIsToolResults(body));
    }

    [Fact]
    public void NewestTurnIsToolResults_UserTextBesideAToolResult_IsFalse()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":[
                {"type":"text","text":"also tell me the time"},
                {"type":"tool_result","tool_use_id":"t1","content":"72F"}
              ]}
            ]}
            """) as JsonObject;

        Assert.False(ToolResultTurn.NewestTurnIsToolResults(body));
    }

    [Fact]
    public void NewestTurnIsToolResults_NewUserMessageAboveAnOlderToolResult_IsFalse()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":"what is the weather"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"c1"}]},
              {"role":"tool","tool_call_id":"c1","content":"72F"},
              {"role":"assistant","content":"It is 72F."},
              {"role":"user","content":"now write a poem about rain"}
            ]}
            """) as JsonObject;

        Assert.False(ToolResultTurn.NewestTurnIsToolResults(body));
    }

    [Fact]
    public void NewestTurnIsToolResults_PlainUserText_IsFalse()
    {
        var body = JsonNode.Parse("""
            {"messages":[{"role":"user","content":"what is a mutex"}]}
            """) as JsonObject;

        Assert.False(ToolResultTurn.NewestTurnIsToolResults(body));
    }

    [Fact]
    public void NewestTurnIsToolResults_AssistantToolUseWithNoResult_IsFalse()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":"what is the weather"},
              {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"get_weather"}]}
            ]}
            """) as JsonObject;

        Assert.False(ToolResultTurn.NewestTurnIsToolResults(body));
    }

    [Fact]
    public void NewestTurnIsToolResults_MissingMessages_IsFalse()
    {
        Assert.False(ToolResultTurn.NewestTurnIsToolResults(null));
        Assert.False(ToolResultTurn.NewestTurnIsToolResults(new JsonObject()));
    }
}
