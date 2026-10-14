using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts the assistant's reply text from Anthropic Messages API responses, for
/// <see cref="RoutingTelemetryEvent.ResponseSummary"/>, and reports whether the same body carried a
/// <c>tool_use</c> block (#176 Phase 2).
/// </summary>
public static class AnthropicResponseTextParser
{
    /// <summary>
    /// Extracts text from a non-streaming Messages API body's top-level <c>content</c> block array: every text
    /// block joined with no separator, exactly as the streaming path and <c>AnthropicPayloadTranslator</c> join
    /// them, so the same reply is stored identically however it arrived (#189 decision D2). Returns
    /// <see langword="false"/> when the reply has no non-blank text (for example, a tool-only response).
    /// <paramref name="hasToolCalls"/> is <see langword="true"/> when any content block has
    /// <c>type: "tool_use"</c>, <see langword="false"/> when <c>content</c> is a present array without one or a bare string, and
    /// <see langword="null"/> when the body cannot be read as that shape.
    /// </summary>
    public static bool TryExtractFromNonStreamingBody(string json, out string text, out bool? hasToolCalls)
    {
        text = string.Empty;
        hasToolCalls = null;

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

        // A bare-string content (a non-spec but tolerated shape) still yields text and cannot carry a tool_use
        // block, so it reports false; a missing or otherwise unreadable content leaves the flag null.
        var content = obj["content"];
        if (content is JsonArray blocks) hasToolCalls = blocks.Any(IsToolUseBlock);
        else if (content is JsonValue value && value.TryGetValue<string>(out _)) hasToolCalls = false;

        var extracted = MessageContentTextExtractor.ExtractVerbatimText(content);
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
    /// <paramref name="hasToolCalls"/> is <see langword="true"/> when a <c>content_block_start</c> names a
    /// <c>tool_use</c> block or a delta is <c>input_json_delta</c>, <see langword="false"/> when the stream
    /// had parseable Anthropic events without those, and <see langword="null"/> when nothing parseable was seen.
    /// </summary>
    public static bool TryExtractFromStreamingBuffer(string sseText, out string text, out bool? hasToolCalls)
    {
        text = string.Empty;
        hasToolCalls = null;
        var builder = new StringBuilder();
        var sawEvent = false;
        var sawToolUse = false;

        foreach (var evt in SseEventReader.ReadDataEvents(sseText))
        {
            var type = evt["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeString)
                ? typeString
                : null;

            if (string.Equals(a: type, b: "content_block_start", comparisonType: StringComparison.Ordinal))
            {
                sawEvent = true;
                if (evt["content_block"] is JsonObject block && IsToolUseBlock(block)) sawToolUse = true;
                continue;
            }

            if (!string.Equals(a: type, b: "content_block_delta", comparisonType: StringComparison.Ordinal) ||
                evt["delta"] is not JsonObject delta)
                continue;

            sawEvent = true;

            var deltaType = delta["type"] is JsonValue deltaTypeValue &&
                            deltaTypeValue.TryGetValue<string>(out var deltaTypeString)
                ? deltaTypeString
                : null;

            if (string.Equals(a: deltaType, b: "input_json_delta", comparisonType: StringComparison.Ordinal))
            {
                sawToolUse = true;
                continue;
            }

            if (!string.Equals(a: deltaType, b: "text_delta", comparisonType: StringComparison.Ordinal)) continue;

            if (delta["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var deltaText))
                builder.Append(deltaText);
        }

        if (sawEvent) hasToolCalls = sawToolUse;

        var assembled = builder.ToString();
        if (!MessageContentTextExtractor.HasReplyText(assembled)) return false;

        text = assembled;
        return true;
    }

    private static bool IsToolUseBlock(JsonNode? node) =>
        node is JsonObject block &&
        block["type"] is JsonValue typeValue &&
        typeValue.TryGetValue<string>(out var type) &&
        string.Equals(a: type, b: "tool_use", comparisonType: StringComparison.Ordinal);
}
