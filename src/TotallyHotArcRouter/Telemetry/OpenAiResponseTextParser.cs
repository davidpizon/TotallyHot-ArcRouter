using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts the assistant's reply text from OpenAI-compatible chat completion responses, for
/// <see cref="RoutingTelemetryEvent.ResponseSummary"/>.
/// </summary>
public static class OpenAiResponseTextParser
{
    /// <summary>
    /// Extracts text from a non-streaming <c>chat.completion</c> body's <c>choices[0].message.content</c>,
    /// verbatim: a string as sent, or an array's text parts joined with no separator (some OpenAI-compatible
    /// servers return parts). Returns <see langword="false"/> when the reply has no non-blank text.
    /// </summary>
    public static bool TryExtractFromNonStreamingBody(string json, out string text)
    {
        text = string.Empty;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject obj ||
            obj["choices"] is not JsonArray choices ||
            choices.Count == 0 ||
            choices[0] is not JsonObject firstChoice ||
            firstChoice["message"] is not JsonObject message)
            return false;

        var extracted = MessageContentTextExtractor.ExtractVerbatimText(message["content"]);
        if (!MessageContentTextExtractor.HasReplyText(extracted)) return false;

        text = extracted;
        return true;
    }

    /// <summary>
    /// Extracts text from a buffered SSE stream by concatenating every event's
    /// <c>choices[0].delta.content</c>, in stream order and verbatim: a whitespace-only delta is part of the
    /// reply (a paragraph break, indentation, the space between two words), so it is kept (#189). Returns
    /// <see langword="false"/> when the assembled reply has no non-blank text.
    /// </summary>
    public static bool TryExtractFromStreamingBuffer(string sseText, out string text)
    {
        text = string.Empty;
        var builder = new StringBuilder();

        foreach (var evt in SseEventReader.ReadDataEvents(sseText))
        {
            if (evt["choices"] is not JsonArray choices ||
                choices.Count == 0 ||
                choices[0] is not JsonObject firstChoice ||
                firstChoice["delta"] is not JsonObject delta)
                continue;

            var deltaText = MessageContentTextExtractor.ExtractVerbatimText(delta["content"]);
            if (deltaText is not null) builder.Append(deltaText);
        }

        var assembled = builder.ToString();
        if (!MessageContentTextExtractor.HasReplyText(assembled)) return false;

        text = assembled;
        return true;
    }
}