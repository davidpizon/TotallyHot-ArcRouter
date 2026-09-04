using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Translates OpenAI-shaped chat-completion payloads to/from Google Gemini's native
/// <c>generateContent</c> shape (Google AI Studio, <c>generativelanguage.googleapis.com</c> - not
/// Vertex AI). The Gemini slice of unified API translation
/// (<c>docs/router/unified-api-translation.md</c> §4.3). Field mappings mirror LiteLLM's pinned
/// <c>vertex_ai/gemini</c> transformation (the parity reference), scoped to the surface this pillar
/// needs: text messages, function/tool calling, and the common generation parameters.
/// <para>
/// <b>Deliberately out of scope</b> (documented, not silently dropped): image/audio/file content
/// blocks, reasoning/thinking blocks and thought signatures, context caching, safety settings,
/// response schema beyond a plain JSON mime type, and Gemini's built-in tools (googleSearch,
/// codeExecution, etc.). A request carrying those still translates - the unsupported parts are
/// ignored rather than erroring - but faithful translation of them is future work, mirroring how the
/// other pillars were scoped honestly rather than over-claimed.
/// </para>
/// </summary>
public sealed class GeminiPayloadTranslator : IPayloadTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public string Provider => "gemini";

    /// <inheritdoc/>
    public Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerModelId);

        // Gemini AI Studio: POST {base}/v1beta/models/{model}:generateContent, or
        // :streamGenerateContent?alt=sse for a server-sent-events stream. The model id lives in the
        // path (not the body), and the ":method" suffix is a literal part of the resource name, so it
        // must not be percent-encoded - hence string composition rather than Uri(Uri, string).
        var method = isStreaming ? "streamGenerateContent" : "generateContent";
        var trimmedBase = baseUrl.ToString().TrimEnd('/');
        var url = $"{trimmedBase}/v1beta/models/{providerModelId}:{method}";
        if (isStreaming) url += "?alt=sse";

        return new Uri(uriString: url, uriKind: UriKind.Absolute);
    }

    /// <inheritdoc/>
    public byte[] TranslateRequest(byte[] openAiShapedBody)
    {
        var root = JsonNode.Parse(openAiShapedBody) as JsonObject
                   ?? throw new JsonException("Gemini request translation expected a JSON object body.");

        var gemini = new JsonObject();

        var messages = root["messages"] as JsonArray ?? new JsonArray();
        var (systemInstruction, contents) = TranslateMessages(messages);

        if (systemInstruction is not null) gemini["system_instruction"] = systemInstruction;

        gemini["contents"] = contents;

        if (TranslateTools(root["tools"] as JsonArray) is { } tools) gemini["tools"] = tools;

        if (TranslateToolChoice(root["tool_choice"]) is { } toolConfig) gemini["toolConfig"] = toolConfig;

        if (TranslateGenerationConfig(root) is { } generationConfig) gemini["generationConfig"] = generationConfig;

        return JsonSerializer.SerializeToUtf8Bytes(value: gemini, options: SerializerOptions);
    }

    /// <inheritdoc/>
    public byte[] TranslateResponse(byte[] nativeShapedBody)
    {
        var root = JsonNode.Parse(nativeShapedBody) as JsonObject
                   ?? throw new JsonException("Gemini response translation expected a JSON object body.");

        var openAi = new JsonObject
        {
            ["id"] = root["responseId"]?.GetValue<string>() ?? PayloadTranslationHelpers.GenerateCompletionId(),
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = root["modelVersion"]?.GetValue<string>() ?? string.Empty
        };

        var choices = new JsonArray();
        if (root["candidates"] is JsonArray candidates)
            foreach (var candidateNode in candidates)
                if (candidateNode is JsonObject candidate)
                    choices.Add(TranslateCandidate(candidate));

        openAi["choices"] = choices;

        if (root["usageMetadata"] is JsonObject usageMetadata) openAi["usage"] = TranslateUsage(usageMetadata);

        return JsonSerializer.SerializeToUtf8Bytes(value: openAi, options: SerializerOptions);
    }

    /// <inheritdoc/>
    public IStreamTranslator CreateStreamTranslator()
    {
        return new GeminiStreamTranslator();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 400 only. Gemini's 429s are ordinary rate-limit responses with no embedded envelope worth
    /// decoding, and opting into them would buffer a body that is currently streamed - see
    /// <see cref="IPayloadTranslator.HandlesEmbeddedErrorAt"/>'s remarks on why this is deliberately
    /// narrower than "every error status".
    /// </remarks>
    public bool HandlesEmbeddedErrorAt(int statusCode)
    {
        return statusCode == StatusCodes.Status400BadRequest;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sets <see cref="EmbeddedProviderError.IsAuthFailure"/> for Gemini's disguised-401 case: an
    /// invalid API key comes back as a 400 whose envelope carries <c>UNAUTHENTICATED</c> (or, on some
    /// surfaces, only the message "API key not valid"), which must trip the provider-wide circuit
    /// breaker the way a real 401 does rather than counting as a per-request client fault.
    /// </remarks>
    public bool TryExtractEmbeddedError(byte[] body, out EmbeddedProviderError error)
    {
        if (!TryExtractEmbeddedError(body: body, status: out var status, message: out var message))
        {
            error = default;
            return false;
        }

        error = new EmbeddedProviderError(
            Status: status,
            Message: message,
            IsAuthFailure: string.Equals(a: status, b: "UNAUTHENTICATED", comparisonType: StringComparison.Ordinal) ||
                           message.Contains(value: "API key not valid",
                               comparisonType: StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>
    /// Splits OpenAI <c>messages</c> into Gemini's top-level <c>system_instruction</c> plus the
    /// <c>contents</c> array, merging consecutive same-role turns (Gemini, like Anthropic, requires
    /// alternating user/model roles) and mapping tool calls/results to functionCall/functionResponse
    /// parts.
    /// </summary>
    private static (JsonObject? SystemInstruction, JsonArray Contents) TranslateMessages(JsonArray messages)
    {
        var systemParts = new JsonArray();
        var contents = new JsonArray();

        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message) continue;

            var role = message["role"]?.GetValue<string>();

            switch (role)
            {
                case "system":
                    if (PayloadTranslationHelpers.ExtractText(message["content"]) is { Length: > 0 } systemText)
                        systemParts.Add(new JsonObject { ["text"] = systemText });

                    break;

                case "assistant":
                    AppendAssistantContent(contents: contents, message: message);
                    break;

                case "tool":
                case "function":
                    AppendToolResult(contents: contents, message: message);
                    break;

                // ReSharper disable once RedundantCaseLabel
                // "user" is listed even though `default` already catches it: it documents the expected
                // role alongside the catch-all, rather than leaving readers to infer that the normal
                // case and the unknown-role fallback happen to share a body.
                case "user":
                default:
                    AppendTextContent(contents: contents, role: "user",
                        text: PayloadTranslationHelpers.ExtractText(message["content"]));
                    break;
            }
        }

        var systemInstruction = systemParts.Count > 0
            ? new JsonObject { ["parts"] = systemParts }
            : null;

        // Gemini rejects an empty contents array; LiteLLM inserts a blank user turn as a guard.
        if (contents.Count == 0) AppendTextContent(contents: contents, role: "user", text: " ");

        return (systemInstruction, contents);
    }

    /// <summary>
    /// Builds a "model" turn's Gemini parts from an assistant message's text and OpenAI-style tool_calls, converted
    /// into functionCall parts.
    /// </summary>
    private static void AppendAssistantContent(JsonArray contents, JsonObject message)
    {
        var parts = new JsonArray();

        if (PayloadTranslationHelpers.ExtractText(message["content"]) is { Length: > 0 } text)
            parts.Add(new JsonObject { ["text"] = text });

        if (message["tool_calls"] is JsonArray toolCalls)
            foreach (var toolCallNode in toolCalls)
            {
                if (toolCallNode is not JsonObject toolCall ||
                    toolCall["function"] is not JsonObject function) continue;

                var name = function["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;

                parts.Add(new JsonObject
                {
                    ["functionCall"] = new JsonObject
                    {
                        ["name"] = name,
                        ["args"] = PayloadTranslationHelpers.ParseArgumentsObject(function["arguments"])
                    }
                });
            }

        if (parts.Count == 0) return;

        AppendMergedContent(contents: contents, role: "model", parts: parts);
    }

    /// <summary>
    /// Appends a tool result as a Gemini functionResponse part on a user turn, wrapping a plain-string result under a
    /// conventional "content" key when it isn't already valid JSON.
    /// </summary>
    private static void AppendToolResult(JsonArray contents, JsonObject message)
    {
        var name = message["name"]?.GetValue<string>() ?? string.Empty;
        var responseText = PayloadTranslationHelpers.ExtractText(message["content"]) ?? string.Empty;

        // Gemini's functionResponse.response is an object; wrap a plain string result under a
        // conventional "content" key (a raw JSON object result is passed through as-is).
        JsonNode responseValue = PayloadTranslationHelpers.TryParseJsonObject(responseText) is { } parsed
            ? parsed
            : new JsonObject { ["content"] = responseText };

        var part = new JsonObject
        {
            ["functionResponse"] = new JsonObject
            {
                ["name"] = name,
                ["response"] = responseValue
            }
        };

        // Tool results are carried on a "user" turn in Gemini's alternation model.
        AppendMergedContent(contents: contents, role: "user", parts: new JsonArray { part });
    }

    /// <summary>
    /// Appends a single text part for the given role, substituting a blank space when the text is empty since Gemini
    /// rejects a turn with no text part.
    /// </summary>
    private static void AppendTextContent(JsonArray contents, string role, string? text)
    {
        // Gemini fails a user turn with no text part; keep a blank space as the floor (LiteLLM does the
        // same to avoid "must have a text parameter" errors).
        var parts = new JsonArray { new JsonObject { ["text"] = string.IsNullOrEmpty(text) ? " " : text } };
        AppendMergedContent(contents: contents, role: role, parts: parts);
    }

    /// <summary>
    /// Appends parts to the last content when it shares <paramref name="role"/>, else starts a new content - the
    /// consecutive-role merge Gemini requires.
    /// </summary>
    private static void AppendMergedContent(JsonArray contents, string role, JsonArray parts)
    {
        if (contents.Count > 0 &&
            contents[^1] is JsonObject last &&
            last["role"]?.GetValue<string>() == role &&
            last["parts"] is JsonArray existingParts)
        {
            foreach (var part in parts.ToArray())
            {
                part!.Parent!.AsArray().Remove(part);
                existingParts.Add(part);
            }

            return;
        }

        contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
    }

    /// <summary>
    /// Maps OpenAI <c>tools</c> to Gemini's single <c>{ functionDeclarations: [...] }</c> tool object; returns null
    /// when there are no function tools.
    /// </summary>
    private static JsonArray? TranslateTools(JsonArray? tools)
    {
        if (tools is null) return null;

        var declarations = new JsonArray();

        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool || tool["function"] is not JsonObject function) continue;

            var name = function["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;

            var declaration = new JsonObject { ["name"] = name };

            if (function["description"]?.GetValue<string>() is { Length: > 0 } description)
                declaration["description"] = description;

            if (function["parameters"] is JsonObject parameters)
                declaration["parameters"] = JsonSchemaSanitizer.ForGemini(parameters.DeepClone().AsObject());

            declarations.Add(declaration);
        }

        return declarations.Count > 0
            ? new JsonArray { new JsonObject { ["functionDeclarations"] = declarations } }
            : null;
    }

    /// <summary>
    /// Maps OpenAI <c>tool_choice</c> to Gemini's <c>functionCallingConfig.mode</c>, treating any object form (naming
    /// a specific function) as ANY; returns null when there is no equivalent mode.
    /// </summary>
    private static JsonObject? TranslateToolChoice(JsonNode? toolChoice)
    {
        // OpenAI tool_choice -> Gemini functionCallingConfig.mode. Object form ({type:function,...})
        // means "call this function" -> ANY.
        var mode = toolChoice switch
        {
            JsonValue value when value.TryGetValue<string>(out var s) => s switch
            {
                "auto" => "AUTO",
                "none" => "NONE",
                "required" => "ANY",
                _ => null
            },
            JsonObject => "ANY",
            _ => null
        };

        return mode is null
            ? null
            : new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject { ["mode"] = mode }
            };
    }

    /// <summary>
    /// Maps OpenAI's sampling and output-shaping request fields (temperature, top_p, top_k, max tokens, n, stop,
    /// response_format) to Gemini's <c>generationConfig</c> object, returning null when none of those fields were present.
    /// </summary>
    private static JsonObject? TranslateGenerationConfig(JsonObject root)
    {
        var config = new JsonObject();

        if (root["temperature"] is { } temperature) config["temperature"] = temperature.DeepClone();

        if (root["top_p"] is { } topP) config["topP"] = topP.DeepClone();

        if (root["top_k"] is { } topK) config["topK"] = topK.DeepClone();

        // OpenAI has both max_tokens (legacy) and max_completion_tokens; either maps to maxOutputTokens.
        if ((root["max_completion_tokens"] ?? root["max_tokens"]) is { } maxTokens)
            config["maxOutputTokens"] = maxTokens.DeepClone();

        if (root["n"] is { } candidateCount) config["candidateCount"] = candidateCount.DeepClone();

        // OpenAI `stop` is a string, an array of strings, or null. Gemini's stopSequences must be
        // strings, so emit only string / string-array values and ignore anything else (null, number,
        // bool) rather than forwarding a shape Gemini would reject.
        switch (root["stop"])
        {
            case JsonValue stopValue when stopValue.TryGetValue<string>(out var stopString):
                config["stopSequences"] = new JsonArray { stopString };
                break;
            case JsonArray stopArray:
                var stopSequences = new JsonArray();
                foreach (var element in stopArray)
                    if (element is JsonValue value && value.TryGetValue<string>(out var sequence))
                        stopSequences.Add(sequence);

                if (stopSequences.Count > 0) config["stopSequences"] = stopSequences;

                break;
        }

        if (root["response_format"] is JsonObject responseFormat)
        {
            var type = responseFormat["type"]?.GetValue<string>();
            if (type is "json_object" or "json_schema") config["responseMimeType"] = "application/json";

            if (responseFormat["json_schema"] is JsonObject jsonSchema && jsonSchema["schema"] is JsonObject schema)
                config["responseSchema"] = JsonSchemaSanitizer.ForGemini(schema.DeepClone().AsObject());
        }

        return config.Count > 0 ? config : null;
    }

    /// <summary>
    /// Translates one Gemini candidate into an OpenAI choice, concatenating text parts, converting functionCall parts
    /// to tool_calls, and mapping the finish reason (always "tool_calls" when any tool call is present).
    /// </summary>
    private static JsonObject TranslateCandidate(JsonObject candidate)
    {
        var contentText = new StringBuilder();
        var toolCalls = new JsonArray();

        if (candidate["content"] is JsonObject content && content["parts"] is JsonArray parts)
            foreach (var partNode in parts)
            {
                if (partNode is not JsonObject part) continue;

                if (part["text"]?.GetValue<string>() is { } text)
                    contentText.Append(text);
                else if (part["functionCall"] is JsonObject functionCall)
                    toolCalls.Add(BuildToolCall(functionCall: functionCall, index: toolCalls.Count));
            }

        var message = new JsonObject { ["role"] = "assistant" };
        var hasToolCalls = toolCalls.Count > 0;

        // OpenAI content is null (not "") when the turn is purely tool calls.
        message["content"] = contentText.Length > 0 || !hasToolCalls ? contentText.ToString() : null;
        if (hasToolCalls) message["tool_calls"] = toolCalls;

        var finishReason = hasToolCalls
            ? "tool_calls"
            : MapFinishReason(candidate["finishReason"]?.GetValue<string>());

        return new JsonObject
        {
            ["index"] = candidate["index"]?.GetValue<int>() ?? 0,
            ["message"] = message,
            ["finish_reason"] = finishReason
        };
    }

    /// <summary>
    /// Converts a Gemini functionCall part into an OpenAI-shaped tool_calls entry, generating a synthetic id and
    /// serializing the args object to a JSON string for the arguments field.
    /// </summary>
    private static JsonObject BuildToolCall(JsonObject functionCall, int index)
    {
        var name = functionCall["name"]?.GetValue<string>() ?? string.Empty;
        var args = functionCall["args"] is { } argsNode
            ? argsNode.ToJsonString(SerializerOptions)
            : "{}";

        return new JsonObject
        {
            ["id"] = $"call_{index}_{Guid.NewGuid():N}",
            ["type"] = "function",
            ["index"] = index,
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = args
            }
        };
    }

    /// <summary>
    /// Maps Gemini's <c>usageMetadata</c> token counts to OpenAI's <c>prompt_tokens</c>/<c>completion_tokens</c>/
    /// <c>total_tokens</c> shape, falling back to a computed total when Gemini omits <c>totalTokenCount</c>.
    /// </summary>
    private static JsonObject TranslateUsage(JsonObject usageMetadata)
    {
        var promptTokens = usageMetadata["promptTokenCount"]?.GetValue<int>() ?? 0;
        var completionTokens = usageMetadata["candidatesTokenCount"]?.GetValue<int>() ?? 0;
        var totalTokens = usageMetadata["totalTokenCount"]?.GetValue<int>() ?? promptTokens + completionTokens;

        return new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = totalTokens
        };
    }

    /// <summary>Maps a Gemini finishReason to OpenAI's, mirroring LiteLLM's <c>map_finish_reason</c>.</summary>
    internal static string MapFinishReason(string? geminiFinishReason)
    {
        return geminiFinishReason switch
        {
            null or "" => "stop",
            "STOP" => "stop",
            "MAX_TOKENS" => "length",
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" => "content_filter",
            "MALFORMED_FUNCTION_CALL" => "tool_calls",
            _ => "stop"
        };
    }

    /// <summary>
    /// Extracts Gemini's embedded <c>error.status</c>/<c>error.message</c> from a non-2xx response body.
    /// Handles both the buffered shape (<c>{"error": {...}}</c>, or the older array-wrapped
    /// <c>[{"error": {...}}]</c> some Google API surfaces still use) and the single-event SSE shape
    /// Gemini emits for a streaming request that errors before producing any content
    /// (<c>data: {"error": {...}}</c>). Returns false when no embedded error object is found, so the
    /// caller can fall back to forwarding the raw body unchanged.
    /// </summary>
    internal static bool TryExtractEmbeddedError(byte[] body, out string status, out string message)
    {
        status = string.Empty;
        message = string.Empty;

        var text = Encoding.UTF8.GetString(body).Trim();
        var jsonText = text.StartsWith(value: "data:", comparisonType: StringComparison.Ordinal)
            ? text[5..].Trim()
            : text;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(jsonText);
        }
        catch (JsonException)
        {
            return false;
        }

        var errorObject = parsed switch
        {
            JsonObject obj => obj["error"] as JsonObject,
            JsonArray { Count: > 0 } arr when arr[0] is JsonObject first => first["error"] as JsonObject,
            _ => null
        };

        if (errorObject is null) return false;

        status = errorObject["status"]?.GetValue<string>() ?? string.Empty;
        message = errorObject["message"]?.GetValue<string>() ?? string.Empty;
        return true;
    }

    // --- shared helpers now live in PayloadTranslationHelpers ---
}