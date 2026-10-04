using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts plain text from a chat message's <c>content</c> field. OpenAI- and Anthropic-shaped messages both
/// represent <c>content</c> either as a plain string, or as an array of parts/blocks (OpenAI multimodal parts,
/// Anthropic content blocks) of which only <c>type: "text"</c> ones are text - others (images, tool_use, etc.)
/// are skipped rather than causing a failure.
/// </summary>
/// <remarks>
/// Two contracts, because prompts and replies need different things (#189). <see cref="ExtractText"/> serves
/// <see cref="RequestTextExtractor"/>: a blank prompt means "no task text", and separate parts are joined with a
/// space so adjacent words do not fuse in classifier and embedding input. <see cref="ExtractVerbatimText"/>
/// serves the response parsers: a streamed delta or a content block is a fragment of one reply, so whitespace-only
/// fragments are content and fragments are joined with nothing in between.
/// </remarks>
public static class MessageContentTextExtractor
{
    /// <summary>
    /// Returns the prompt-side text of <paramref name="content"/>: a string unless it is blank, or the
    /// non-blank <c>type: "text"</c> parts of an array joined with a single space. Returns
    /// <see langword="null"/> when there is no non-blank text, which callers treat as "no prompt".
    /// </summary>
    public static string? ExtractText(JsonNode? content)
    {
        if (content is JsonValue stringValue && stringValue.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text) ? null : text;

        if (content is JsonArray parts)
        {
            var textParts = new List<string>();
            foreach (var part in parts)
                if (TryGetTextPart(part: part, text: out var partText) && !string.IsNullOrWhiteSpace(partText))
                    textParts.Add(partText);

            return textParts.Count == 0 ? null : string.Join(' ', values: textParts);
        }

        return null;
    }

    /// <summary>
    /// Returns the reply-side text of <paramref name="content"/> exactly as sent: a string unchanged (even when
    /// empty or whitespace-only), or every <c>type: "text"</c> part of an array concatenated in order with no
    /// separator and no part dropped. Returns <see langword="null"/> only when there is no text at all (no
    /// content, a non-string scalar, or an array with no text part). Whether a whole reply is blank is decided
    /// once, on the assembled text, by <see cref="HasReplyText"/>.
    /// </summary>
    public static string? ExtractVerbatimText(JsonNode? content)
    {
        if (content is JsonValue stringValue && stringValue.TryGetValue<string>(out var text)) return text;

        if (content is not JsonArray parts) return null;

        StringBuilder? builder = null;
        foreach (var part in parts)
            if (TryGetTextPart(part: part, text: out var partText))
                (builder ??= new StringBuilder()).Append(partText);

        return builder?.ToString();
    }

    /// <summary>
    /// Whether an assembled reply counts as text. A reply that is empty or whitespace-only - for example a
    /// <c>"\n"</c> streamed ahead of a tool call - is "no text" on every response path (#189 decision D1), so a
    /// tool-only turn keeps a <see langword="null"/> response rather than a blank one.
    /// </summary>
    public static bool HasReplyText([NotNullWhen(true)] string? reply)
    {
        return !string.IsNullOrWhiteSpace(reply);
    }

    /// <summary>Reads <paramref name="part"/>'s text when it is a <c>type: "text"</c> part with a string <c>text</c>.</summary>
    private static bool TryGetTextPart(JsonNode? part, out string text)
    {
        text = string.Empty;

        if (part is not JsonObject partObj ||
            partObj["type"] is not JsonValue typeValue ||
            !typeValue.TryGetValue<string>(out var type) ||
            !string.Equals(a: type, b: "text", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            partObj["text"] is not JsonValue textValue ||
            !textValue.TryGetValue<string>(out var partText))
            return false;

        text = partText;
        return true;
    }
}
