using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Cache;

/// <summary>
/// Decides whether a request may participate in the semantic cache, and builds the scope key that
/// keeps incompatible answers from being reused.
/// </summary>
/// <remarks>
/// Similarity answers "does this question mean the same thing?". The scope key answers "would the
/// provider have been asked the same thing?": the resolved provider and provider model id (so two
/// client-facing aliases of one upstream model share an entry, and two different upstream models never
/// do), plus a canonical hash of every generation setting and every message except the newest user
/// message's text. That text is what the routing embedding already represents. A different temperature,
/// system prompt, or earlier turn is a different scope even when the newest question matches.
/// </remarks>
internal static class SemanticCacheScope
{
    private const string UserTextPlaceholder = "\u0000semantic-cache-user\u0000";

    /// <summary>
    /// Builds the scope key for <paramref name="body"/>, or reports why the request must not be cached.
    /// </summary>
    /// <param name="body">The rewritten request JSON (model id already substituted).</param>
    /// <param name="provider">The resolved provider key.</param>
    /// <param name="providerModelId">The upstream model id the alias resolved to.</param>
    /// <param name="scopeKey">The scope key when this returns <see langword="true"/>.</param>
    /// <param name="refusalReason">A stable reason code when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the request is eligible to look up and, later, to store.</returns>
    public static bool TryCreateScopeKey(
        JsonObject body,
        string provider,
        string providerModelId,
        out string scopeKey,
        out string refusalReason)
    {
        scopeKey = string.Empty;
        refusalReason = "ineligible";

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerModelId))
        {
            refusalReason = "no-model";
            return false;
        }

        if (IsStreaming(body))
        {
            refusalReason = "streaming";
            return false;
        }

        if (body["tools"] is JsonArray { Count: > 0 })
        {
            refusalReason = "tools";
            return false;
        }

        if (DependsOnToolChoice(body["tool_choice"]))
        {
            refusalReason = "tool-choice";
            return false;
        }

        if (body["messages"] is not JsonArray messages)
        {
            refusalReason = "no-user-message";
            return false;
        }

        if (MessagesDependOnTools(messages))
        {
            refusalReason = "tool-history";
            return false;
        }

        var userIndex = FindNewestUserMessageIndex(messages);
        if (userIndex < 0)
        {
            refusalReason = "no-user-message";
            return false;
        }

        if (messages[userIndex] is not JsonObject userMessage || !IsPlainTextContent(userMessage["content"]))
        {
            refusalReason = "structured-user-content";
            return false;
        }

        var clone = JsonNode.Parse(body.ToJsonString()) as JsonObject;
        if (clone?["messages"] is not JsonArray clonedMessages || clonedMessages[userIndex] is not JsonObject clonedUser)
        {
            refusalReason = "unreadable-body";
            return false;
        }

        clone.Remove("model");
        clonedUser["content"] = UserTextPlaceholder;

        var canonical = CanonicalJson(clone);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        scopeKey = string.Join(separator: "\u001f",
            provider.Trim().ToLowerInvariant(),
            providerModelId.Trim(),
            fingerprint);
        refusalReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Reports whether <paramref name="responseBody"/> is a complete, tool-free success payload safe to
    /// replay. Streaming captures, error envelopes, and tool-call answers return <see langword="false"/>.
    /// </summary>
    /// <param name="responseBody">The bytes that were written to the client.</param>
    /// <param name="refusalReason">A stable reason code when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the body may be stored.</returns>
    public static bool IsReusableAnswer(byte[] responseBody, out string refusalReason)
    {
        refusalReason = "not-json";
        if (responseBody.Length == 0)
        {
            refusalReason = "empty";
            return false;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(responseBody);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject root)
        {
            refusalReason = "not-json-object";
            return false;
        }

        if (root["error"] is not null)
        {
            refusalReason = "error-body";
            return false;
        }

        if (ContainsToolCall(root))
        {
            refusalReason = "tool-call-answer";
            return false;
        }

        refusalReason = string.Empty;
        return true;
    }

    /// <summary>Writes <paramref name="node"/> with object keys sorted so key order cannot change the hash.</summary>
    private static string CanonicalJson(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer: writer, node: node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Recursively writes one JSON node in canonical form.</summary>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer: writer, node: property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                    WriteCanonical(writer: writer, node: item);

                writer.WriteEndArray();
                break;
            case JsonValue value:
                writer.WriteRawValue(value.ToJsonString());
                break;
        }
    }

    /// <summary>True when the client asked for a streamed response, which this cache never stores.</summary>
    private static bool IsStreaming(JsonObject body)
    {
        return body["stream"] is JsonValue value && value.TryGetValue<bool>(out var stream) && stream;
    }

    /// <summary>
    /// True when <paramref name="toolChoice"/> asks the model to call a tool. Absent and <c>"none"</c>
    /// do not.
    /// </summary>
    private static bool DependsOnToolChoice(JsonNode? toolChoice)
    {
        if (toolChoice is null) return false;

        if (toolChoice is JsonValue value && value.TryGetValue<string>(out var text))
            return !text.Equals(value: "none", comparisonType: StringComparison.OrdinalIgnoreCase);

        return true;
    }

    /// <summary>True when history already contains a tool call or tool result.</summary>
    private static bool MessagesDependOnTools(JsonArray messages)
    {
        foreach (var node in messages)
        {
            if (node is not JsonObject message) continue;

            var role = ReadString(message["role"]);
            if (role is not null && role.Equals(value: "tool", comparisonType: StringComparison.OrdinalIgnoreCase))
                return true;

            if (message["tool_calls"] is JsonArray { Count: > 0 }) return true;

            if (message["function_call"] is JsonObject) return true;

            if (message["content"] is JsonArray parts && parts.Any(ContainsToolCall)) return true;
        }

        return false;
    }

    /// <summary>Index of the last message whose role is <c>user</c>, or -1.</summary>
    private static int FindNewestUserMessageIndex(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message) continue;

            var role = ReadString(message["role"]);
            if (role is not null && role.Equals(value: "user", comparisonType: StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>True when <paramref name="content"/> is a JSON string the embedding already covers.</summary>
    private static bool IsPlainTextContent(JsonNode? content)
    {
        return content is JsonValue value && value.TryGetValue<string>(out _);
    }

    /// <summary>True when <paramref name="node"/> or a descendant is a tool call or tool result.</summary>
    private static bool ContainsToolCall(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["tool_calls"] is JsonArray { Count: > 0 }) return true;

                if (obj["function_call"] is JsonObject) return true;

                var type = ReadString(obj["type"]);
                if (type is not null &&
                    (type.Equals(value: "tool_use", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                     type.Equals(value: "tool_result", comparisonType: StringComparison.OrdinalIgnoreCase)))
                    return true;

                return obj.Any(property => ContainsToolCall(property.Value));
            case JsonArray array:
                return array.Any(ContainsToolCall);
            default:
                return false;
        }
    }

    /// <summary>Reads a JSON string, or <see langword="null"/> when the node is not one.</summary>
    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
