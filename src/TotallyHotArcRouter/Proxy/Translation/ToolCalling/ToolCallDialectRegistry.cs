namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// The built-in set of <see cref="ToolCallDialect"/>s, and the lookups over it that detection and
/// normalization need (see <c>docs/router/tool-call-normalization.md</c> §3.1).
///
/// <para>
/// Entries are ordered most- to least-specific, and <see cref="ScannableDialects"/> preserves that
/// order, because <see cref="DialectMatcher.MatchAny"/> resolves ties by taking the first dialect that
/// yields a well-shaped call. Reordering this list therefore changes which dialect an ambiguous
/// response is attributed to - it is a behavioral list, not a presentation one.
/// </para>
/// </summary>
internal static class ToolCallDialectRegistry
{
    /// <summary>
    /// A model that emits real OpenAI <c>tool_calls</c> deltas and needs no rewriting at all. Has no
    /// delimiters, so <see cref="ToolCallDialect.IsScannable"/> is false and the normalizing translator
    /// is never installed for it - preserving byte-for-byte forwarding for the common case.
    /// </summary>
    public static readonly ToolCallDialect OpenAiNative = new(
        Name: "openai-native",
        Delimiters: [],
        NameKey: "name",
        ArgumentsKey: "arguments");

    /// <summary>
    /// Qwen 2.5 / NousResearch Hermes and the many templates derived from them. Both tags are listed
    /// for the reason <c>unified-api-translation.md</c> §4.5 documents from a live LM Studio repro:
    /// the Qwen chat template wraps the tool *schema* documentation in the system prompt with
    /// <c>&lt;tools&gt;</c> and asks the model to reply with <c>&lt;tool_call&gt;</c>, and a small model
    /// sometimes blends the two. This is the one dialect the shipping echo guard already handles, and
    /// this entry reproduces its exact behavior.
    /// </summary>
    public static readonly ToolCallDialect Hermes = new(
        Name: "hermes",
        Delimiters:
        [
            new DialectDelimiter("<tool_call>", "</tool_call>"),
            new DialectDelimiter("<tools>", "</tools>")
        ],
        NameKey: "name",
        ArgumentsKey: "arguments");

    /// <summary>
    /// Mistral / Mixtral, which opens with <c>[TOOL_CALLS]</c> and then emits the payload with no
    /// closing token - hence the <see langword="null"/> close. The payload is normally a JSON *array*
    /// of calls, which needs no special handling here:
    /// <see cref="JsonObjectScanner.FindTopLevelJsonObjects"/>
    /// walks the region for balanced-brace objects, so <c>[{a},{b}]</c> yields both entries exactly as
    /// two sequential objects would.
    /// </summary>
    public static readonly ToolCallDialect Mistral = new(
        Name: "mistral",
        Delimiters: [new DialectDelimiter("[TOOL_CALLS]", Close: null)],
        NameKey: "name",
        ArgumentsKey: "arguments");

    /// <summary>
    /// The Llama 3.x JSON tool-call form, which keys its arguments as <c>"parameters"</c> rather than
    /// <c>"arguments"</c> - the reason a hardcoded <c>arguments</c> lookup silently drops a
    /// Llama-family call even when the surrounding text is found.
    ///
    /// <para>
    /// Only the <c>&lt;|python_tag|&gt;</c>-delimited form is registered. Llama also sometimes emits a
    /// *bare* JSON object with no delimiter at all, and that form is **deliberately excluded**: a
    /// dialect with no opening token would match any JSON object anywhere in any response, turning
    /// every code sample containing <c>{"name": …}</c> into a tool invocation. Recovering the bare form
    /// safely needs the request context (was a tool named <c>name</c> even offered?), which belongs to
    /// the normalizing translator in Phase 4, not to a context-free dialect.
    /// </para>
    /// </summary>
    public static readonly ToolCallDialect Llama3Json = new(
        Name: "llama3-json",
        Delimiters: [new DialectDelimiter("<|python_tag|>", Close: null)],
        NameKey: "name",
        ArgumentsKey: "parameters");

    /// <summary>
    /// A bare <c>&lt;function-call&gt;</c> ... <c>&lt;/function-call&gt;</c> wrapper, observed from a live
    /// LM Studio repro of a Qwen2-architecture fine-tune (<c>qwen2.5-coder-7b-instruct-ghidra-v2</c>) that
    /// is otherwise classified <see cref="Hermes"/> by <see cref="ModelDialectResolver"/>'s architecture
    /// tier. The same model, same request, same tools, produced a real <c>tool_calls</c> field on one run
    /// and this wrapper on another - so it is a genuine alternate framing the underlying weights
    /// sometimes fall back to, not a fabricated guess the way an unverified DeepSeek spelling would be
    /// (see the DeepSeek omission note below). Registering it lets that framing normalize instead of
    /// reaching the client as literal <c>content</c>.
    /// </summary>
    public static readonly ToolCallDialect FunctionCall = new(
        Name: "function-call",
        Delimiters: [new DialectDelimiter("<function-call>", "</function-call>")],
        NameKey: "name",
        ArgumentsKey: "arguments");

    /// <summary>
    /// The dialect for a model whose tool calls are produced by <b>constrained decoding</b> rather than by
    /// any text convention: the request carries a <c>response_format</c> JSON schema, and the server's own
    /// sampler (llama.cpp GBNF grammar under LM Studio, the equivalent under Ollama) makes any other shape
    /// unrepresentable. Mirrors LiteLLM's JSON-mode fallback for models without native tool calling.
    ///
    /// <para>
    /// Has no delimiters, so <see cref="ToolCallDialect.IsScannable"/> is false and it never joins
    /// <see cref="ScannableDialects"/> - there is nothing to scan *for*. The reply is a whole JSON envelope
    /// parsed strictly, not a framed region located inside prose, which is precisely why this dialect
    /// escapes the failure mode every other entry here is subject to. A model that emits an unregistered
    /// framing (or none at all) defeats delimiter matching completely; it cannot defeat a grammar that
    /// forbids emitting anything else.
    /// </para>
    ///
    /// <para>
    /// <b>It still carries an <see cref="ToolCallDialect.EmulationPrompt"/>, and that is not redundant.</b>
    /// The obvious reading - the schema is the instruction, so a prompt would only repeat it - is wrong, and
    /// was measured to be wrong. A <c>response_format</c> schema is compiled into a sampler grammar by the
    /// server and never enters the model's context, so it constrains the <em>shape</em> of a reply while
    /// telling the model nothing about what tools exist. Asked to read a file under a schema naming
    /// <c>read_file</c> but with no tool descriptions in the prompt,
    /// <c>qwen2.5-coder-7b-instruct-ghidra-v2</c> returned a flawlessly-shaped envelope containing
    /// <em>invented file contents</em> and an empty <c>tool_calls</c>. The preamble below is therefore the
    /// same measured Qwen/Hermes tools block <see cref="Emulated"/> uses, for the same borrowed-not-invented
    /// reason; only the postamble differs, because the reply format it describes is the one the grammar
    /// already enforces.
    /// </para>
    ///
    /// <para>
    /// Because it has a prompt, ordering in <see cref="ToolCallNormalizerFactory"/> matters: the constrained
    /// branch must be tested <em>before</em> the "has an emulation prompt" branch, or a constrained model
    /// would be routed to <see cref="ToolCallEmulatingTranslator"/> - which would then throw, since that
    /// translator requires delimiters this dialect deliberately does not have.
    /// </para>
    /// </summary>
    public static readonly ToolCallDialect Constrained = new(
        Name: "constrained",
        Delimiters: [],
        NameKey: "name",
        ArgumentsKey: "arguments",
        EmulationPrompt: new EmulationPrompt(
            Preamble:
                """
                # Tools

                You may call one or more functions to assist with the user query.

                You are provided with function signatures within <tools></tools> XML tags:
                <tools>
                """,
            Postamble:
                """
                </tools>

                Reply with a JSON object containing two fields: "content" for your reply to the user, and
                "tool_calls" for any functions to invoke, each as {"name": <function-name>, "arguments":
                <args-json-object>}. Leave "tool_calls" empty when no function is needed, and leave
                "content" empty when calling one. Never answer from memory what a function can determine.
                """));

    /// <summary>
    /// The canonical dialect TotallyHot.ArcRouter *teaches* a model that has no usable native tool calling
    /// (Phase 5). It reuses Hermes framing on purpose, so emulated replies are parsed by the same
    /// matcher path as a real Hermes echo rather than needing a second parser.
    ///
    /// <para>
    /// <b>This wording is measured, not written.</b> It is the Qwen/Hermes chat template's own
    /// instruction text, near-verbatim, and it was chosen because three hand-written alternatives all
    /// failed against a live <c>qwen2.5.1-coder-7b-instruct</c>. Prose that explained the format in plain
    /// English - including a version that stated the tags were literal and showed a worked example -
    /// produced a correct JSON object with <em>no delimiters at all</em> on every single run: the right
    /// call, unparseable, which is precisely the bug this workstream exists to fix. Novel phrasing loses
    /// to phrasing the model has seen in training, so the emulated dialect borrows rather than invents.
    /// Rewriting this text is a change that must be re-measured, not reasoned about.
    /// </para>
    ///
    /// <para>
    /// Both Hermes delimiters are registered, and the second one is not redundant. Told to wrap schemas in
    /// <c>&lt;tools&gt;</c> and to reply in <c>&lt;tool_call&gt;</c>, the model deterministically replied
    /// in <c>&lt;tools&gt;</c> - the tag it had just seen framing JSON. That is the same blending
    /// <see cref="Hermes"/> documents from the original incident, and it is why removing the second
    /// delimiter takes this dialect from every case passing to almost none. Removing the
    /// <c>&lt;tools&gt;</c> wrapper from the prompt to avoid the blend was also tried and is worse: the
    /// model then invents its own tags (<c>&lt;json&gt;</c>) or falls back to a code fence.
    /// </para>
    /// </summary>
    public static readonly ToolCallDialect Emulated = new(
        Name: "emulated",
        Delimiters:
        [
            new DialectDelimiter("<tool_call>", "</tool_call>"),
            new DialectDelimiter("<tools>", "</tools>")
        ],
        NameKey: "name",
        ArgumentsKey: "arguments",
        EmulationPrompt: new EmulationPrompt(
            Preamble:
                """
                # Tools

                You may call one or more functions to assist with the user query.

                You are provided with function signatures within <tools></tools> XML tags:
                <tools>
                """,
            Postamble:
                """
                </tools>

                For each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags:
                <tool_call>
                {"name": <function-name>, "arguments": <args-json-object>}
                </tool_call>
                """));

    /// <summary>
    /// Every built-in dialect, including the non-scannable <see cref="OpenAiNative"/> sentinel, so a
    /// persisted capability value can always be resolved back to a dialect by name.
    /// </summary>
    public static IReadOnlyList<ToolCallDialect> All { get; } =
        [OpenAiNative, Hermes, Mistral, Llama3Json, FunctionCall, Constrained, Emulated];

    /// <summary>
    /// The dialects worth scanning a response for, in tie-break order. Excludes
    /// <see cref="OpenAiNative"/> and <see cref="Constrained"/> (nothing to scan in either - the first
    /// emits a real <c>tool_calls</c> field, the second a whole JSON envelope parsed strictly), and
    /// <see cref="Emulated"/>, whose framing is
    /// identical to <see cref="Hermes"/> - including both would make attribution a coin flip between
    /// two names for the same match, and "hermes" is the correct attribution for a model that produced
    /// that framing on its own rather than because TotallyHot.ArcRouter taught it to.
    /// </summary>
    public static IReadOnlyList<ToolCallDialect> ScannableDialects { get; } =
        [Hermes, Mistral, Llama3Json, FunctionCall];

    /// <summary>
    /// The distinct first characters of every scannable dialect's opening tokens (<c>&lt;</c> and
    /// <c>[</c> today). Phase 4's streaming scanner tests incoming text against this with a single
    /// <see cref="string.IndexOfAny(char[])"/> before attempting any delimiter match, so ordinary prose
    /// - which is nearly all output - never pays for full matching. Precomputed once here rather than
    /// rebuilt per request.
    /// </summary>
    public static char[] ScannableOpenerFirstChars { get; } =
        ScannableDialects
            .SelectMany(dialect => dialect.Delimiters)
            .Select(delimiter => delimiter.Open[0])
            .Distinct()
            .ToArray();

    // DeepSeek is deliberately absent. Its tool-call template uses special delimiter tokens built from
    // non-ASCII full-width characters, and registering a guessed spelling would be worse than
    // registering nothing: a near-miss silently never matches, which is indistinguishable from the bug
    // this whole workstream exists to fix. Phase 3's Ollama `/api/show` integration dumps a model's
    // literal chat template, which is the correct place to read the real tokens off a live model and
    // add the entry with evidence. Until then, a DeepSeek model is classified by Phase 4's
    // observation path like any other unknown, and falls back to emulation if nothing matches.

    /// <summary>
    /// Resolves a dialect by its persisted <see cref="ToolCallDialect.Name"/>, case-insensitively.
    /// Returns <see langword="false"/> for an unknown name rather than throwing, so a capability row
    /// written by a newer build - or hand-edited - degrades to "undetected" instead of taking the proxy
    /// down on startup.
    /// </summary>
    /// <param name="name">The dialect name to look up.</param>
    /// <param name="dialect">The matching dialect, when found.</param>
    public static bool TryGet(string? name, out ToolCallDialect dialect)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                dialect = candidate;
                return true;
            }
        }

        dialect = OpenAiNative;
        return false;
    }
}

