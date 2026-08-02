using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts a request's "newest user message" text from its <c>messages</c> array, for
/// <see cref="RoutingTelemetryEvent.RequestSummary"/>. Deliberately not the whole array: OpenAI- and
/// Anthropic-style clients resend the full growing conversation history on every request, so using
/// the whole array would repeat every prior turn's content in every subsequent turn's summary.
/// </summary>
public static class RequestTextExtractor
{
    /// <summary>
    /// Scans <paramref name="requestBody"/>'s <c>messages</c> array from the end and returns the text
    /// content of the most recent message with role <c>"user"</c> - not necessarily the last element,
    /// since a user message can be followed by tool-call/tool-result messages in agentic workflows.
    /// Returns <see langword="null"/> if there's no <c>messages</c> array, or no user message in it,
    /// or that message's content couldn't be read as text.
    /// </summary>
    public static string? ExtractNewestUserMessage(JsonObject? requestBody)
    {
        if (requestBody?["messages"] is not JsonArray messages)
        {
            return null;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            var role = message["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out var roleString)
                ? roleString
                : null;

            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return MessageContentTextExtractor.ExtractText(message["content"]);
        }

        return null;
    }
}

