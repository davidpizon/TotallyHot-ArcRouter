using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy.Translation;
using Microsoft.AspNetCore.Http;

namespace TotallyHot.ArcRouter.Proxy.Bedrock;

/// <summary>
/// Translates OpenAI-shaped chat-completion payloads to/from Meta Llama's shape on Bedrock (the
/// Bedrock slice of unified API translation -
/// <c>docs/router/unified-api-translation.md</c> §4.2). Llama's <c>InvokeModel</c> API takes a single
/// <c>prompt</c> string, not a structured message array - so OpenAI's <c>messages</c> are folded into
/// Meta's own documented Llama 3 chat template (<c>&lt;|begin_of_text|&gt;</c> /
/// <c>&lt;|start_header_id|&gt;{role}&lt;|end_header_id|&gt;</c> / <c>&lt;|eot_id|&gt;</c>), verified
/// against AWS's and Meta's published prompt-format documentation, not guessed.
///
/// <para>
/// <b>Deliberately out of scope</b> (a genuine capability absence in Llama's native Bedrock invoke API,
/// not a translation gap): function/tool calling, extended thinking, and <c>stop</c> sequences (Llama's
/// documented Bedrock request parameters have no stop-sequence field). A prior <c>role: "tool"</c>
/// result is folded into the prompt as a labeled <c>user</c>-role turn ("Tool result: ...") rather than
/// silently dropped, since Llama's template has no native tool-result role either.
/// </para>
/// </summary>
public sealed class LlamaPayloadTranslator : IBedrockPayloadTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // Mirrors the other Bedrock translators' documented-default-over-unbounded-generation approach.
    private const int DefaultMaxGenLen = 512;

    /// <inheritdoc />
    public string Provider => "bedrock-llama";

    /// <inheritdoc />
    public bool ShouldTranslate(HttpRequest request) => true;

    /// <inheritdoc />
    public Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming) =>
        throw new NotSupportedException(
            "Bedrock providers are invoked via the AWS SDK (IAmazonBedrockRuntime), not a forwarded HTTP URL.");

    /// <inheritdoc />
    public byte[] TranslateRequest(byte[] openAiShapedBody)
    {
        var root = JsonNode.Parse(openAiShapedBody) as JsonObject
            ?? throw new JsonException("Llama request translation expected a JSON object body.");

        var messages = root["messages"] as JsonArray ?? new JsonArray();

        var llama = new JsonObject { ["prompt"] = BuildPrompt(messages) };

        if (root["temperature"] is JsonNode temperature)
        {
            llama["temperature"] = temperature.DeepClone();
        }

        if (root["top_p"] is JsonNode topP)
        {
            llama["top_p"] = topP.DeepClone();
        }

        llama["max_gen_len"] = (root["max_tokens"] ?? root["max_completion_tokens"]) is JsonNode maxTokens
            ? maxTokens.DeepClone()
            : DefaultMaxGenLen;

        return JsonSerializer.SerializeToUtf8Bytes(llama, SerializerOptions);
    }

    /// <summary>
    /// Builds Meta's documented Llama 3 chat-template prompt string from OpenAI <c>messages</c>, ending
    /// with a trailing <c>&lt;|start_header_id|&gt;assistant&lt;|end_header_id|&gt;</c> for the model to
    /// continue - unless the transcript already ends on an assistant turn (an unusual prefill case).
    /// </summary>
    private static string BuildPrompt(JsonArray messages)
    {
        var builder = new StringBuilder("<|begin_of_text|>");
        string? lastTemplateRole = null;

        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message)
            {
                continue;
            }

            var text = PayloadTranslationHelpers.ExtractText(message["content"]) ?? string.Empty;
            string templateRole;

            switch (message["role"]?.GetValue<string>())
            {
                case "system":
                    templateRole = "system";
                    break;
                case "assistant":
                    templateRole = "assistant";
                    break;
                case "tool":
                case "function":
                    // Llama's template has no native tool-result role; fold it into a labeled user turn
                    // rather than silently dropping the context - best-effort, not faithful, since Llama
                    // cannot act on it either way (no tools were ever declared to it).
                    templateRole = "user";
                    text = $"Tool result: {text}";
                    break;
                case "user":
                default:
                    templateRole = "user";
                    break;
            }

            builder.Append("<|start_header_id|>").Append(templateRole).Append("<|end_header_id|>\n\n")
                .Append(text).Append("<|eot_id|>");
            lastTemplateRole = templateRole;
        }

        if (lastTemplateRole != "assistant")
        {
            builder.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public byte[] TranslateResponse(byte[] nativeShapedBody)
    {
        var root = JsonNode.Parse(nativeShapedBody) as JsonObject
            ?? throw new JsonException("Llama response translation expected a JSON object body.");

        var generation = root["generation"]?.GetValue<string>() ?? string.Empty;
        var promptTokens = root["prompt_token_count"]?.GetValue<int>() ?? 0;
        var completionTokens = root["generation_token_count"]?.GetValue<int>() ?? 0;

        var openAi = new JsonObject
        {
            ["id"] = PayloadTranslationHelpers.GenerateCompletionId(),
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            // Llama's own response body carries no model identifier to echo (unlike Claude/Gemini,
            // which return one) - the provider key is the most stable identifier available here.
            ["model"] = Provider,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = generation },
                    ["finish_reason"] = MapStopReason(root["stop_reason"]?.GetValue<string>()),
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = promptTokens + completionTokens,
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(openAi, SerializerOptions);
    }

    /// <summary>Maps Llama's <c>stop_reason</c> ("stop"/"length", per AWS's docs) to OpenAI's <c>finish_reason</c>.</summary>
    internal static string MapStopReason(string? reason) => reason switch
    {
        "length" => "length",
        _ => "stop",
    };

    /// <inheritdoc />
    public IStreamTranslator CreateStreamTranslator() =>
        throw new NotSupportedException("Bedrock streaming uses CreateBedrockStreamChunkTranslator, not IStreamTranslator.");

    /// <inheritdoc />
    public IBedrockStreamChunkTranslator CreateBedrockStreamChunkTranslator() => new LlamaStreamChunkTranslator(Provider);
}

/// <summary>
/// Translates each of Llama's already-decoded streaming chunks (<c>{generation, prompt_token_count?,
/// generation_token_count?, stop_reason?}</c>) into an OpenAI-shaped SSE chunk. <c>stop_reason</c> is
/// present only on the terminal chunk - its presence is this translator's finish signal, mirroring
/// Titan's <c>completionReason</c> convention.
/// </summary>
internal sealed class LlamaStreamChunkTranslator : IBedrockStreamChunkTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _id = PayloadTranslationHelpers.GenerateCompletionId();
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly string _modelIdentifier;
    private bool _roleSent;

    // Llama's streaming chunks carry no model identifier either (see TranslateResponse's remarks) - the
    // provider key is passed down from LlamaPayloadTranslator.CreateBedrockStreamChunkTranslator so every
    // chunk reports the same stable value non-streaming responses do.
    /// <summary>Initializes a new instance, reporting <paramref name="modelIdentifier"/> as the <c>model</c> field on every emitted chunk.</summary>
    /// <param name="modelIdentifier">The stable model identifier to report (the provider key, since Llama's own chunks carry none).</param>
    public LlamaStreamChunkTranslator(string modelIdentifier) => _modelIdentifier = modelIdentifier;

    /// <summary>
    /// Translates one decoded Llama Bedrock streaming chunk into an OpenAI-shaped SSE
    /// <c>chat.completion.chunk</c> event, emitting the role delta once, the generated text delta, and
    /// (on the final chunk) the mapped finish reason and token usage.
    /// </summary>
    public byte[] TranslateChunk(byte[] nativeChunkJson)
    {
        var chunk = JsonNode.Parse(nativeChunkJson) as JsonObject
            ?? throw new JsonException("Llama stream chunk was not a JSON object.");

        var generation = chunk["generation"]?.GetValue<string>() ?? string.Empty;
        var stopReason = chunk["stop_reason"]?.GetValue<string>();

        var delta = new JsonObject();
        if (!_roleSent)
        {
            delta["role"] = "assistant";
            _roleSent = true;
        }

        if (generation.Length > 0)
        {
            delta["content"] = generation;
        }

        var finishReason = string.IsNullOrEmpty(stopReason) ? null : LlamaPayloadTranslator.MapStopReason(stopReason);

        var openAiChunk = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion.chunk",
            ["created"] = _created,
            ["model"] = _modelIdentifier,
            ["choices"] = new JsonArray
            {
                new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason },
            },
        };

        if (finishReason is not null)
        {
            var promptTokens = chunk["prompt_token_count"]?.GetValue<int>() ?? 0;
            var completionTokens = chunk["generation_token_count"]?.GetValue<int>() ?? 0;
            openAiChunk["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = promptTokens + completionTokens,
            };
        }

        var json = JsonSerializer.Serialize(openAiChunk, SerializerOptions);
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    /// <summary>Emits the terminal OpenAI-style <c>data: [DONE]</c> SSE marker once the stream ends.</summary>
    public byte[] Flush() => Encoding.UTF8.GetBytes("data: [DONE]\n\n");
}

