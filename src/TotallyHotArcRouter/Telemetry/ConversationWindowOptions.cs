namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Bounds for <see cref="ConversationWindow.Extract"/>: how much conversation context an LLM grader may
/// see. Defaults match issue #293 plan D1/D2 so the judge call stays short and predictable.
/// </summary>
public sealed class ConversationWindowOptions
{
    /// <summary>
    /// Maximum number of earlier text messages (user or assistant) kept before the newest user message.
    /// Defaults to 6 (about three exchanges).
    /// </summary>
    public int MaxEarlierContextMessages { get; init; } = 6;

    /// <summary>
    /// Maximum characters kept from an earlier assistant message; the rest is dropped. Defaults to 500.
    /// </summary>
    public int MaxEarlierAssistantChars { get; init; } = 500;

    /// <summary>
    /// Total character budget for the formatted window (question plus context). Defaults to 16,384.
    /// </summary>
    public int MaxGradingInputChars { get; init; } = 16_384;

    /// <summary>
    /// Maximum characters kept from the newest user message inside the budget. Defaults to 12,288; when
    /// longer, the message is cut head-and-tail with a visible marker so a trailing instruction survives.
    /// </summary>
    public int MaxNewestUserMessageChars { get; init; } = 12_288;

    /// <summary>
    /// Fenced code blocks longer than this in an <em>earlier</em> turn are replaced by
    /// <c>[code block omitted]</c>. The newest user message is never elided this way. Defaults to 2,000.
    /// </summary>
    public int MaxEarlierFencedCodeChars { get; init; } = 2_000;

    /// <summary>The marker inserted when a long newest-user message is cut head-and-tail.</summary>
    public const string TruncationMarker = "\n…[truncated]…\n";

    /// <summary>The placeholder substituted for a long fenced code block in an earlier turn.</summary>
    public const string CodeBlockOmittedMarker = "[code block omitted]";
}
