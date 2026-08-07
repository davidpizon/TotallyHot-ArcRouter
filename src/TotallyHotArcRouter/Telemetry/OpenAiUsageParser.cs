using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts token usage from OpenAI-compatible chat completion responses.
/// </summary>
public static class OpenAiUsageParser
{
    /// <summary>
    /// Extracts usage from a non-streaming <c>chat.completion</c> JSON body's top-level
    /// <c>usage.prompt_tokens</c>/<c>usage.completion_tokens</c> fields.
    /// </summary>
    public static bool TryExtractFromNonStreamingBody(string json, out UsageInfo usage)
    {
        usage = default;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        return node is JsonObject obj && TryExtractFromUsageContainer(obj, out usage);
    }

    /// <summary>
    /// Extracts usage from a buffered SSE stream. OpenAI only includes a <c>usage</c> object in a
    /// streaming response when the client requested <c>stream_options.include_usage = true</c>; when
    /// present, it appears once, in the final content chunk (with an empty <c>choices</c> array),
    /// before the <c>[DONE]</c> sentinel. Scans every event and keeps the last usable one, so a
    /// mid-stream event that happens to carry a (rare, non-final) usage field doesn't win over the
    /// real final one.
    /// </summary>
    public static bool TryExtractFromStreamingBuffer(string sseText, out UsageInfo usage)
    {
        usage = default;
        var found = false;

        foreach (var evt in SseEventReader.ReadDataEvents(sseText))
        {
            if (TryExtractFromUsageContainer(evt, out var candidate))
            {
                usage = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Attempts to read prompt and completion token counts from the "usage" object nested inside the given
    /// JSON container, normalizing OpenAI's <b>inclusive</b> cache semantics into <see cref="UsageInfo"/>'s
    /// additive (Anthropic-convention) model (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §6.1):
    /// <c>usage.prompt_tokens_details.cached_tokens</c> is a subset of <c>prompt_tokens</c>, so it (and, for
    /// an enriched translated-Anthropic body, the <c>cache_creation_input_tokens</c> extension field) is
    /// subtracted back out rather than added on top - naively adding it would double-count.
    /// </summary>
    private static bool TryExtractFromUsageContainer(JsonObject container, out UsageInfo usage)
    {
        usage = default;

        if (container["usage"] is not JsonObject usageObj)
        {
            return false;
        }

        if (!TryGetInt(usageObj, "prompt_tokens", out var promptTokens) ||
            !TryGetInt(usageObj, "completion_tokens", out var completionTokens))
        {
            return false;
        }

        var cacheReadTokens = 0;
        if (usageObj["prompt_tokens_details"] is JsonObject promptTokensDetails)
        {
            TryGetInt(promptTokensDetails, "cached_tokens", out cacheReadTokens);
        }

        // Only an enriched translated-Anthropic body (AnthropicPayloadTranslator.BuildEnrichedUsage) carries
        // this extension field; absent for a real OpenAI response, which has no cache-write concept.
        TryGetInt(usageObj, "cache_creation_input_tokens", out var cacheCreationTokens);

        // Math.Max guards malformed input (cached_tokens exceeding prompt_tokens) rather than producing a
        // negative additive prompt count.
        var additivePromptTokens = Math.Max(0, promptTokens - cacheReadTokens - cacheCreationTokens);

        usage = new UsageInfo(additivePromptTokens, completionTokens, cacheCreationTokens, cacheReadTokens);
        return true;
    }

    /// <summary>
    /// Attempts to read a named property from the JSON object as an integer value.
    /// </summary>
    private static bool TryGetInt(JsonObject obj, string propertyName, out int value)
    {
        value = 0;
        return obj[propertyName] is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }
}

