using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy.Translation;

namespace TotallyHot.ArcRouter.Proxy.Bedrock;

/// <summary>
/// Translates OpenAI-shaped chat-completion payloads to/from Anthropic Claude's shape as hosted on
/// Amazon Bedrock (the Bedrock slice of unified API translation -
/// <c>docs/router/unified-api-translation.md</c> §4.2). Bedrock's Claude <c>InvokeModel</c> body is
/// nearly identical to native Anthropic's Messages API (verified against AWS's own documentation) - the
/// model id moves from the body to the AWS SDK's <c>ModelId</c> parameter, and
/// <c>anthropic_version</c> becomes the fixed literal <c>bedrock-2023-05-31</c> instead of an HTTP
/// header - so this translator reuses <see cref="AnthropicPayloadTranslator"/>'s message/tool/response
/// translation logic directly (both are <c>internal</c> exactly to enable this reuse) rather than
/// reimplementing it.
///
/// <para>
/// Unlike <see cref="AnthropicPayloadTranslator"/>, this translator is <b>always</b> applied - there is
/// no Bedrock-native client sending Bedrock-shaped requests directly the way Claude Code sends
/// Anthropic-native ones, so there's nothing to detect or bypass (mirrors Gemini's always-translate
/// behavior, not native Anthropic's dual-mode <see cref="IPayloadTranslator.ShouldTranslate"/>).
/// </para>
/// </summary>
public sealed class AnthropicOnBedrockPayloadTranslator : IBedrockPayloadTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // Matches AnthropicPayloadTranslator's own floor - see its remarks for why a default is needed at
    // all (Bedrock's Claude body requires max_tokens exactly as native Anthropic's does).
    private const int DefaultMaxTokens = 4096;

    private const string BedrockAnthropicVersion = "bedrock-2023-05-31";

    /// <inheritdoc />
    public string Provider => "bedrock-anthropic";

    /// <inheritdoc />
    public bool ShouldTranslate(HttpRequest request) => true;

    /// <inheritdoc />
    public Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming) =>
        throw new NotSupportedException(
            "Bedrock providers are invoked via the AWS SDK (IAmazonBedrockRuntime), not a forwarded HTTP URL. " +
            "ProxyMiddleware forks to its Bedrock invocation path before this would ever be called for an IBedrockPayloadTranslator.");

    /// <inheritdoc />
    public byte[] TranslateRequest(byte[] openAiShapedBody)
    {
        var root = JsonNode.Parse(openAiShapedBody) as JsonObject
            ?? throw new JsonException("Anthropic-on-Bedrock request translation expected a JSON object body.");

        var bedrock = new JsonObject { ["anthropic_version"] = BedrockAnthropicVersion };

        var messages = root["messages"] as JsonArray ?? new JsonArray();
        var (system, translatedMessages) = AnthropicPayloadTranslator.TranslateMessages(messages);

        if (system is not null)
        {
            bedrock["system"] = system;
        }

        bedrock["messages"] = translatedMessages;

        if (AnthropicPayloadTranslator.TranslateTools(root["tools"] as JsonArray) is { } tools)
        {
            bedrock["tools"] = tools;
        }

        if (AnthropicPayloadTranslator.TranslateToolChoice(root["tool_choice"]) is { } toolChoice)
        {
            bedrock["tool_choice"] = toolChoice;
        }

        if (root["temperature"] is JsonNode temperature)
        {
            bedrock["temperature"] = temperature.DeepClone();
        }

        if (root["top_p"] is JsonNode topP)
        {
            bedrock["top_p"] = topP.DeepClone();
        }

        if (root["top_k"] is JsonNode topK)
        {
            bedrock["top_k"] = topK.DeepClone();
        }

        bedrock["max_tokens"] = (root["max_tokens"] ?? root["max_completion_tokens"]) is JsonNode maxTokens
            ? maxTokens.DeepClone()
            : DefaultMaxTokens;

        switch (root["stop"])
        {
            case JsonValue stopValue when stopValue.TryGetValue<string>(out var stopString):
                bedrock["stop_sequences"] = new JsonArray { stopString };
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
                    bedrock["stop_sequences"] = stopSequences;
                }

                break;
        }

        // No "model" (the SDK's ModelId parameter carries it) and no "stream" (the SDK method choice -
        // InvokeModel vs InvokeModelWithResponseStream - carries it, unlike native Anthropic's own body
        // flag) - the two respects in which this envelope genuinely differs from native Anthropic's.
        if (root["thinking"] is JsonObject thinkingParam)
        {
            bedrock["thinking"] = thinkingParam.DeepClone();
        }

        return JsonSerializer.SerializeToUtf8Bytes(bedrock, SerializerOptions);
    }

    /// <inheritdoc />
    public byte[] TranslateResponse(byte[] nativeShapedBody) =>
        // Bedrock's Claude InvokeModel response body is the same content[]/stop_reason/usage envelope as
        // native Anthropic's (verified against AWS's docs) - reused verbatim, not reimplemented.
        new AnthropicPayloadTranslator().TranslateResponse(nativeShapedBody);

    /// <inheritdoc />
    public IStreamTranslator CreateStreamTranslator() =>
        throw new NotSupportedException(
            "Bedrock streaming uses CreateBedrockStreamChunkTranslator (no SSE framing to buffer - the AWS SDK " +
            "already decodes each chunk), not IStreamTranslator.");

    /// <inheritdoc />
    public IBedrockStreamChunkTranslator CreateBedrockStreamChunkTranslator() => new AnthropicOnBedrockStreamChunkTranslator();
}

/// <summary>
/// Thin adapter reusing <see cref="AnthropicStreamTranslator"/>'s per-event-type handling for Bedrock's
/// already-decoded chunks: Bedrock's Claude streaming events carry the identical JSON shape (including
/// their own <c>"type"</c> field) as native Anthropic's SSE events, just without the SSE
/// <c>event:</c>/<c>data:</c> framing around them, since the AWS SDK has already stripped that framing.
/// </summary>
internal sealed class AnthropicOnBedrockStreamChunkTranslator : IBedrockStreamChunkTranslator
{
    private readonly AnthropicStreamTranslator _inner = new();

    /// <summary>
    /// Translates a single already-decoded Bedrock streaming chunk by delegating to the inner
    /// <see cref="AnthropicStreamTranslator"/>'s native-JSON event handling.
    /// </summary>
    public byte[] TranslateChunk(byte[] nativeChunkJson) => _inner.TranslateNativeJsonChunk(nativeChunkJson) ?? [];

    /// <summary>
    /// Flushes any buffered state in the inner stream translator once the Bedrock stream ends.
    /// </summary>
    public byte[] Flush() => _inner.Flush();
}

