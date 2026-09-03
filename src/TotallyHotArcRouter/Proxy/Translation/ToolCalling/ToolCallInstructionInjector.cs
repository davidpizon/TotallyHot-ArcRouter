using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Puts a dialect's tool-calling instructions, and the request's tool schemas, into the system prompt -
/// the half of tool-call rewriting that tells a model <em>what tools exist and what they do</em>.
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
    /// <summary>
    /// Caps the serialized tool schemas injected into the system prompt, when no probed context window is
    /// available for the model (<see cref="ComputeBudget"/>).
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
    /// This fixed value is what every model got before context windows were tracked per (provider, model),
    /// and it remains the fallback for the majority of providers that never publish one at all - every
    /// hosted OpenAI-shaped and Anthropic endpoint, per <see cref="IModelContextWindowStore"/>. Once a window
    /// is known, <see cref="ComputeBudget"/> scales the cap to it instead: the same 16 KiB is a reasonable
    /// half of an 8k local model's context and a needlessly tight cap on a 128k one, and it can just as
    /// easily be *too generous* for a genuinely tiny window, where 4k tokens of schema would crowd out the
    /// conversation entirely.
    /// </para>
    /// <para>
    /// The budget applies to the <em>prompt</em> only. A <c>response_format</c> schema is compiled by the
    /// server and never enters the model's context, so it costs network bytes rather than context tokens -
    /// which is exactly why <see cref="Inject"/> reports back which tools survived, instead of letting the
    /// schema builder re-derive a set that could differ.
    /// </para>
    /// </remarks>
    internal const int MaxToolSchemaChars = 16 * 1024;

    /// <summary>
    /// The share of a probed context window <see cref="ComputeBudget"/> will spend on tool schemas.
    /// </summary>
    /// <remarks>
    /// Chosen to match the reasoning already documented on <see cref="MaxToolSchemaChars"/>: 16 KiB is about
    /// 4k tokens, which that constant's own comment calls "a large but survivable share of an 8k-context
    /// local model" - i.e. roughly half. Scaling from that same fraction means a model whose probed window
    /// happens to be 8k gets the identical budget it always did; the change is only in what happens above
    /// and below that point.
    /// </remarks>
    private const double ContextWindowBudgetFraction = 0.5;

    /// <summary>
    /// The conservative tokens-per-character estimate <see cref="ComputeBudget"/> uses to convert a
    /// token-denominated context window into a character budget.
    /// </summary>
    /// <remarks>
    /// Matches <see cref="MaxToolSchemaChars"/>'s own arithmetic (16384 chars / 4 = 4096 tokens), rather than
    /// introducing a second, independently-tuned ratio. Tool schemas are JSON - punctuation-dense compared to
    /// prose - so 4 chars/token undercounts slightly, which is the safe direction to be wrong in: it leaves
    /// headroom rather than spending it.
    /// </remarks>
    private const double CharsPerTokenEstimate = 4.0;

    /// <summary>
    /// The smallest budget <see cref="ComputeBudget"/> will produce from a probed window, regardless of how
    /// small that window is.
    /// </summary>
    /// <remarks>
    /// Without a floor, a model probed at a genuinely tiny window (a few hundred tokens) would get a budget
    /// too small to describe even one realistic tool, silently disabling tool calling for it entirely rather
    /// than degrading gracefully. 4 KiB is enough room for one or two typical tool schemas to survive the
    /// boundary in <see cref="SerializeSchemas"/>.
    /// </remarks>
    private const int MinToolSchemaChars = 4 * 1024;

    /// <summary>
    /// The largest budget <see cref="ComputeBudget"/> will produce from a probed window, regardless of how
    /// large that window is.
    /// </summary>
    /// <remarks>
    /// A sanity ceiling against a corrupt or wildly-misreported probe (a provider that returns a
    /// context-length field in the wrong unit, for example) rather than a real design target - in practice a
    /// request's own tool count already bounds the serialized size (<see cref="SerializeSchemas"/> only
    /// spends what the client actually offered), so this ceiling is rarely the thing doing the limiting.
    /// </remarks>
    private const int MaxToolSchemaCharsCeiling = 128 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Adds the dialect's instruction preamble plus the request's tool schemas to the system prompt, and
    /// reports which tools actually fitted.
    /// </summary>
    /// <param name="messages">The (already history-rendered) message array, mutated in place.</param>
    /// <param name="tools">The client's <c>tools</c> array, in OpenAI shape.</param>
    /// <param name="dialect">Supplies the instruction text; must carry a <see cref="ToolCallDialect.EmulationPrompt"/>.</param>
    /// <param name="logger">Logger for the bounded-overhead warning.</param>
    /// <param name="contextWindow">
    /// The model's probed context window, if any has been read for it. When <see langword="null"/> - most
    /// providers, since only LM Studio and Ollama publish one - the schema budget falls back to the fixed
    /// <see cref="MaxToolSchemaChars"/>; otherwise <see cref="ComputeBudget"/> scales the budget to it.
    /// </param>
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
    public static JsonArray Inject(
        JsonArray messages, JsonArray tools, ToolCallDialect dialect, ILogger logger,
        ModelContextWindow? contextWindow = null)
    {
        var prompt = dialect.EmulationPrompt!;
        var (schemaText, included) =
            SerializeSchemas(tools: tools, logger: logger, budgetChars: ComputeBudget(contextWindow));
        var instructions = $"{prompt.Preamble}{schemaText}{prompt.Postamble}";

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message ||
                (message["role"] as JsonValue)?.TryGetValue<string>(out var role) != true ||
                !string.Equals(a: role, b: "system", comparisonType: StringComparison.Ordinal))
                continue;

            var existing = (message["content"] as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;
            message["content"] = string.IsNullOrEmpty(existing) ? instructions : $"{existing}\n\n{instructions}";
            return included;
        }

        messages.Insert(0, item: new JsonObject
        {
            ["role"] = "system",
            ["content"] = instructions
        });

        return included;
    }

    /// <summary>
    /// Computes the injection budget for one request: the fixed <see cref="MaxToolSchemaChars"/> when
    /// <paramref name="contextWindow"/> is unknown, or a share of the probed window otherwise.
    /// </summary>
    /// <remarks>
    /// Scaling by the window rather than always spending the fixed cap corrects it in both directions: a
    /// large-context model that was needlessly having tools dropped at 16 KiB gets more room, and a
    /// genuinely small-context model that would have had 16 KiB - possibly its entire window - spent on
    /// schemas gets less. <see cref="ContextWindowBudgetFraction"/> and <see cref="CharsPerTokenEstimate"/>
    /// carry the reasoning for the specific numbers; this method only combines and clamps them.
    /// </remarks>
    private static int ComputeBudget(ModelContextWindow? contextWindow)
    {
        if (contextWindow is not { ContextLength: > 0 }) return MaxToolSchemaChars;

        var scaledChars = contextWindow.ContextLength * CharsPerTokenEstimate * ContextWindowBudgetFraction;
        return (int)Math.Clamp(value: scaledChars, min: MinToolSchemaChars, max: MaxToolSchemaCharsCeiling);
    }

    /// <summary>
    /// Serializes each tool's entry, one per line, stopping before <paramref name="budgetChars"/> is
    /// exceeded, and returns both the text and the tools it covers.
    /// </summary>
    /// <param name="tools">The client's <c>tools</c> array, in OpenAI shape.</param>
    /// <param name="logger">Logger for the bounded-overhead warning.</param>
    /// <param name="budgetChars">The character budget for this request, from <see cref="ComputeBudget"/>.</param>
    /// <remarks>
    /// The <b>whole</b> entry is emitted, <c>{"type": "function", "function": {…}}</c> wrapper included.
    /// An earlier version stripped the wrapper as pure OpenAI protocol overhead carrying nothing a model
    /// needs. Measured against a live <c>qwen2.5.1-coder-7b-instruct</c>, that reasoning is wrong: bare
    /// function objects drop the success rate and push the model into inventing its own reply tags, while
    /// the wrapped form - the shape the Qwen chat template puts in front of the model - does not. The
    /// wrapper is not information for the model, it is a shape the model recognizes, and those are not the
    /// same thing.
    /// </remarks>
    private static (string SchemaText, JsonArray Included) SerializeSchemas(JsonArray tools, ILogger logger,
        int budgetChars)
    {
        var builder = new StringBuilder();
        var included = new JsonArray();
        var omitted = 0;

        foreach (var node in tools)
        {
            if (node is not JsonObject tool) continue;

            var line = tool.ToJsonString(SerializerOptions);

            // Once one tool has been dropped, every later one is dropped too rather than squeezing in a
            // smaller schema that happens to fit. Offering tools in a different order than the client
            // listed them would make which tools a model can see depend on their serialized size.
            if (omitted > 0 || builder.Length + line.Length + 1 > budgetChars)
            {
                omitted++;
                continue;
            }

            builder.Append(line).Append('\n');
            included.Add(tool.DeepClone());
        }

        if (omitted > 0)
            // States the consequence, not just the arithmetic. An operator reading this needs to know that
            // the omitted tools are not degraded or slower - they are invisible to the model, so a request
            // depending on one cannot succeed, and the reply will look like the model simply chose not to
            // call it. Without that sentence the line reports a number and leaves the diagnosis to be
            // rediscovered. The limit reported is the budget actually applied to this request, which may
            // differ from MaxToolSchemaChars once a probed context window is scaling it.
            logger.LogWarning(
                message:
                "Tool-call rewriting: the request offered more tool schemas than the {Limit}-character injection budget allows; {Included} tool(s) were described to the model and {Omitted} were left out, so the model cannot call them.",
                budgetChars,
                included.Count,
                omitted);

        return (builder.ToString(), included);
    }
}