using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts token usage from Anthropic Messages API responses.
/// </summary>
public static class AnthropicUsageParser
{
    /// <summary>
    /// Extracts usage from a non-streaming Messages API JSON body's top-level
    /// <c>usage.input_tokens</c>/<c>usage.output_tokens</c> fields.
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

        if (node is not JsonObject obj || obj["usage"] is not JsonObject usageObj)
        {
            return false;
        }

        return TryReadTopLevelUsage(usageObj, out usage);
    }

    /// <summary>
    /// Extracts usage from a buffered Anthropic SSE stream. <c>input_tokens</c> and the cache fields come
    /// from the <c>message_start</c> event's <c>message.usage</c> (fixed for the whole response, as
    /// initially reported); <c>output_tokens</c> comes from the last <c>message_delta</c> event's
    /// <c>usage.output_tokens</c> (a running/cumulative total, so only the final one is meaningful). When
    /// the final <c>message_delta</c>'s <c>usage</c> also carries cache fields (newer API versions send
    /// cumulative full usage there), those values win over <c>message_start</c>'s - they are final,
    /// <c>message_start</c>'s are only initial. Returns usage as soon as input_tokens is known even if no
    /// message_delta was seen (output_tokens then reports as 0 rather than failing outright, since a valid
    /// input-token count is still useful telemetry).
    /// </summary>
    public static bool TryExtractFromStreamingBuffer(string sseText, out UsageInfo usage)
    {
        usage = default;
        var haveInput = false;
        var promptTokens = 0;
        var completionTokens = 0;
        var cacheCreationTokens = 0;
        var cacheReadTokens = 0;

        foreach (var evt in SseEventReader.ReadDataEvents(sseText))
        {
            var type = evt["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeString)
                ? typeString
                : null;

            switch (type)
            {
                case "message_start" when evt["message"] is JsonObject message && message["usage"] is JsonObject startUsage:
                    if (TryGetInt(startUsage, "input_tokens", out var inputTokens))
                    {
                        promptTokens = inputTokens;
                        haveInput = true;
                    }
                    if (TryGetInt(startUsage, "output_tokens", out var initialOutputTokens))
                    {
                        completionTokens = initialOutputTokens;
                    }
                    if (TryGetInt(startUsage, "cache_creation_input_tokens", out var startCacheCreation))
                    {
                        cacheCreationTokens = startCacheCreation;
                    }
                    if (TryGetInt(startUsage, "cache_read_input_tokens", out var startCacheRead))
                    {
                        cacheReadTokens = startCacheRead;
                    }
                    break;

                case "message_delta" when evt["usage"] is JsonObject deltaUsage:
                    if (TryGetInt(deltaUsage, "output_tokens", out var outputTokens))
                    {
                        completionTokens = outputTokens;
                    }
                    if (TryGetInt(deltaUsage, "cache_creation_input_tokens", out var deltaCacheCreation))
                    {
                        cacheCreationTokens = deltaCacheCreation;
                    }
                    if (TryGetInt(deltaUsage, "cache_read_input_tokens", out var deltaCacheRead))
                    {
                        cacheReadTokens = deltaCacheRead;
                    }
                    break;
            }
        }

        if (!haveInput)
        {
            return false;
        }

        usage = new UsageInfo(promptTokens, completionTokens, cacheCreationTokens, cacheReadTokens);
        return true;
    }

    /// <summary>
    /// Attempts to read input, output, and cache token counts from a top-level "usage" JSON object. Cache
    /// fields default to 0 when absent - older responses simply predate prompt caching, which is not a
    /// parse failure.
    /// </summary>
    private static bool TryReadTopLevelUsage(JsonObject usageObj, out UsageInfo usage)
    {
        usage = default;

        if (!TryGetInt(usageObj, "input_tokens", out var inputTokens) ||
            !TryGetInt(usageObj, "output_tokens", out var outputTokens))
        {
            return false;
        }

        TryGetInt(usageObj, "cache_creation_input_tokens", out var cacheCreationTokens);
        TryGetInt(usageObj, "cache_read_input_tokens", out var cacheReadTokens);

        usage = new UsageInfo(inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);
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

