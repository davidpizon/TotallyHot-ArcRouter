using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Classifies whether an upstream error response indicates the provider's account is out of
/// credits/quota/billing (docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md),
/// as opposed to an ordinary client-fault error (a malformed request, an unknown model, etc.). No
/// status code or error shape is universal across providers - Anthropic reports this on a 400 with a
/// message, OpenAI reports it via a typed <c>insufficient_quota</c> error code, usually on a 429 - so
/// this inspects the parsed error body itself, falling back to a message-keyword match when no typed
/// signal exists. Always fails closed: a parse failure or an unrecognized shape is never classified as
/// out-of-credits, since misclassifying an ordinary client error would incorrectly trip the
/// provider-wide circuit breaker for every other model on that provider.
/// </summary>
internal static class OutOfCreditsClassifier
{
    // Substring keywords checked against an error message when no typed signal exists (e.g.
    // Anthropic's native error shape has no error code, only a message). Matched case-insensitively.
    private static readonly string[] Keywords = ["credit", "balance", "quota", "billing"];

    /// <summary>
    /// Determines whether an upstream error response is classified as out-of-credits.
    /// </summary>
    /// <param name="body">The raw upstream response body.</param>
    /// <param name="embeddedMessage">
    /// A message already extracted from the body by a provider-specific translator (e.g.
    /// <c>AnthropicPayloadTranslator.TryExtractEmbeddedError</c>'s <c>message</c> output), when one is
    /// available. When supplied, this is keyword-matched directly rather than re-parsing
    /// <paramref name="body"/>, since the caller has already done the provider-specific extraction.
    /// </param>
    /// <param name="message">
    /// The human-readable reason, suitable for recording against <c>LiveTrafficStatus</c> or relaying to
    /// the client, when classification succeeds; otherwise empty.
    /// </param>
    /// <returns><see langword="true"/> if the response is classified as out-of-credits.</returns>
    internal static bool IsOutOfCredits(byte[] body, string? embeddedMessage, out string message)
    {
        message = string.Empty;

        if (!string.IsNullOrEmpty(embeddedMessage))
        {
            if (!MatchesKeywords(embeddedMessage))
            {
                return false;
            }

            message = embeddedMessage;
            return true;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is not JsonObject root || root["error"] is not JsonObject errorObject)
        {
            return false;
        }

        var errorMessage = TryGetString(errorObject["message"], out var extractedMessage) ? extractedMessage : string.Empty;

        // OpenAI's typed signal, checked first: higher confidence than a message-keyword guess.
        if (TryGetString(errorObject["code"], out var code) && string.Equals(code, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
        {
            message = errorMessage.Length > 0 ? errorMessage : "insufficient_quota";
            return true;
        }

        if (errorMessage.Length > 0 && MatchesKeywords(errorMessage))
        {
            message = errorMessage;
            return true;
        }

        return false;
    }

    private static bool MatchesKeywords(string text) =>
        Keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Safely reads <paramref name="node"/> as a string, without throwing when the upstream sent an
    /// unexpected JSON type for that field (e.g. a numeric or object <c>message</c>/<c>code</c>) - unlike
    /// <c>JsonNode.GetValue&lt;string&gt;()</c>, which throws on a type mismatch. An unexpected shape is
    /// treated as "field absent", consistent with this classifier's fail-closed contract.
    /// </summary>
    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value!);
    }
}
