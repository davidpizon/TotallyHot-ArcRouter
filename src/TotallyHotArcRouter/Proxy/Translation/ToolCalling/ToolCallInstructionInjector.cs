using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Puts a dialect's tool-calling instructions, and the request's tool schemas, into the system prompt -
/// the half of tool-call rewriting that tells a model <em>what tools exist and what they do</em>.
///
/// <para>
/// <b>Constrained decoding needs this just as much as emulation does</b>, which is why it is shared rather
/// than private to <see cref="ToolCallEmulationRewriter"/>. A <c>response_format</c> schema is compiled into
/// a sampler grammar by the server; the model never sees it. Verified live against
/// <c>qwen2.5-coder-7b-instruct-ghidra-v2</c>: asked to read a file with a schema naming
/// <c>read_file</c> but no tool descriptions in the prompt, it emitted a perfectly well-formed envelope
/// containing <em>fabricated file contents</em> and an empty <c>tool_calls</c>. The grammar had constrained
/// the shape of an answer to a question the model did not know it could delegate. With the same schema and
/// these instructions added, it called <c>read_file</c> with the correct path. Shape and semantics are
/// separate problems, and only one of them is solved by a grammar.
/// </para>
/// </summary>
internal static class ToolCallInstructionInjector
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Caps the serialized tool schemas injected into the system prompt.
    /// </summary>
    /// <remarks>
    /// Rewriting spends context to buy tool calling, and an unbounded spend is a real failure: a client
    /// offering dozens of tools with verbose JSON Schema can push a small model's whole context window into
    /// instructions, leaving no room for the conversation and producing worse answers than not rewriting at
    /// all. 16 KiB is roughly 4k tokens - a large but survivable share of an 8k-context local model, and far
    /// past any realistic toolset. Whole tools are dropped at the boundary rather than the text being cut
    /// mid-schema, because a truncated schema is worse than an absent one: the model would confidently call
    /// a tool with a signature it half-read.
    /// <para>
    /// The budget applies to the <em>prompt</em> only. A <c>response_format</c> schema is compiled by the
    /// server and never enters the model's context, so it costs network bytes rather than context tokens -
    /// which is exactly why <see cref="Inject"/> reports back which tools survived, instead of letting the
    /// schema builder re-derive a set that could differ.
    /// </para>
    /// </remarks>
    internal const int MaxToolSchemaChars = 16 * 1024;

    /// <summary>
    /// Adds the dialect's instruction preamble plus the request's tool schemas to the system prompt, and
    /// reports which tools actually fitted.
    /// </summary>
    /// <param name="messages">The (already history-rendered) message array, mutated in place.</param>
    /// <param name="tools">The client's <c>tools</c> array, in OpenAI shape.</param>
    /// <param name="dialect">Supplies the instruction text; must carry a <see cref="ToolCallDialect.EmulationPrompt"/>.</param>
    /// <param name="logger">Logger for the bounded-overhead warning.</param>
    /// <returns>
    /// The tools that were described to the model, in the order the client listed them. A caller building a
    /// grammar must constrain to <em>these</em> and not to <paramref name="tools"/>: a schema naming a tool
    /// the prompt never described lets the model emit a call it was told nothing about, and a prompt
    /// describing a tool the schema omits lets it try a call the grammar then forbids mid-token.
    /// </returns>
    /// <remarks>
    /// The instructions are appended to the first existing system message rather than inserted as a second
    /// one. Several local chat templates render only the first system message, or concatenate all of them
    /// into the first position anyway; appending is correct under both, while inserting a separate message
    /// is silently dropped under the first. When the conversation has no system message at all, one is
    /// inserted at the front.
    /// </remarks>
    public static JsonArray Inject(JsonArray messages, JsonArray tools, ToolCallDialect dialect, ILogger logger)
    {
        var prompt = dialect.EmulationPrompt!;
        var (schemaText, included) = SerializeSchemas(tools, logger);
        var instructions = $"{prompt.Preamble}{schemaText}{prompt.Postamble}";

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message ||
                (message["role"] as JsonValue)?.TryGetValue<string>(out var role) != true ||
                !string.Equals(role, "system", StringComparison.Ordinal))
            {
                continue;
            }

            var existing = (message["content"] as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;
            message["content"] = string.IsNullOrEmpty(existing) ? instructions : $"{existing}\n\n{instructions}";
            return included;
        }

        messages.Insert(0, new JsonObject
        {
            ["role"] = "system",
            ["content"] = instructions,
        });

        return included;
    }

    /// <summary>
    /// Serializes each tool's entry, one per line, stopping before <see cref="MaxToolSchemaChars"/> is
    /// exceeded, and returns both the text and the tools it covers.
    /// </summary>
    /// <remarks>
    /// The <b>whole</b> entry is emitted, <c>{"type": "function", "function": {…}}</c> wrapper included.
    /// An earlier version stripped the wrapper as pure OpenAI protocol overhead carrying nothing a model
    /// needs. Measured against a live <c>qwen2.5.1-coder-7b-instruct</c>, that reasoning is wrong: bare
    /// function objects drop the success rate and push the model into inventing its own reply tags, while
    /// the wrapped form - the shape the Qwen chat template puts in front of the model - does not. The
    /// wrapper is not information for the model, it is a shape the model recognizes, and those are not the
    /// same thing.
    /// </remarks>
    private static (string SchemaText, JsonArray Included) SerializeSchemas(JsonArray tools, ILogger logger)
    {
        var builder = new StringBuilder();
        var included = new JsonArray();
        var omitted = 0;

        foreach (var node in tools)
        {
            if (node is not JsonObject tool)
            {
                continue;
            }

            var line = tool.ToJsonString(SerializerOptions);

            // Once one tool has been dropped, every later one is dropped too rather than squeezing in a
            // smaller schema that happens to fit. Offering tools in a different order than the client
            // listed them would make which tools a model can see depend on their serialized size.
            if (omitted > 0 || builder.Length + line.Length + 1 > MaxToolSchemaChars)
            {
                omitted++;
                continue;
            }

            builder.Append(line).Append('\n');
            included.Add(tool.DeepClone());
        }

        if (omitted > 0)
        {
            // States the consequence, not just the arithmetic. An operator reading this needs to know that
            // the omitted tools are not degraded or slower - they are invisible to the model, so a request
            // depending on one cannot succeed, and the reply will look like the model simply chose not to
            // call it. Without that sentence the line reports a number and leaves the diagnosis to be
            // rediscovered.
            logger.LogWarning(
                "Tool-call rewriting: the request offered more tool schemas than the {Limit}-character injection budget allows; {Included} tool(s) were described to the model and {Omitted} were left out, so the model cannot call them.",
                MaxToolSchemaChars,
                included.Count,
                omitted);
        }

        return (builder.ToString(), included);
    }
}

