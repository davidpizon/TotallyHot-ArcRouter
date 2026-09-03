namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// The system-prompt text that teaches a dialect, split around the tool schemas it must contain.
/// <para>
/// Two parts rather than one preamble with the schemas appended, because the wording that actually works
/// puts the schemas <em>in the middle</em>: the schema block is wrapped in its own tags and the reply
/// instructions follow it. Measured against a live model - a preamble-only shape forces the reply format
/// to be described before the model has seen the tools, and the tail is what the model is still looking at
/// when it starts generating.
/// </para>
/// </summary>
/// <param name="Preamble">Everything before the tool schemas, ending at the point they are inserted.</param>
/// <param name="Postamble">Everything after them - in practice the reply-format instructions.</param>
internal sealed record EmulationPrompt(string Preamble, string Postamble);

/// <summary>
/// How one family of models expresses its intent to invoke a tool in plain assistant text, when it
/// does not emit a native OpenAI <c>tool_calls</c> delta (see
/// <c>docs/router/tool-call-normalization.md</c>).
/// <para>
/// A dialect is deliberately **data, not a code path**: adding a model family is a table entry in
/// <see cref="ToolCallDialectRegistry"/>, not a new branch in the scanner. This is the whole reason
/// this type exists - the tool-call echo guard it replaced hardcoded one family's tags in two
/// <see langword="static"/> dictionaries and one payload shape in its scanner, which is why it could
/// not recognize a Mistral or Llama-family echo.
/// </para>
/// <para>
/// The tool-call syntax is a property of the *model's chat template* (which ships inside the GGUF),
/// not of the server hosting it - so one LM Studio or Ollama process can serve models that disagree
/// on every field below. That is why a dialect is resolved per (provider, model) rather than per
/// provider.
/// </para>
/// </summary>
/// <param name="Name">
/// Stable identifier persisted in the capability store's <c>model_tool_capabilities.dialect</c>
/// column, so it must not be renamed once written to a database.
/// </param>
/// <param name="Delimiters">
/// The delimiter pairs that frame a call. **Empty means this dialect is never scanned for** - the
/// case of <see cref="ToolCallDialectRegistry.OpenAiNative"/>, which describes a model that emits real
/// <c>tool_calls</c> and whose prose must therefore never be rewritten.
/// </param>
/// <param name="NameKey">
/// The payload key holding the function name. Every known family uses <c>"name"</c>, but it is
/// configurable rather than assumed.
/// </param>
/// <param name="ArgumentsKey">
/// The payload key holding the arguments. Qwen/Hermes and Mistral use <c>"arguments"</c>; the
/// Llama-3 JSON form uses <c>"parameters"</c>. Hardcoding <c>"arguments"</c> is precisely why the
/// shipping guard silently fails on a Llama-family echo.
/// </param>
/// <param name="EmulationPrompt">
/// The system-prompt text that teaches a model with no native tool calling to reply in this dialect,
/// or <see langword="null"/> for a dialect that is only ever *recognized*, never *taught*. Non-null
/// only for <see cref="ToolCallDialectRegistry.Emulated"/>. Consumed by
/// <see cref="ToolCallEmulationRewriter"/>, which also treats a non-null value here as the marker that a
/// dialect *can* be taught rather than only recognized; carried here so the
/// emulated dialect is a complete, self-describing registry entry rather than a special case split
/// across two components.
/// </param>
internal sealed record ToolCallDialect(
    string Name,
    IReadOnlyList<DialectDelimiter> Delimiters,
    string NameKey,
    string ArgumentsKey,
    EmulationPrompt? EmulationPrompt = null)
{
    /// <summary>
    /// Whether this dialect can be scanned for at all. False for a model that emits native
    /// <c>tool_calls</c>: there is nothing to rewrite, and scanning it anyway is what causes a capable
    /// model's prose *example* of a tool call to be turned into a real invocation.
    /// </summary>
    public bool IsScannable => Delimiters.Count > 0;
}