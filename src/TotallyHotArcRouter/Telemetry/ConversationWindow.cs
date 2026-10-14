using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Builds the bounded conversation window LLM graders may see: the newest user message plus a few earlier
/// text turns, under a character budget, with secrets obscured and tool/system/image content dropped
/// (issue #293 plan §2.4).
/// </summary>
public static partial class ConversationWindow
{
    /// <summary>
    /// Extracts a grading window from <paramref name="requestBody"/>'s <c>messages</c> array. Returns
    /// <see langword="null"/> when there is no usable newest user message — the same fail-closed signal
    /// <see cref="RequestTextExtractor.ExtractNewestUserMessage"/> gives today.
    /// </summary>
    /// <param name="requestBody">The chat-completions (or Anthropic-shaped) request body.</param>
    /// <param name="options">Size and turn bounds; defaults match plan D1/D2 when omitted.</param>
    /// <returns>The window, or <see langword="null"/> when the newest user message is missing.</returns>
    public static ConversationWindowResult? Extract(JsonObject? requestBody, ConversationWindowOptions? options = null)
    {
        options ??= new ConversationWindowOptions();

        if (requestBody?["messages"] is not JsonArray messages) return null;

        var textTurns = CollectTextTurns(messages);
        if (textTurns.Count == 0) return null;

        var newestIndex = -1;
        for (var i = textTurns.Count - 1; i >= 0; i--)
        {
            if (textTurns[i].IsUser)
            {
                newestIndex = i;
                break;
            }
        }

        if (newestIndex < 0) return null;

        var newestRaw = textTurns[newestIndex].Text;
        var questionChars = newestRaw.Length;
        var truncated = false;

        var newestBounded = BoundNewestUserMessage(text: newestRaw, maxChars: options.MaxNewestUserMessageChars,
            truncated: ref truncated);

        var earlier = new List<TextTurn>();
        var start = Math.Max(0, newestIndex - options.MaxEarlierContextMessages);
        for (var i = start; i < newestIndex; i++)
        {
            var turn = textTurns[i];
            var text = turn.Text;

            if (!turn.IsUser)
            {
                text = ElideLongFencedCode(text: text, maxCodeChars: options.MaxEarlierFencedCodeChars,
                    truncated: ref truncated);
                if (text.Length > options.MaxEarlierAssistantChars)
                {
                    text = text[..options.MaxEarlierAssistantChars];
                    truncated = true;
                }
            }
            else
            {
                text = ElideLongFencedCode(text: text, maxCodeChars: options.MaxEarlierFencedCodeChars,
                    truncated: ref truncated);
            }

            earlier.Add(turn with { Text = text });
        }

        var (formatted, finalNewest) = FitToBudget(newestUserMessage: newestBounded, earlier: earlier,
            maxChars: options.MaxGradingInputChars, truncated: ref truncated);

        var obscuredFormatted = SecretObscurer.Obscure(formatted);
        var obscuredNewest = SecretObscurer.Obscure(finalNewest);

        return new ConversationWindowResult(
            FormattedText: obscuredFormatted,
            NewestUserMessage: obscuredNewest,
            QuestionChars: questionChars,
            ContextMessages: earlier.Count,
            Truncated: truncated);
    }

    /// <summary>Walks <paramref name="messages"/> and keeps only user/assistant turns with recoverable text.</summary>
    private static List<TextTurn> CollectTextTurns(JsonArray messages)
    {
        List<TextTurn> turns = [];
        foreach (var node in messages)
        {
            if (node is not JsonObject message) continue;

            var role = message["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out var roleString)
                ? roleString
                : null;

            if (role is null) continue;

            if (string.Equals(a: role, b: "system", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a: role, b: "tool", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a: role, b: "function", comparisonType: StringComparison.OrdinalIgnoreCase))
                continue;

            var isUser = string.Equals(a: role, b: "user", comparisonType: StringComparison.OrdinalIgnoreCase);
            var isAssistant = string.Equals(a: role, b: "assistant",
                comparisonType: StringComparison.OrdinalIgnoreCase);
            if (!isUser && !isAssistant) continue;

            var text = MessageContentTextExtractor.ExtractText(message["content"]);
            if (string.IsNullOrWhiteSpace(text)) continue;

            turns.Add(new TextTurn(IsUser: isUser, Text: text));
        }

        return turns;
    }

    /// <summary>
    /// Keeps the head and tail of a long newest-user message so a trailing instruction is not lost to a
    /// tail-only cut.
    /// </summary>
    private static string BoundNewestUserMessage(string text, int maxChars, ref bool truncated)
    {
        if (text.Length <= maxChars) return text;

        truncated = true;
        var marker = ConversationWindowOptions.TruncationMarker;
        var keep = maxChars - marker.Length;
        if (keep < 2)
            return text[..Math.Min(text.Length, maxChars)];

        var head = keep / 2;
        var tail = keep - head;
        return string.Concat(text.AsSpan(0, head), marker, text.AsSpan(text.Length - tail, tail));
    }

    /// <summary>Replaces fenced code blocks longer than <paramref name="maxCodeChars"/> with a marker.</summary>
    private static string ElideLongFencedCode(string text, int maxCodeChars, ref bool truncated)
    {
        var elided = false;
        var result = FencedCodeBlockPattern().Replace(text, match =>
        {
            var body = match.Groups[2].Value;
            if (body.Length <= maxCodeChars) return match.Value;

            elided = true;
            var fence = match.Groups[1].Value;
            return FormattableString.Invariant($"{fence}\n{ConversationWindowOptions.CodeBlockOmittedMarker}\n{fence}");
        });
        if (elided) truncated = true;

        return result;
    }

    /// <summary>Formats earlier turns plus the newest user message into a single grading prompt block.</summary>
    private static string FormatWindow(IReadOnlyList<TextTurn> earlier, string newestUserMessage)
    {
        var builder = new StringBuilder();
        foreach (var turn in earlier)
        {
            builder.Append(turn.IsUser ? "User: " : "Assistant: ");
            builder.AppendLine(turn.Text);
            builder.AppendLine();
        }

        builder.Append("User: ");
        builder.Append(newestUserMessage);
        return builder.ToString();
    }

    /// <summary>
    /// Ensures the formatted window fits <paramref name="maxChars"/> by dropping the oldest earlier turns
    /// first, then head-and-tail cutting the newest message if still over budget.
    /// </summary>
    private static (string Formatted, string NewestUserMessage) FitToBudget(
        string newestUserMessage,
        List<TextTurn> earlier,
        int maxChars,
        ref bool truncated)
    {
        var formatted = FormatWindow(earlier: earlier, newestUserMessage: newestUserMessage);
        if (formatted.Length <= maxChars) return (formatted, newestUserMessage);

        truncated = true;
        while (earlier.Count > 0 && formatted.Length > maxChars)
        {
            earlier.RemoveAt(0);
            formatted = FormatWindow(earlier: earlier, newestUserMessage: newestUserMessage);
        }

        if (formatted.Length <= maxChars) return (formatted, newestUserMessage);

        var prefixLength = FormatWindow(earlier: earlier, newestUserMessage: string.Empty).Length;
        var newestBudget = Math.Max(1, maxChars - prefixLength);
        var cutNewest = BoundNewestUserMessage(text: newestUserMessage, maxChars: newestBudget,
            truncated: ref truncated);
        return (FormatWindow(earlier: earlier, newestUserMessage: cutNewest), cutNewest);
    }

    /// <summary>One recoverable user or assistant text turn inside the request's messages array.</summary>
    /// <param name="IsUser"><see langword="true"/> for a user turn; otherwise assistant.</param>
    /// <param name="Text">The turn's plain text.</param>
    private readonly record struct TextTurn(bool IsUser, string Text);

    /// <summary>Matches a fenced code block (triple backtick or tilde) including its body.</summary>
    [GeneratedRegex(@"^(```|~~~)[^\r\n]*\r?\n([\s\S]*?)^\1\s*$", RegexOptions.Multiline)]
    private static partial Regex FencedCodeBlockPattern();
}
