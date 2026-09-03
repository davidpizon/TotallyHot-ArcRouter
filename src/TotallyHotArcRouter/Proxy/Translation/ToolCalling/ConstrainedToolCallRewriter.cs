using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Rewrites an OpenAI-shaped chat request so the server's own sampler enforces the tool-call format: the
/// <c>tools</c> array becomes a <c>response_format</c> JSON schema, and any tool-calling history becomes
/// plain text.
///
/// <para>
/// The counterpart to <see cref="ToolCallEmulationRewriter"/>, and the reason it exists: emulation
/// <em>asks</em> a model to reply in a syntax, which a model is free to ignore. Measured live against
/// <c>qwen2.5-coder-7b-instruct-ghidra-v2</c>, the identical request produced three different shapes across
/// three runs - a native <c>tool_calls</c> field, a <c>&lt;function-call&gt;</c> wrapper, and bare
/// undelimited JSON. Registering a dialect per observed shape is a race that cannot be won, because the
/// third shape has no framing to register. Constrained decoding removes the choice: the same three runs
/// under a schema produced three structurally identical, parseable replies.
/// </para>
///
/// <para>
/// Pure and static by design, exactly like the emulation rewriter - no I/O, no per-request state - so every
/// branch here is testable against a JSON string with no proxy, no HTTP, and no capability store.
/// </para>
/// </summary>
internal static class ConstrainedToolCallRewriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Rewrites <paramref name="openAiShapedBody"/> to carry a tool-call envelope schema, or returns it
    /// unchanged when it is not a request this can safely constrain.
    /// </summary>
    /// <param name="openAiShapedBody">The request body as the client sent it (with <c>model</c> already rewritten).</param>
    /// <param name="dialect">The constrained dialect, supplying the payload key names history is re-rendered with.</param>
    /// <param name="logger">Logger for the schema-budget warning.</param>
    /// <param name="contextWindow">
    /// The model's probed context window, if known, forwarded to <see cref="ToolCallInstructionInjector.Inject"/>
    /// so the schema budget scales to it instead of the fixed fallback.
    /// </param>
    /// <returns>The rewritten body, or the original bytes when no rewrite was safe.</returns>
    public static byte[] Rewrite(byte[] openAiShapedBody, ToolCallDialect dialect, ILogger logger, ModelContextWindow? contextWindow = null)
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
            // means something upstream changed. Forwarding unchanged is the fail-open choice.
            return openAiShapedBody;
        }

        if (root["messages"] is not JsonArray messages)
        {
            // Not a chat-completions body. Checked before anything is removed, for the same reason emulation
            // checks it: stripping tools without putting a constraint in their place would remove tool
            // calling and give nothing back.
            return openAiShapedBody;
        }

        // The client's own response_format wins, and this is the one case where the caller may not have
        // caught it (ToolCallNormalizerFactory declines to select this path when it sees one, but the guard
        // is repeated here because this type is public to its assembly and independently testable). Two
        // response_format values cannot coexist, and silently overwriting a client's structured-output
        // request would be a worse bug than the unreliable tool calling being fixed.
        if (root["response_format"] is not null)
        {
            return openAiShapedBody;
        }

        // Swapped in wholesale rather than emptied and refilled: a JsonNode has exactly one parent, so
        // replacing the array moves the whole list for free instead of re-cloning every element.
        var rendered = ToolCallHistoryRenderer.Render(messages, dialect, ToolCallHistoryStyle.JsonEnvelope);
        root["messages"] = rendered;

        // A follow-up turn carrying history but no re-offered tools still needs its history re-rendered
        // above - the model must be able to read its own prior turn - but there is nothing to describe or
        // constrain, and a schema naming no tools would forbid every reply it could give.
        if (root["tools"] is JsonArray tools && tools.Count > 0)
        {
            // Instructions first, then a grammar over exactly what the instructions covered. Both halves are
            // load-bearing and neither substitutes for the other: the schema makes a malformed call
            // impossible, while the prompt is the only thing that tells the model the tools exist at all.
            // Constraining to `tools` rather than to what Inject returned would re-introduce the mismatch
            // the budget can create.
            var described = ToolCallInstructionInjector.Inject(rendered, tools, dialect, logger, contextWindow);
            var schema = ToolCallEnvelopeSchema.TryBuild(described);

            if (schema is not null)
            {
                root.Remove("tools");
                root.Remove("tool_choice");
                root.Remove("parallel_tool_calls");
                root["response_format"] = schema;
            }
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(SerializerOptions));
    }
}

