using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Tells a tool-loop continuation from a new user question using only the current request
/// (issue #304 phase 6). Grading that continuation against the question that started the loop
/// waits on the parent-plan chain ADR. Until then a text reply on this turn is
/// <c>loop_unlinked</c>, and the check must not invent that question.
/// </summary>
/// <remarks>
/// The newest turn is tool results when the messages end in one or more <c>role: tool</c> or
/// legacy <c>role: function</c> messages, or when the newest message is a <c>role: user</c>
/// message whose content blocks are only <c>tool_result</c>. A later user message that carries
/// the user's own text is a new question, even if older tool results remain above it. Tool-call
/// ids, same-agent prefixes, and loop totals are the chain this type does not read.
/// </remarks>
public static class ToolResultTurn
{
    /// <summary>
    /// Whether <paramref name="requestBody"/>'s newest turn is tool results.
    /// </summary>
    /// <param name="requestBody">The inbound request JSON, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the newest turn is tool results. A missing or unreadable
    /// newest message returns <see langword="false"/>, so a reply is not refused by a guess.
    /// </returns>
    public static bool NewestTurnIsToolResults(JsonObject? requestBody)
    {
        if (requestBody?["messages"] is not JsonArray messages || messages.Count == 0)
            return false;

        var sawTrailingToolRole = false;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message || !TryGetRole(message: message, role: out var role))
                return sawTrailingToolRole;

            if (IsToolResultRole(role))
            {
                sawTrailingToolRole = true;
                continue;
            }

            if (sawTrailingToolRole)
                return true;

            return IsToolResultOnlyUserMessage(role: role, message: message);
        }

        return sawTrailingToolRole;
    }

    /// <summary>OpenAI <c>role: tool</c> and the legacy <c>role: function</c> result message.</summary>
    private static bool IsToolResultRole(string role) =>
        string.Equals(a: role, b: "tool", comparisonType: StringComparison.OrdinalIgnoreCase) ||
        string.Equals(a: role, b: "function", comparisonType: StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An Anthropic continuation: the newest user message is only <c>tool_result</c> blocks.
    /// Any text block is the user's own words and starts a new question.
    /// </summary>
    private static bool IsToolResultOnlyUserMessage(string role, JsonObject message)
    {
        if (!string.Equals(a: role, b: "user", comparisonType: StringComparison.OrdinalIgnoreCase))
            return false;

        if (message["content"] is not JsonArray blocks || blocks.Count == 0)
            return false;

        var sawToolResult = false;
        foreach (var block in blocks)
        {
            if (block is not JsonObject part || !TryGetBlockType(part: part, type: out var type))
                return false;

            if (string.Equals(a: type, b: "tool_result", comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                sawToolResult = true;
                continue;
            }

            return false;
        }

        return sawToolResult;
    }

    /// <summary>Reads a non-blank <c>role</c> string. A missing role is unreadable, not a tool result.</summary>
    private static bool TryGetRole(JsonObject message, out string role)
    {
        role = string.Empty;
        if (message["role"] is not JsonValue value || !value.TryGetValue<string>(out var parsed) ||
            string.IsNullOrEmpty(parsed))
            return false;

        role = parsed;
        return true;
    }

    /// <summary>Reads a non-blank content-block <c>type</c>. A missing type is unreadable.</summary>
    private static bool TryGetBlockType(JsonObject part, out string type)
    {
        type = string.Empty;
        if (part["type"] is not JsonValue value || !value.TryGetValue<string>(out var parsed) ||
            string.IsNullOrEmpty(parsed))
            return false;

        type = parsed;
        return true;
    }
}
