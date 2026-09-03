namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// One delimiter pair that frames a tool call in a model's chat-template dialect - e.g. Qwen/Hermes'
/// <c>&lt;tool_call&gt;</c> … <c>&lt;/tool_call&gt;</c>. A dialect may declare several pairs, because a
/// weak model sometimes blends the tag its system prompt uses to *document* the tool schema with the
/// one it is supposed to *reply* with (see <c>docs/router/unified-api-translation.md</c> §4.5 for the
/// live-observed case of exactly that).
/// <para>
/// A <see langword="null"/> <see cref="Close"/> means "the call payload runs to the end of the
/// message" rather than "this delimiter has no body". Some families genuinely have no closing token -
/// Mistral opens with <c>[TOOL_CALLS]</c> and then emits the payload with nothing terminating it - so
/// the distinction is load-bearing, not a convenience default.
/// </para>
/// </summary>
/// <param name="Open">
/// The literal opening token, matched ordinally (never case-insensitively - these are template tokens,
/// not prose).
/// </param>
/// <param name="Close">
/// The literal closing token, or <see langword="null"/> when the payload runs to the end of the
/// message.
/// </param>
internal sealed record DialectDelimiter(string Open, string? Close)
{
    // Validated here rather than left to callers: every built-in entry in ToolCallDialectRegistry
    // already satisfies this, so today it's a no-op - but a dialect is meant to be extensible via
    // operator configuration (see the design doc), and an empty token isn't just theoretically wrong,
    // it's actively broken. An empty Open matches at every position in DialectMatcher.TryFindNextOpener
    // and makes Open[0] throw in ToolCallDialectRegistry.ScannableOpenerFirstChars; an empty Close
    // matches immediately at bodyStart, framing a zero-length region instead of a real one. Fail at
    // construction, not with a confusing downstream exception or a silently-wrong scan.
    //
    // Note: this validates `new DialectDelimiter(...)` but not a `with` expression - C# record `with`
    // copies via the auto-generated copy constructor and sets `init` properties directly, bypassing a
    // field initializer like the ones below. No call site in this codebase uses `with` on this type
    // today; if one starts to, it would need the same guard moved into the init accessor bodies.
    /// <summary>Gets the literal opening token, matched ordinally against the raw message text.</summary>
    public string Open { get; init; } = !string.IsNullOrEmpty(Open)
        ? Open
        : throw new ArgumentException(message: "A dialect delimiter's Open token must be non-empty.",
            paramName: nameof(Open));

    /// <summary>
    /// Gets the literal closing token, or <see langword="null"/> when the payload runs to the end of the
    /// message (e.g. Mistral's close-less <c>[TOOL_CALLS]</c>).
    /// </summary>
    public string? Close { get; init; } = Close is not { Length: 0 }
        ? Close
        : throw new ArgumentException(
            message:
            "A dialect delimiter's Close token must be null or non-empty - use null for \"runs to end of message\", never an empty string.",
            paramName: nameof(Close));
}