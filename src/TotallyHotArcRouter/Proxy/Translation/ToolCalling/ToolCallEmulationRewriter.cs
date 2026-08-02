using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Rewrites an OpenAI-shaped chat request into one a model with no native tool calling can actually
/// answer: the <c>tools</c> array becomes instructions in the system prompt, and any tool-calling history
/// becomes plain text (<c>docs/router/tool-call-normalization.md</c> Phase 5).
///
/// <para>
/// Pure and static by design - no I/O, no per-request state - so every branch below is testable against a
/// JSON string with no proxy, no HTTP, and no capability store. The stateful half of emulation is the
/// response scan, and that is entirely
/// <see cref="ToolCallNormalizingTranslator"/>'s existing job: emulation teaches the dialect the scanner
/// already reads, so there is no second parser.
/// </para>
///
/// <para>
/// <b>Multi-turn is the part that is easy to get wrong.</b> Rewriting only the first request works exactly
/// once. The turn after a tool runs, the client sends back an assistant message carrying
/// <c>tool_calls</c> and a <c>role: "tool"</c> result - two shapes a model we just told has no native tool
/// support has never been trained on, and which most local chat templates either drop or refuse. Both are
/// re-rendered into the same text syntax the model was taught, so its own history reads back the way it was
/// asked to write it. That work lives in <see cref="ToolCallHistoryRenderer"/>, shared with the constrained
/// path because the problem is identical whether the model was taught a syntax or constrained to one.
/// </para>
/// </summary>
internal static class ToolCallEmulationRewriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Rewrites <paramref name="openAiShapedBody"/> for an emulated model, or returns it unchanged when it
    /// is not a JSON object this can safely touch.
    /// </summary>
    /// <param name="openAiShapedBody">The request body as the client sent it (with <c>model</c> already rewritten).</param>
    /// <param name="dialect">The dialect being taught - supplies both the delimiters and the instruction preamble, so teaching and re-rendering cannot drift apart.</param>
    /// <param name="logger">Logger for the bounded-overhead warning.</param>
    public static byte[] Rewrite(byte[] openAiShapedBody, ToolCallDialect dialect, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(openAiShapedBody);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(logger);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(openAiShapedBody) as JsonObject ?? throw new JsonException("Request body was not a JSON object.");
        }
        catch (JsonException)
        {
            // Defensive: RequestInterceptor already parsed this body to rewrite `model`, so reaching here
            // means something upstream changed. Forwarding unchanged is the fail-open choice - the request
            // may still work, whereas throwing turns a heuristic into an outage.
            return openAiShapedBody;
        }

        if (root["messages"] is not JsonArray messages)
        {
            // Not a chat-completions body, so there is nowhere to put the instructions. Checked *before*
            // anything is removed, because stripping without injecting is the one outcome worse than not
            // rewriting at all: the request would lose tool calling and gain nothing in its place, which
            // is exactly the silent-drop failure this whole workstream exists to prevent. Everything this
            // type knows how to do lives in `messages`; without it the only safe rewrite is none.
            return openAiShapedBody;
        }

        var tools = root["tools"] as JsonArray;

        // Removed unconditionally, not only when instructions were injected. An emulated model's template
        // cannot render these, and some OpenAI-compatible servers reject a tools array for a model they
        // know has no tool support - so leaving them behind risks a 400 on a request that would otherwise
        // have worked. Safe here in a way it was not above: a message list exists, so history is being
        // re-rendered and any offered tools are being taught.
        root.Remove("tools");
        root.Remove("tool_choice");
        root.Remove("parallel_tool_calls");

        // Swapped in wholesale rather than emptied and refilled. A JsonNode has exactly one parent, so
        // copying the rendered nodes back into the original array would mean cloning every one of them a
        // second time purely to re-parent it; replacing the array moves the whole list for free.
        var rendered = ToolCallHistoryRenderer.Render(messages, dialect, ToolCallHistoryStyle.Delimited);
        root["messages"] = rendered;

        // Only teach when there is something to teach. A follow-up turn that carries history but no tools
        // still needs its history re-rendered above - the model must be able to read its own prior turn
        // either way.
        if (tools is { Count: > 0 })
        {
            ToolCallInstructionInjector.Inject(rendered, tools, dialect, logger);
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(SerializerOptions));
    }

}

