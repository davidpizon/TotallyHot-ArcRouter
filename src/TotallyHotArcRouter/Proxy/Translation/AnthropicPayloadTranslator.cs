using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Translates OpenAI-shaped chat-completion payloads to/from Anthropic's native Messages API shape
/// (PLAN.md TODO 4's "Unified API Translation" pillar, Anthropic retrofit,
/// <c>docs/router/unified-api-translation.md</c> §4.4). Field mappings mirror LiteLLM's Anthropic
/// transformation (the parity reference used throughout this pillar), scoped to the surface this
/// pillar needs: text messages, function/tool calling, extended-thinking round-tripping, and the
/// common generation parameters.
///
/// <para>
/// <b>Unlike every other translated provider (Ollama, Gemini), "anthropic" is dual-mode</b>: the same
/// provider key already serves real Claude Code production traffic, which sends Anthropic-native
/// requests directly to <c>POST /v1/messages</c> and must keep passing through byte-for-byte exactly as
/// it did before this translator existed. <see cref="ShouldTranslate"/> is the seam that tells the two
/// apart - by request path, not by sniffing the body - so a client already speaking Anthropic natively
/// is never touched, and only a client sending an OpenAI-shaped request (e.g. to
/// <c>/v1/chat/completions</c>) gets translated.
/// </para>
///
/// <para>
/// <b>Deliberately out of scope</b> (documented, not silently dropped): image/document content blocks,
/// prompt caching (<c>cache_control</c>), and Anthropic's built-in tools (web search, code execution,
/// computer use, etc.). A request carrying those still translates - the unsupported parts are ignored
/// rather than erroring - but faithful translation of them is future work, mirroring how Gemini's PR
/// scoped its own gaps honestly rather than over-claiming.
/// </para>
/// </summary>
public sealed class AnthropicPayloadTranslator : IPayloadTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // Anthropic requires max_tokens on every request; OpenAI clients often omit it (or send neither
    // max_tokens nor max_completion_tokens). This floor keeps a request from being rejected outright
    // rather than guessing at what the client actually wanted - documented, not silent.
    private const int DefaultMaxTokens = 4096;

    /// <inheritdoc />
    public string Provider => "anthropic";

    /// <inheritdoc />
    public bool ShouldTranslate(HttpRequest request) => !IsNativeMessagesPath(request.Path);

    /// <summary>Matches Anthropic's own Messages API path, case-insensitively, tolerating a trailing slash - the request-path detection strategy chosen for this dual-mode provider (§4.4's "first open question").</summary>
    private static bool IsNativeMessagesPath(PathString path) =>
        string.Equals(path.Value?.TrimEnd('/'), "/v1/messages", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        // Unlike Gemini, Anthropic encodes neither the model id nor the streaming choice in the URL -
        // both live in the body (model, stream) - so the target is always the same fixed path.
        var trimmedBase = baseUrl.ToString().TrimEnd('/');
        return new Uri($"{trimmedBase}/v1/messages", UriKind.Absolute);
    }

    /// <inheritdoc />
    public byte[] TranslateRequest(byte[] openAiShapedBody)
    {
        var root = JsonNode.Parse(openAiShapedBody) as JsonObject
            ?? throw new JsonException("Anthropic request translation expected a JSON object body.");

        var anthropic = new JsonObject();

        // RequestInterceptor has already rewritten "model" to the resolved provider model id before the
        // body reaches here (see RequestInterceptor.ResolveModelRouteAsync) - copied through as-is.
        if (root["model"]?.GetValue<string>() is { Length: > 0 } model)
        {
            anthropic["model"] = model;
        }

        var messages = root["messages"] as JsonArray ?? new JsonArray();
        var (system, translatedMessages) = TranslateMessages(messages);

        if (system is not null)
        {
            anthropic["system"] = system;
        }

        anthropic["messages"] = translatedMessages;

        if (TranslateTools(root["tools"] as JsonArray) is { } tools)
        {
            anthropic["tools"] = tools;
        }

        if (TranslateToolChoice(root["tool_choice"]) is { } toolChoice)
        {
            anthropic["tool_choice"] = toolChoice;
        }

        if (root["temperature"] is JsonNode temperature)
        {
            anthropic["temperature"] = temperature.DeepClone();
        }

        if (root["top_p"] is JsonNode topP)
        {
            anthropic["top_p"] = topP.DeepClone();
        }

        if (root["top_k"] is JsonNode topK)
        {
            anthropic["top_k"] = topK.DeepClone();
        }

        // Anthropic's max_tokens is mandatory, unlike OpenAI's optional max_tokens/max_completion_tokens.
        anthropic["max_tokens"] = (root["max_tokens"] ?? root["max_completion_tokens"]) is JsonNode maxTokens
            ? maxTokens.DeepClone()
            : DefaultMaxTokens;

        // OpenAI `stop` is a string, an array of strings, or null. Anthropic's stop_sequences must be a
        // string array, so emit only string / string-array values.
        switch (root["stop"])
        {
            case JsonValue stopValue when stopValue.TryGetValue<string>(out var stopString):
                anthropic["stop_sequences"] = new JsonArray { stopString };
                break;
            case JsonArray stopArray:
                var stopSequences = new JsonArray();
                foreach (var element in stopArray)
                {
                    if (element is JsonValue value && value.TryGetValue<string>(out var sequence))
                    {
                        stopSequences.Add(sequence);
                    }
                }

                if (stopSequences.Count > 0)
                {
                    anthropic["stop_sequences"] = stopSequences;
                }

                break;
        }

        // Anthropic's own streaming signal is a body field (unlike Gemini's URL-encoded choice), and it
        // happens to share OpenAI's exact name and boolean semantics - passed straight through.
        if (root["stream"] is JsonNode stream)
        {
            anthropic["stream"] = stream.DeepClone();
        }

        // Extended-thinking opt-in has no OpenAI-standard field; a client that wants it sends Anthropic's
        // own already-shaped `thinking: {type, budget_tokens}` object as a pass-through extension field
        // (mirrors LiteLLM accepting **kwargs straight through), forwarded verbatim.
        if (root["thinking"] is JsonObject thinkingParam)
        {
            anthropic["thinking"] = thinkingParam.DeepClone();
        }

        return JsonSerializer.SerializeToUtf8Bytes(anthropic, SerializerOptions);
    }

    /// <summary>
    /// Splits OpenAI <c>messages</c> into Anthropic's top-level <c>system</c> string plus the
    /// <c>messages</c> array, merging consecutive same-role turns (Anthropic, like Gemini, requires
    /// alternating user/assistant roles) and mapping tool calls/results to tool_use/tool_result blocks.
    /// Internal (not private) so <c>AnthropicOnBedrockPayloadTranslator</c> can reuse it verbatim -
    /// Bedrock's Claude Messages API body is identical here, differing only in its top-level envelope
    /// (no "model" field, "anthropic_version" instead of a header).
    /// </summary>
    internal static (string? System, JsonArray Messages) TranslateMessages(JsonArray messages)
    {
        var systemParts = new List<string>();
        var result = new JsonArray();

        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message)
            {
                continue;
            }

            var role = message["role"]?.GetValue<string>();

            switch (role)
            {
                case "system":
                    if (ExtractText(message["content"]) is { Length: > 0 } systemText)
                    {
                        systemParts.Add(systemText);
                    }

                    break;

                case "assistant":
                    AppendAssistantContent(result, message);
                    break;

                case "tool":
                case "function":
                    AppendToolResult(result, message);
                    break;

                case "user":
                default:
                    AppendUserContent(result, message);
                    break;
            }
        }

        // Anthropic rejects an empty messages array; guard with a blank user turn, mirroring Gemini's
        // own empty-contents guard.
        if (result.Count == 0)
        {
            AppendMergedContent(result, "user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = " " } });
        }

        var system = systemParts.Count > 0 ? string.Join("\n\n", systemParts) : null;
        return (system, result);
    }

    /// <summary>Appends a user message's text as a single Anthropic text content block, substituting a blank space when the text is empty since Anthropic rejects empty text blocks.</summary>
    private static void AppendUserContent(JsonArray messages, JsonObject message)
    {
        var text = ExtractText(message["content"]);
        var block = new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrEmpty(text) ? " " : text };
        AppendMergedContent(messages, "user", new JsonArray { block });
    }

    /// <summary>Builds an assistant turn's Anthropic content blocks in required order: thinking/reasoning blocks first, then text, then tool_use blocks converted from OpenAI-style tool_calls.</summary>
    private static void AppendAssistantContent(JsonArray messages, JsonObject message)
    {
        var blocks = new JsonArray();

        // Extended-thinking round trip (LiteLLM's reasoning_content/thinking_blocks convention):
        // Anthropic requires thinking blocks to be the first content blocks of an assistant turn.
        if (message["thinking_blocks"] is JsonArray thinkingBlocks)
        {
            foreach (var thinkingBlockNode in thinkingBlocks)
            {
                if (thinkingBlockNode is JsonObject thinkingBlock)
                {
                    blocks.Add(thinkingBlock.DeepClone());
                }
            }
        }
        else if (message["reasoning_content"] is JsonValue reasoningValue && reasoningValue.TryGetValue<string>(out var reasoningText) && reasoningText.Length > 0)
        {
            // A client kept only the plain reasoning text, not the raw thinking_blocks (with its
            // signature). Reconstruct a best-effort thinking block so the text isn't silently dropped;
            // this is not a substitute for a verifiable signature on a genuinely new thinking turn.
            blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoningText });
        }

        if (ExtractText(message["content"]) is { Length: > 0 } text)
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var toolCallNode in toolCalls)
            {
                if (toolCallNode is not JsonObject toolCall || toolCall["function"] is not JsonObject function)
                {
                    continue;
                }

                var name = function["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var id = toolCall["id"]?.GetValue<string>() is { Length: > 0 } toolCallId
                    ? toolCallId
                    : $"toolu_{Guid.NewGuid():N}";

                blocks.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = id,
                    ["name"] = name,
                    ["input"] = ParseArgumentsObject(function["arguments"]),
                });
            }
        }

        if (blocks.Count == 0)
        {
            return;
        }

        AppendMergedContent(messages, "assistant", blocks);
    }

    /// <summary>Appends a tool result as an Anthropic tool_result block on a user turn, falling back to a plain labeled text block when the message lacks a tool_call_id so the content isn't silently dropped.</summary>
    private static void AppendToolResult(JsonArray messages, JsonObject message)
    {
        var content = ExtractText(message["content"]) ?? string.Empty;

        // Anthropic requires a non-empty tool_use_id on every tool_result block; a legacy role:"function"
        // message (or a malformed/partial client payload) may omit tool_call_id. Emitting a tool_result
        // with an empty id would produce an invalid upstream request Anthropic 400s on, so fall back to a
        // plain labeled text block instead - keeps the content instead of silently dropping it, without
        // claiming a tool-result linkage that doesn't exist.
        if (message["tool_call_id"]?.GetValue<string>() is { Length: > 0 } toolUseId)
        {
            // Tool results are carried on a "user" turn in Anthropic's alternation model, same as Gemini's
            // functionResponse.
            var block = new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = content,
            };

            AppendMergedContent(messages, "user", new JsonArray { block });
            return;
        }

        if (content.Length > 0)
        {
            AppendMergedContent(messages, "user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = $"Tool result: {content}" } });
        }
    }

    /// <summary>Appends blocks to the last message when it shares <paramref name="role"/>, else starts a new message - the consecutive-role merge Anthropic requires.</summary>
    private static void AppendMergedContent(JsonArray messages, string role, JsonArray blocks)
    {
        if (messages.Count > 0 &&
            messages[^1] is JsonObject last &&
            last["role"]?.GetValue<string>() == role &&
            last["content"] is JsonArray existingBlocks)
        {
            foreach (var block in blocks.ToArray())
            {
                block!.Parent!.AsArray().Remove(block);
                existingBlocks.Add(block);
            }

            return;
        }

        messages.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
    }

    /// <summary>Maps OpenAI <c>tools</c> to Anthropic's <c>{name, description, input_schema}</c> shape; returns null when there are no function tools. Internal so <c>AnthropicOnBedrockPayloadTranslator</c> can reuse it - Bedrock's Claude tool shape is identical.</summary>
    internal static JsonArray? TranslateTools(JsonArray? tools)
    {
        if (tools is null)
        {
            return null;
        }

        var result = new JsonArray();

        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool || tool["function"] is not JsonObject function)
            {
                continue;
            }

            var name = function["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var entry = new JsonObject { ["name"] = name };

            if (function["description"]?.GetValue<string>() is { Length: > 0 } description)
            {
                entry["description"] = description;
            }

            // Unlike Gemini's OpenAPI-subset schema, Anthropic's input_schema is regular JSON Schema -
            // no keyword stripping needed, copied through as-is.
            entry["input_schema"] = function["parameters"] is JsonObject parameters
                ? parameters.DeepClone()
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            result.Add(entry);
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>Internal so <c>AnthropicOnBedrockPayloadTranslator</c> can reuse it - Bedrock's Claude tool_choice shape is identical.</summary>
    internal static JsonObject? TranslateToolChoice(JsonNode? toolChoice) => toolChoice switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s switch
        {
            "auto" => new JsonObject { ["type"] = "auto" },
            "none" => new JsonObject { ["type"] = "none" },
            "required" => new JsonObject { ["type"] = "any" },
            _ => null,
        },
        JsonObject obj when obj["function"] is JsonObject fn && fn["name"]?.GetValue<string>() is { Length: > 0 } name =>
            new JsonObject { ["type"] = "tool", ["name"] = name },
        _ => null,
    };

    /// <inheritdoc />
    public byte[] TranslateResponse(byte[] nativeShapedBody)
    {
        var root = JsonNode.Parse(nativeShapedBody) as JsonObject
            ?? throw new JsonException("Anthropic response translation expected a JSON object body.");

        var openAi = new JsonObject
        {
            ["id"] = root["id"]?.GetValue<string>() ?? GenerateCompletionId(),
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = root["model"]?.GetValue<string>() ?? string.Empty,
        };

        var message = new JsonObject { ["role"] = "assistant" };
        var contentText = new StringBuilder();
        var reasoningText = new StringBuilder();
        JsonArray? thinkingBlocks = null;
        var toolCalls = new JsonArray();

        if (root["content"] is JsonArray content)
        {
            foreach (var blockNode in content)
            {
                if (blockNode is not JsonObject block)
                {
                    continue;
                }

                switch (block["type"]?.GetValue<string>())
                {
                    case "text":
                        if (block["text"]?.GetValue<string>() is { } text)
                        {
                            contentText.Append(text);
                        }

                        break;

                    case "tool_use":
                        toolCalls.Add(BuildToolCall(block, toolCalls.Count));
                        break;

                    case "thinking":
                        if (block["thinking"]?.GetValue<string>() is { } thinking)
                        {
                            reasoningText.Append(thinking);
                        }

                        thinkingBlocks ??= new JsonArray();
                        thinkingBlocks.Add(block.DeepClone());
                        break;

                    case "redacted_thinking":
                        thinkingBlocks ??= new JsonArray();
                        thinkingBlocks.Add(block.DeepClone());
                        break;
                }
            }
        }

        var hasToolCalls = toolCalls.Count > 0;

        // OpenAI content is null (not "") when the turn is purely tool calls.
        message["content"] = contentText.Length > 0 || !hasToolCalls ? contentText.ToString() : null;

        // reasoning_content/thinking_blocks: LiteLLM's standardized cross-provider reasoning
        // representation - the string is the portable summary, thinking_blocks (with its signature) is
        // what must be resent verbatim on a later turn for Anthropic to accept it back.
        if (reasoningText.Length > 0)
        {
            message["reasoning_content"] = reasoningText.ToString();
        }

        if (thinkingBlocks is not null)
        {
            message["thinking_blocks"] = thinkingBlocks;
        }

        if (hasToolCalls)
        {
            message["tool_calls"] = toolCalls;
        }

        var finishReason = MapStopReason(root["stop_reason"]?.GetValue<string>(), hasToolCalls);

        openAi["choices"] = new JsonArray
        {
            new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finishReason },
        };

        if (root["usage"] is JsonObject usage)
        {
            openAi["usage"] = TranslateUsage(usage);
        }

        return JsonSerializer.SerializeToUtf8Bytes(openAi, SerializerOptions);
    }

    /// <summary>Converts an Anthropic tool_use content block into an OpenAI-shaped tool_calls entry, generating a synthetic id when the block is missing one and serializing the input object to a JSON string for the arguments field.</summary>
    private static JsonObject BuildToolCall(JsonObject block, int index)
    {
        var id = block["id"]?.GetValue<string>() is { Length: > 0 } blockId ? blockId : $"toolu_{Guid.NewGuid():N}";
        var name = block["name"]?.GetValue<string>() ?? string.Empty;
        var arguments = block["input"] is JsonNode inputNode ? inputNode.ToJsonString(SerializerOptions) : "{}";

        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "function",
            ["index"] = index,
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments,
            },
        };
    }

    /// <summary>Maps Anthropic's <c>input_tokens</c>/<c>output_tokens</c> usage fields to OpenAI's <c>prompt_tokens</c>/<c>completion_tokens</c>/<c>total_tokens</c> shape.</summary>
    private static JsonObject TranslateUsage(JsonObject usage)
    {
        var promptTokens = usage["input_tokens"]?.GetValue<int>() ?? 0;
        var completionTokens = usage["output_tokens"]?.GetValue<int>() ?? 0;

        return new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = promptTokens + completionTokens,
        };
    }

    /// <summary>Maps an Anthropic <c>stop_reason</c> to OpenAI's <c>finish_reason</c>, mirroring LiteLLM's Anthropic finish-reason mapping. A turn carrying any tool_use block always reports "tool_calls", matching OpenAI's own convention regardless of the raw stop_reason.</summary>
    internal static string MapStopReason(string? stopReason, bool hasToolCalls)
    {
        if (hasToolCalls)
        {
            return "tool_calls";
        }

        return stopReason switch
        {
            null or "" => "stop",
            "end_turn" or "stop_sequence" => "stop",
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            "pause_turn" => "stop",
            "refusal" => "content_filter",
            _ => "stop",
        };
    }

    /// <inheritdoc />
    public IStreamTranslator CreateStreamTranslator() => new AnthropicStreamTranslator();

    // --- shared helpers, also used by AnthropicStreamTranslator ---

    /// <summary>Extracts plain text from an OpenAI message <c>content</c> field (a string, or an array of content blocks - only <c>text</c> blocks contribute).</summary>
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
                {
                    if (element is JsonObject obj &&
                        obj["type"]?.GetValue<string>() == "text" &&
                        obj["text"]?.GetValue<string>() is { } partText)
                    {
                        builder.Append(partText);
                    }
                }

                return builder.ToString();
            default:
                return null;
        }
    }

    /// <summary>Parses an OpenAI tool-call <c>arguments</c> string (JSON text) into an Anthropic <c>input</c> object; falls back to an empty object.</summary>
    internal static JsonNode ParseArgumentsObject(JsonNode? arguments)
    {
        if (arguments is JsonObject alreadyObject)
        {
            return alreadyObject.DeepClone();
        }

        if (arguments is JsonValue value && value.TryGetValue<string>(out var text) &&
            TryParseJsonObject(text) is { } parsed)
        {
            return parsed;
        }

        return new JsonObject();
    }

    /// <summary>Attempts to parse <paramref name="text"/> as a JSON object, returning null for blank input or a JSON value that isn't an object (e.g. malformed JSON or a non-object arguments payload) instead of throwing.</summary>
    internal static JsonObject? TryParseJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Generates a unique OpenAI-style completion id (<c>chatcmpl-&lt;guid&gt;</c>) for use when the upstream response omits one.</summary>
    internal static string GenerateCompletionId() =>
        "chatcmpl-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}

