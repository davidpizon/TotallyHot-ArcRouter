using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy.Translation;

namespace TotallyHot.ArcRouter.Proxy.Bedrock;

/// <summary>
/// Translates OpenAI-shaped chat-completion payloads to/from Amazon Titan Text's shape on Bedrock
/// (the Bedrock slice of unified API translation -
/// <c>docs/router/unified-api-translation.md</c> §4.2). Titan Text's <c>InvokeModel</c> API has no
/// structured multi-turn message concept at all - a single <c>inputText</c> string and an optional
/// <c>textGenerationConfig</c> - so, per AWS's own documented convention
/// (<c>"User: {{...}}\nBot:"</c>), OpenAI's <c>messages</c> array is folded into one
/// <c>User:</c>/<c>Bot:</c>-prefixed transcript rather than mapped onto any native structure, because
/// none exists.
/// <para>
/// <b>Deliberately out of scope</b> (a genuine capability absence in Titan's native Bedrock invoke API,
/// not a translation gap): function/tool calling, extended thinking, and any structured multi-turn role
/// concept beyond the flattened transcript above. An assistant turn's prior <c>tool_calls</c> are
/// dropped (Titan cannot act on them); a <c>role: "tool"</c> result is folded into the transcript as a
/// <c>Tool:</c>-prefixed line rather than silently dropped, so context isn't lost even though Titan has
/// no native way to distinguish it from ordinary text.
/// </para>
/// </summary>
public sealed class TitanPayloadTranslator : IBedrockPayloadTranslator
{
    // Titan's maxTokenCount is optional per AWS's docs (unlike Bedrock Claude's mandatory max_tokens),
    // but an unbounded generation is a poor default for a proxy - this floor matches the other
    // translators' documented-default-over-silent-behavior approach.
    private const int DefaultMaxTokenCount = 512;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public string Provider => "bedrock-titan";

    /// <inheritdoc/>
    public bool ShouldTranslate(HttpRequest request)
    {
        return true;
    }

    /// <inheritdoc/>
    public Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming)
    {
        throw new NotSupportedException(
            "Bedrock providers are invoked via the AWS SDK (IAmazonBedrockRuntime), not a forwarded HTTP URL.");
    }

    /// <inheritdoc/>
    public byte[] TranslateRequest(byte[] openAiShapedBody)
    {
        var root = JsonNode.Parse(openAiShapedBody) as JsonObject
                   ?? throw new JsonException("Titan request translation expected a JSON object body.");

        var messages = root["messages"] as JsonArray ?? new JsonArray();

        var titan = new JsonObject { ["inputText"] = BuildInputText(messages) };

        var config = new JsonObject();

        if (root["temperature"] is JsonNode temperature) config["temperature"] = temperature.DeepClone();

        if (root["top_p"] is JsonNode topP) config["topP"] = topP.DeepClone();

        config["maxTokenCount"] = (root["max_tokens"] ?? root["max_completion_tokens"]) is JsonNode maxTokens
            ? maxTokens.DeepClone()
            : DefaultMaxTokenCount;

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

        titan["textGenerationConfig"] = config;

        return JsonSerializer.SerializeToUtf8Bytes(value: titan, options: SerializerOptions);
    }

    /// <inheritdoc/>
    public byte[] TranslateResponse(byte[] nativeShapedBody)
    {
        var root = JsonNode.Parse(nativeShapedBody) as JsonObject
                   ?? throw new JsonException("Titan response translation expected a JSON object body.");

        var result = (root["results"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var outputText = result?["outputText"]?.GetValue<string>() ?? string.Empty;
        var promptTokens = root["inputTextTokenCount"]?.GetValue<int>() ?? 0;
        var completionTokens = result?["tokenCount"]?.GetValue<int>() ?? 0;

        var openAi = new JsonObject
        {
            ["id"] = PayloadTranslationHelpers.GenerateCompletionId(),
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            // Titan's own response body carries no model identifier to echo (unlike Claude/Gemini,
            // which return one) - the provider key is the most stable identifier available here.
            ["model"] = Provider,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = outputText },
                    ["finish_reason"] = MapCompletionReason(result?["completionReason"]?.GetValue<string>())
                }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = promptTokens + completionTokens
            }
        };

        return JsonSerializer.SerializeToUtf8Bytes(value: openAi, options: SerializerOptions);
    }

    /// <inheritdoc/>
    public IStreamTranslator CreateStreamTranslator()
    {
        throw new NotSupportedException(
            "Bedrock streaming uses CreateBedrockStreamChunkTranslator, not IStreamTranslator.");
    }

    /// <inheritdoc/>
    public IBedrockStreamChunkTranslator CreateBedrockStreamChunkTranslator()
    {
        return new TitanStreamChunkTranslator(Provider);
    }

    /// <summary>
    /// Flattens OpenAI <c>messages</c> into Titan's <c>"User: ...\nBot: ...\nUser: ...\nBot:"</c>
    /// transcript convention (the format AWS's own documentation illustrates), leaving a trailing
    /// <c>Bot:</c> prompt for the model to continue when the transcript ends on a user (or system-only)
    /// turn - which is the common case, a client sending the next turn of a conversation.
    /// </summary>
    private static string BuildInputText(JsonArray messages)
    {
        var builder = new StringBuilder();
        string? lastTurnRole = null;

        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message) continue;

            var text = PayloadTranslationHelpers.ExtractText(message["content"]) ?? string.Empty;

            switch (message["role"]?.GetValue<string>())
            {
                case "system":
                    if (text.Length > 0) builder.Append(text).Append('\n');

                    break;

                case "assistant":
                    builder.Append("Bot: ").Append(text).Append('\n');
                    lastTurnRole = "assistant";
                    break;

                case "tool":
                case "function":
                    // Titan has no native tool-result concept; fold it into the transcript as a labeled
                    // line rather than silently dropping the context, but this is a best-effort
                    // approximation, not a faithful translation - Titan cannot act on it either way.
                    if (text.Length > 0) builder.Append("Tool: ").Append(text).Append('\n');

                    break;

                case "user":
                default:
                    builder.Append("User: ").Append(text).Append('\n');
                    lastTurnRole = "user";
                    break;
            }
        }

        if (lastTurnRole != "assistant") builder.Append("Bot:");

        return builder.ToString();
    }

    /// <summary>Maps Titan's <c>completionReason</c> to OpenAI's <c>finish_reason</c>, per AWS's documented possible values.</summary>
    internal static string MapCompletionReason(string? reason)
    {
        return reason switch
        {
            null or "" => "stop",
            "FINISHED" => "stop",
            "STOP_CRITERIA_MET" => "stop",
            "LENGTH" => "length",
            "CONTENT_FILTERED" => "content_filter",
            "RAG_QUERY_WHEN_RAG_DISABLED" => "stop",
            _ => "stop"
        };
    }
}

/// <summary>
/// Translates each of Titan's already-decoded streaming chunks (
/// <c>
/// {index, inputTextTokenCount,
/// totalOutputTextTokenCount, outputText, completionReason}
/// </c>
/// , per AWS's documented shape) into an
/// OpenAI-shaped SSE chunk. <c>completionReason</c> is present only on the terminal chunk, per AWS's
/// docs - its presence is this translator's finish signal.
/// </summary>
internal sealed class TitanStreamChunkTranslator : IBedrockStreamChunkTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private readonly string _id = PayloadTranslationHelpers.GenerateCompletionId();
    private readonly string _modelIdentifier;
    private bool _roleSent;

    // Titan's streaming chunks carry no model identifier either (see TranslateResponse's remarks) - the
    // provider key is passed down from TitanPayloadTranslator.CreateBedrockStreamChunkTranslator so every
    // chunk reports the same stable value non-streaming responses do.
    /// <summary>
    /// Initializes a new instance, reporting <paramref name="modelIdentifier"/> as the <c>model</c> field on every
    /// emitted chunk.
    /// </summary>
    /// <param name="modelIdentifier">
    /// The stable model identifier to report (the provider key, since Titan's own chunks carry
    /// none).
    /// </param>
    public TitanStreamChunkTranslator(string modelIdentifier)
    {
        _modelIdentifier = modelIdentifier;
    }

    /// <summary>
    /// Translates one decoded Titan Bedrock streaming chunk into an OpenAI-shaped SSE
    /// <c>chat.completion.chunk</c> event, emitting the role delta once, the generated text delta, and
    /// (once <c>completionReason</c> appears) the mapped finish reason and token usage.
    /// </summary>
    public byte[] TranslateChunk(byte[] nativeChunkJson)
    {
        var chunk = JsonNode.Parse(nativeChunkJson) as JsonObject
                    ?? throw new JsonException("Titan stream chunk was not a JSON object.");

        var outputText = chunk["outputText"]?.GetValue<string>() ?? string.Empty;
        var completionReason = chunk["completionReason"]?.GetValue<string>();

        var delta = new JsonObject();
        if (!_roleSent)
        {
            delta["role"] = "assistant";
            _roleSent = true;
        }

        if (outputText.Length > 0) delta["content"] = outputText;

        var finishReason = string.IsNullOrEmpty(completionReason)
            ? null
            : TitanPayloadTranslator.MapCompletionReason(completionReason);

        var openAiChunk = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion.chunk",
            ["created"] = _created,
            ["model"] = _modelIdentifier,
            ["choices"] = new JsonArray
            {
                new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason }
            }
        };

        if (finishReason is not null)
        {
            var promptTokens = chunk["inputTextTokenCount"]?.GetValue<int>() ?? 0;
            var completionTokens = chunk["totalOutputTextTokenCount"]?.GetValue<int>() ?? 0;
            openAiChunk["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = promptTokens + completionTokens
            };
        }

        var json = JsonSerializer.Serialize(value: openAiChunk, options: SerializerOptions);
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    /// <summary>Emits the terminal OpenAI-style <c>data: [DONE]</c> SSE marker once the stream ends.</summary>
    public byte[] Flush()
    {
        return Encoding.UTF8.GetBytes("data: [DONE]\n\n");
    }
}