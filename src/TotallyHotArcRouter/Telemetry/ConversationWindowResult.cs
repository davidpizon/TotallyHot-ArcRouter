namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// The bounded conversation slice an LLM grader may condition on: newest user message plus a few earlier
/// text turns, already secret-obscured and size-capped. SQLite never stores this text (#293 / ADR-0019).
/// </summary>
/// <param name="FormattedText">
/// The full window text for <c>PendingPromptCache</c> and the judge prompt's task section, after
/// <see cref="Logging.SecretObscurer"/> has run.
/// </param>
/// <param name="NewestUserMessage">
/// The newest user message alone (also obscured), used for presence checks and the question HMAC.
/// Empty when no usable newest user message existed.
/// </param>
/// <param name="QuestionChars">Character count of the newest user message before budget cutting.</param>
/// <param name="ContextMessages">How many earlier text messages were kept.</param>
/// <param name="Truncated">
/// Whether any budget cut, assistant-message cut, or long-code elision was applied.
/// </param>
public sealed record ConversationWindowResult(
    string FormattedText,
    string NewestUserMessage,
    int QuestionChars,
    int ContextMessages,
    bool Truncated);
