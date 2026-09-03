using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts plain text from a chat message's <c>content</c> field, shared by
/// <see cref="RequestTextExtractor"/> and the response text parsers: OpenAI- and Anthropic-shaped
/// messages both represent <c>content</c> either as a plain string, or as an array of parts/blocks
/// (OpenAI multimodal parts, Anthropic content blocks) of which only <c>type: "text"</c> ones are
/// text - others (images, tool_use, etc.) are skipped rather than causing a failure.
/// </summary>
public static class MessageContentTextExtractor
{
    /// <summary>
    /// Returns the text content, or <see langword="null"/> if <paramref name="content"/> is neither a
    /// non-empty string nor an array containing at least one non-empty <c>type: "text"</c> part.
    /// </summary>
    public static string? ExtractText(JsonNode? content)
    {
        if (content is JsonValue stringValue && stringValue.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text) ? null : text;

        if (content is JsonArray parts)
        {
            var textParts = new List<string>();
            foreach (var part in parts)
                if (part is JsonObject partObj &&
                    partObj["type"] is JsonValue typeValue &&
                    typeValue.TryGetValue<string>(out var type) &&
                    string.Equals(a: type, b: "text", comparisonType: StringComparison.OrdinalIgnoreCase) &&
                    partObj["text"] is JsonValue textValue &&
                    textValue.TryGetValue<string>(out var partText) &&
                    !string.IsNullOrWhiteSpace(partText))
                    textParts.Add(partText);

            return textParts.Count == 0 ? null : string.Join(' ', values: textParts);
        }

        return null;
    }
}