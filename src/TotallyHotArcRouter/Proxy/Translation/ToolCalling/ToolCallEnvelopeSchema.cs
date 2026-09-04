using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Builds the JSON Schema that constrained decoding forces a model's reply into, from the <c>tools</c> the
/// client offered - the request half of <see cref="ToolCallDialectRegistry.Constrained"/>.
/// <para>
/// The envelope is deliberately <c>{"content", "tool_calls"}</c> rather than a bare call object, and that
/// shape is the whole reason this approach is safe. A schema describing only a tool call would make
/// <em>not</em> calling a tool unrepresentable, so a grammar-constrained model asked a question needing no
/// tool would be forced to invent one - converting the false-positive risk this workstream already guards
/// against into a certainty. With <c>tool_calls</c> present but allowed to be empty, declining is a legal
/// output. Verified live against <c>qwen2.5-coder-7b-instruct-ghidra-v2</c>: asked for a haiku with two
/// tools offered, it returned prose and <c>"tool_calls": []</c>.
/// </para>
/// <para>
/// <b>
/// Each call is one branch of a <c>oneOf</c>, with its name pinned by <c>const</c> alongside its own
/// argument schema.
/// </b>
/// That is the property delimiter scanning can never have: a hallucinated tool name
/// is not merely detected and rejected after the fact, it is unrepresentable in the grammar the sampler is
/// walking, so the tokens spelling it are never emitted - and neither is a real name paired with the wrong
/// tool's arguments, because the two live in the same branch rather than in sibling properties a grammar
/// has no way to correlate.
/// </para>
/// </summary>
internal static class ToolCallEnvelopeSchema
{
    /// <summary>The schema property holding the model's prose, streamed through as ordinary content.</summary>
    internal const string ContentProperty = "content";

    /// <summary>The schema property holding the calls, lifted into a real OpenAI <c>tool_calls</c> array.</summary>
    internal const string ToolCallsProperty = "tool_calls";

    /// <summary>
    /// Builds the <c>response_format</c> value constraining a reply to calls on
    /// <paramref name="describedTools"/>, or <see langword="null"/> when none of them yielded a usable
    /// signature.
    /// </summary>
    /// <param name="describedTools">
    /// The tools that <see cref="ToolCallInstructionInjector.Inject"/> actually described to the model -
    /// <b>not</b> the client's full <c>tools</c> array. The two differ when the injection budget drops
    /// trailing tools, and constraining to a tool the prompt never described would let the model emit a
    /// call it was told nothing about.
    /// </param>
    /// <returns>
    /// A complete <c>{"type": "json_schema", "json_schema": {…}}</c> object ready to assign, or
    /// <see langword="null"/> if no named function could be read - in which case the caller must leave the
    /// request unconstrained rather than imposing a schema naming no tools, which would forbid every reply
    /// the model could give.
    /// </returns>
    public static JsonObject? TryBuild(JsonArray describedTools)
    {
        ArgumentNullException.ThrowIfNull(describedTools);

        var branches = new JsonArray();
        foreach (var node in describedTools)
        {
            if (node is not JsonObject tool || tool["function"] is not JsonObject function) continue;

            // Read with OpenAI's own field name, not a dialect's: `tools` is what the *client* sent, and the
            // client always speaks OpenAI regardless of which dialect the model answers in.
            if (function["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) ||
                string.IsNullOrEmpty(name))
                continue;

            // One self-contained branch per tool, with the name pinned by `const` *inside* the same object
            // as its own argument schema. This is what actually couples the two. An earlier shape put a
            // shared `name` enum and a `oneOf` of argument schemas side by side as sibling properties, which
            // reads as if it binds them but does not: with no discriminator between siblings, a reply naming
            // one tool while supplying another's arguments satisfies every constraint. Nesting the `const`
            // in the branch makes the pairing part of the grammar rather than something the prompt has to
            // be trusted for.
            branches.Add(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["const"] = name },

                    // A tool with no `parameters` still needs an entry: without one its branch would have no
                    // legal arguments shape at all, making the tool uncallable. An empty object schema is
                    // the correct reading of "takes no arguments".
                    ["arguments"] = function["parameters"] is JsonObject parameters
                        ? JsonSchemaSanitizer.ForConstrainedDecoding(parameters.DeepClone().AsObject())
                        : new JsonObject { ["type"] = "object" }
                },
                ["required"] = new JsonArray { "name", "arguments" }
            });
        }

        if (branches.Count == 0) return null;

        return new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = "arcrouter_tool_call_envelope",
                // Not `strict: true`. Strict mode in OpenAI's dialect additionally demands that every
                // property be required and that additionalProperties be false throughout the *tool's own*
                // parameter schemas - which are the client's, not ours to rewrite. Constraining the envelope
                // without imposing that on arbitrary client schemas is the point.
                ["schema"] = BuildEnvelope(branches)
            }
        };
    }

    /// <summary>
    /// Assembles the outer envelope around the per-tool call branches.
    /// </summary>
    /// <remarks>
    /// Both properties are <c>required</c>, so the model must decide explicitly rather than omitting
    /// <c>tool_calls</c> and leaving "did it decline, or did it forget?" to be guessed at parse time. An
    /// empty array is the unambiguous way to decline.
    /// <para>
    /// Each array item is a <c>oneOf</c> over whole call objects rather than an object with a shared name
    /// enum and a union of argument shapes. Only this form binds a name to its own signature: sibling
    /// properties have no discriminator between them, so the flatter version accepted one tool's name
    /// beside another tool's arguments while reading as though it did not.
    /// </para>
    /// </remarks>
    private static JsonObject BuildEnvelope(JsonArray branches)
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [ContentProperty] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Your reply to the user. Leave empty when calling a tool."
                },
                [ToolCallsProperty] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Tools to invoke. Empty when no tool is needed.",
                    ["items"] = new JsonObject { ["oneOf"] = branches }
                }
            },
            ["required"] = new JsonArray { ContentProperty, ToolCallsProperty }
        };
    }
}