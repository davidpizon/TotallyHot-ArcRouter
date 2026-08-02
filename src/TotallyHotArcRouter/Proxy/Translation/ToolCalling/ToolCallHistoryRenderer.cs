using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// The shape a prior assistant tool call is written back into when a conversation's history is flattened
/// for a model that cannot read OpenAI's native <c>tool_calls</c> field.
/// </summary>
/// <remarks>
/// The choice is not cosmetic: a model reads its own previous turn as an example of what it is supposed to
/// produce now, so history must be rendered in the same form the model is currently being asked for. Using
/// the wrong one teaches it the wrong syntax from its own transcript, which is worse than not re-rendering
/// at all - the instruction and the example would actively disagree.
/// </remarks>
internal enum ToolCallHistoryStyle
{
    /// <summary>
    /// Wrap each call in the dialect's own delimiter pair (e.g. <c>&lt;tool_call&gt;…&lt;/tool_call&gt;</c>),
    /// matching what <see cref="ToolCallEmulationRewriter"/> teaches in the system prompt. Requires the
    /// dialect to declare at least one delimiter.
    /// </summary>
    Delimited,

    /// <summary>
    /// Render the whole assistant turn as the same <c>{"content", "tool_calls"}</c> JSON envelope that
    /// constrained decoding forces the model to emit. Used by
    /// <see cref="ToolCallDialectRegistry.Constrained"/>, which has no delimiters at all.
    /// </summary>
    JsonEnvelope,
}

/// <summary>
/// Flattens a conversation's tool-calling history into plain text a model with no native tool support can
/// read - the multi-turn half of both emulation (<c>docs/router/tool-call-normalization.md</c> Phase 5) and
/// constrained decoding.
///
/// <para>
/// <b>Shared between both rewriters on purpose.</b> Rewriting only the first request works exactly once.
/// The turn after a tool runs, the client sends back an assistant message carrying <c>tool_calls</c> and a
/// <c>role: "tool"</c> result - two shapes such a model has never been trained on, and which most local
/// chat templates either drop or refuse. That problem is identical whether the model was taught a delimiter
/// syntax or constrained by a grammar, and the merge rules below are subtle enough that a second
/// implementation would drift from this one rather than match it.
/// </para>
/// </summary>
internal static class ToolCallHistoryRenderer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns a new message list with every tool-calling message rewritten into plain text the model can
    /// read. The caller swaps it in for the original.
    ///
    /// <para>
    /// Two shapes are converted. An <c>assistant</c> message carrying <c>tool_calls</c> becomes the form
    /// named by <paramref name="style"/>, so its own previous turn reads back in the syntax it is being
    /// asked for. A <c>role: "tool"</c> result becomes a <c>user</c> message - <c>user</c> because it is the
    /// one role every chat template supports, where <c>tool</c> is exactly the role these models' templates
    /// do not know.
    /// </para>
    ///
    /// <para>
    /// Consecutive tool results are merged into one <c>user</c> message. Several parallel calls return
    /// several results in a row, and many local templates require user and assistant turns to alternate -
    /// emitting three consecutive user messages would break rendering on exactly the models this path exists
    /// to serve.
    /// </para>
    /// </summary>
    /// <param name="messages">The client's message array. Never mutated.</param>
    /// <param name="dialect">Supplies the delimiters and payload key names used to write calls back out.</param>
    /// <param name="style">Which form a prior assistant tool call is rendered into.</param>
    public static JsonArray Render(JsonArray messages, ToolCallDialect dialect, ToolCallHistoryStyle style)
    {
        // Maps a tool_call_id back to the function name that produced it, so a result can be labeled with
        // the tool it came from. OpenAI's tool message carries only the id; the name is knowable solely
        // from the assistant turn that requested the call, which is why this is built while walking
        // forward rather than read off the result.
        var nameByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        // Rebuilt rather than edited in place: merging consecutive tool results changes the message count,
        // and mutating a JsonArray while indexing through it is the kind of thing that works until the
        // first conversation with two parallel calls.
        var rewritten = new JsonArray();
        StringBuilder? pendingResults = null;

        foreach (var node in messages)
        {
            if (node is not JsonObject message)
            {
                // Not a message this can rewrite - but dropping it would be a silent content loss, and
                // this type's contract is to forward what it cannot handle rather than delete it. It also
                // changes what upstream sees: a malformed body that a non-rewritten route would forward, and
                // the server would reject, would instead be quietly repaired for a rewritten one.
                //
                // Flushed *before* the node is added, not merely preserved alongside it. Buffered tool
                // results are waiting to become one user message, and carrying them past this point would
                // emit them after a node that came before them - reordering the conversation rather than
                // just tolerating an oddity in it.
                FlushPendingResults(rewritten, ref pendingResults);
                rewritten.Add(node?.DeepClone());
                continue;
            }

            var role = (message["role"] as JsonValue)?.TryGetValue<string>(out var r) == true ? r : null;

            if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                pendingResults ??= new StringBuilder();
                if (pendingResults.Length > 0)
                {
                    pendingResults.Append("\n\n");
                }

                pendingResults.Append(RenderToolResult(message, nameByCallId));
                continue;
            }

            FlushPendingResults(rewritten, ref pendingResults);

            if (string.Equals(role, "assistant", StringComparison.Ordinal) && message["tool_calls"] is JsonArray toolCalls)
            {
                RenderAssistantToolCalls(message, toolCalls, dialect, style, nameByCallId);
            }

            rewritten.Add(message.DeepClone());
        }

        FlushPendingResults(rewritten, ref pendingResults);

        return rewritten;
    }

    /// <summary>Emits any merged tool results as a single <c>user</c> message and resets the buffer.</summary>
    private static void FlushPendingResults(JsonArray rewritten, ref StringBuilder? pendingResults)
    {
        if (pendingResults is null)
        {
            return;
        }

        rewritten.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = pendingResults.ToString(),
        });

        pendingResults = null;
    }

    /// <summary>
    /// Reduces a message's <c>content</c> to the plain text these models can read, whatever shape the client
    /// sent it in.
    /// </summary>
    /// <remarks>
    /// OpenAI's <c>content</c> is either a string or an array of typed parts
    /// (<c>[{"type":"text","text":"…"}, …]</c>), and both shapes reach here - the multi-part form on an
    /// assistant message that also carries <c>tool_calls</c> is the case that prompted this. Text parts are
    /// concatenated, because rendering them as their raw JSON would put protocol noise where the model
    /// expects prose.
    /// <para>
    /// Non-text parts (images, audio) contribute nothing: this path exists for models with no native tool
    /// calling, which have no way to consume them either. Anything else - an unrecognized shape entirely -
    /// falls back to its JSON rather than being discarded, because a model reading something odd is
    /// recoverable and a model reading nothing is the silent drop this whole workstream exists to prevent.
    /// </para>
    /// <para>
    /// Shared by both renderers deliberately. They previously disagreed: the tool-result path wrote
    /// non-string content through as JSON while the assistant path treated it as absent and then
    /// overwrote it, silently losing whatever the assistant had said alongside its call.
    /// </para>
    /// </remarks>
    private static string RenderContentAsText(JsonNode? content)
    {
        switch (content)
        {
            case null:
                return string.Empty;

            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;

            case JsonArray parts:
                var builder = new StringBuilder();
                foreach (var part in parts)
                {
                    if (part is JsonObject obj &&
                        obj["text"] is JsonValue textValue &&
                        textValue.TryGetValue<string>(out var partText) &&
                        !string.IsNullOrEmpty(partText))
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('\n');
                        }

                        builder.Append(partText);
                    }
                }

                // An array carrying no text at all (an image-only turn) serializes rather than vanishing,
                // for the same preserve-over-drop reason as the default case.
                return builder.Length > 0 ? builder.ToString() : parts.ToJsonString(SerializerOptions);

            default:
                return content.ToJsonString(SerializerOptions);
        }
    }

    /// <summary>
    /// Turns one <c>role: "tool"</c> message into a labeled block of text, naming the tool it answers when
    /// the id can be resolved.
    /// </summary>
    private static string RenderToolResult(JsonObject message, Dictionary<string, string> nameByCallId)
    {
        var callId = (message["tool_call_id"] as JsonValue)?.TryGetValue<string>(out var id) == true ? id : null;

        // The message's own `name` is preferred when present (some clients still send it), then the id
        // lookup. An unresolvable id degrades to an unnamed result rather than being dropped: the content
        // is what the model actually needs, and a result with no label still answers the question.
        var name = (message["name"] as JsonValue)?.TryGetValue<string>(out var n) == true && !string.IsNullOrEmpty(n)
            ? n
            : callId is not null && nameByCallId.TryGetValue(callId, out var mapped) ? mapped : null;

        var content = RenderContentAsText(message["content"]);

        return name is null
            ? $"Tool result:\n{content}"
            : $"Tool result for {name}:\n{content}";
    }

    /// <summary>
    /// Replaces an assistant message's <c>tool_calls</c> field with the text form named by
    /// <paramref name="style"/>, carrying over any content the message already had.
    /// </summary>
    private static void RenderAssistantToolCalls(
        JsonObject message,
        JsonArray toolCalls,
        ToolCallDialect dialect,
        ToolCallHistoryStyle style,
        Dictionary<string, string> nameByCallId)
    {
        var existing = RenderContentAsText(message["content"]);
        var payloads = new JsonArray();

        foreach (var node in toolCalls)
        {
            if (node is not JsonObject call || call["function"] is not JsonObject function)
            {
                continue;
            }

            if (function[dialect.NameKey] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            if ((call["id"] as JsonValue)?.TryGetValue<string>(out var callId) == true && !string.IsNullOrEmpty(callId))
            {
                nameByCallId[callId] = name;
            }

            // OpenAI carries arguments as an already-serialized JSON string. Re-parsing it means the model
            // sees a real nested object - the exact shape it was taught to emit - rather than a string full
            // of escaped quotes, which is both harder to read and unlike its own prior output. A value that
            // does not parse is written through as a string so a malformed history is still forwarded.
            var argumentsText = (function[dialect.ArgumentsKey] as JsonValue)?.TryGetValue<string>(out var raw) == true ? raw : null;
            JsonNode? argumentsNode = null;
            if (argumentsText is not null)
            {
                try
                {
                    argumentsNode = JsonNode.Parse(argumentsText);
                }
                catch (JsonException)
                {
                    argumentsNode = JsonValue.Create(argumentsText);
                }
            }

            payloads.Add(new JsonObject
            {
                [dialect.NameKey] = name,
                [dialect.ArgumentsKey] = argumentsNode ?? function[dialect.ArgumentsKey]?.DeepClone() ?? new JsonObject(),
            });
        }

        message["content"] = style switch
        {
            ToolCallHistoryStyle.JsonEnvelope => RenderAsEnvelope(existing, payloads),
            _ => RenderAsDelimited(existing, payloads, dialect),
        };

        message.Remove("tool_calls");
    }

    /// <summary>
    /// Writes the calls as the same <c>{"content", "tool_calls"}</c> envelope constrained decoding forces,
    /// so the model's own prior turn is indistinguishable in shape from what it must produce next.
    /// </summary>
    private static string RenderAsEnvelope(string existing, JsonArray payloads) =>
        new JsonObject
        {
            ["content"] = existing,
            ["tool_calls"] = payloads,
        }.ToJsonString(SerializerOptions);

    /// <summary>
    /// Writes each call wrapped in the dialect's first delimiter pair, appended after any prose the turn
    /// already carried.
    /// </summary>
    private static string RenderAsDelimited(string existing, JsonArray payloads, ToolCallDialect dialect)
    {
        var delimiter = dialect.Delimiters[0];
        var rendered = new StringBuilder(existing);

        foreach (var payload in payloads)
        {
            if (rendered.Length > 0)
            {
                rendered.Append('\n');
            }

            rendered.Append(delimiter.Open).Append(payload!.ToJsonString(SerializerOptions)).Append(delimiter.Close);
        }

        return rendered.ToString();
    }
}

