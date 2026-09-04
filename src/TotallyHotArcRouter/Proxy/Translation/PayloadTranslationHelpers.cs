using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// JSON-shaping helpers shared across the unified API's payload translators
/// (<c>docs/router/unified-api-translation.md</c>). These four members were previously duplicated,
/// byte-for-byte, between <see cref="AnthropicPayloadTranslator"/> and <see cref="GeminiPayloadTranslator"/>
/// (and reached across that duplication by the Bedrock strategy translators
/// <c>TitanPayloadTranslator</c>/<c>LlamaPayloadTranslator</c> via a concrete dependency on
/// <see cref="AnthropicPayloadTranslator"/>); consolidating them here removes both the duplication and
/// that inappropriate sibling-to-sibling coupling. Provider-specific translation logic - including the
/// two providers' genuinely different embedded-error shapes - stays on each provider's own translator.
/// </summary>
internal static class PayloadTranslationHelpers
{
    /// <summary>
    /// Extracts plain text from an OpenAI message <c>content</c> field (a string, or an array of content blocks -
    /// only <c>text</c> blocks contribute).
    /// </summary>
    internal static string? ExtractText(JsonNode? content)
    {
        switch (content)
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;
            case JsonArray array:
                var builder = new StringBuilder();
                foreach (var element in array)
                    if (element is JsonObject obj &&
                        obj["type"]?.GetValue<string>() == "text" &&
                        obj["text"]?.GetValue<string>() is { } partText)
                        builder.Append(partText);

                return builder.ToString();
            default:
                return null;
        }
    }

    /// <summary>
    /// Parses an OpenAI tool-call <c>arguments</c> string (JSON text) into a provider-native tool-input object; falls
    /// back to an empty object.
    /// </summary>
    internal static JsonNode ParseArgumentsObject(JsonNode? arguments)
    {
        if (arguments is JsonObject alreadyObject) return alreadyObject.DeepClone();

        if (arguments is JsonValue value && value.TryGetValue<string>(out var text) &&
            TryParseJsonObject(text) is { } parsed)
            return parsed;

        return new JsonObject();
    }

    /// <summary>
    /// Attempts to parse <paramref name="text"/> as a JSON object, returning null for blank input or a JSON value
    /// that isn't an object (e.g. malformed JSON or a non-object arguments payload) instead of throwing.
    /// </summary>
    internal static JsonObject? TryParseJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Generates a unique OpenAI-style completion id (<c>chatcmpl-&lt;guid&gt;</c>) for use when the upstream
    /// response omits one.
    /// </summary>
    internal static string GenerateCompletionId()
    {
        return "chatcmpl-" + Guid.NewGuid().ToString(format: "N", provider: CultureInfo.InvariantCulture);
    }
}