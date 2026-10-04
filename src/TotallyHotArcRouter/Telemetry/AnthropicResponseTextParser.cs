using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts the assistant's reply text from Anthropic Messages API responses, for
/// <see cref="RoutingTelemetryEvent.ResponseSummary"/>.
/// </summary>
public static class AnthropicResponseTextParser
{
    /// <summary>
    /// Extracts text from a non-streaming Messages API body's top-level <c>content</c> block array: every text
    /// block joined with no separator, exactly as the streaming path and <c>AnthropicPayloadTranslator</c> join
    /// them, so the same reply is stored identically however it arrived (#189 decision D2). Returns
    /// <see langword="false"/> when the reply has no non-blank text (for example, a tool-only response).
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

        if (node is not JsonObject obj) return false;

        var extracted = MessageContentTextExtractor.ExtractVerbatimText(obj["content"]);
        if (!MessageContentTextExtractor.HasReplyText(extracted)) return false;

        text = extracted;
        return true;
    }

    /// <summary>
    /// Extracts text from a buffered Anthropic SSE stream by concatenating every
    /// <c>content_block_delta</c> event's <c>delta.text</c> (only when <c>delta.type</c> is
    /// <c>"text_delta"</c> - other delta types, e.g. <c>input_json_delta</c> for tool use, are
    /// skipped), in stream order. Returns <see langword="false"/> when the assembled reply has no non-blank text
    /// (#189 decision D1).
    /// </summary>
    public static bool TryExtractFromStreamingBuffer(string sseText, out string text)
    {
        text = string.Empty;
        var builder = new StringBuilder();

        foreach (var evt in SseEventReader.ReadDataEvents(sseText))
        {
            var type = evt["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeString)
                ? typeString
                : null;

            if (!string.Equals(a: type, b: "content_block_delta", comparisonType: StringComparison.Ordinal) ||
                evt["delta"] is not JsonObject delta)
                continue;

            var deltaType = delta["type"] is JsonValue deltaTypeValue &&
                            deltaTypeValue.TryGetValue<string>(out var deltaTypeString)
                ? deltaTypeString
                : null;

            if (!string.Equals(a: deltaType, b: "text_delta", comparisonType: StringComparison.Ordinal)) continue;

            if (delta["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var deltaText))
                builder.Append(deltaText);
        }

        var assembled = builder.ToString();
        if (!MessageContentTextExtractor.HasReplyText(assembled)) return false;

        text = assembled;
        return true;
    }
}