using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Per-request, stateful translator for one Anthropic Messages API streaming response. Consumes
/// Anthropic's native SSE bytes (<c>event: ...</c> / <c>data: {...}</c> pairs) as they arrive and emits
/// OpenAI <c>chat.completion.chunk</c> SSE bytes for the client. Created per response by
/// <see cref="AnthropicPayloadTranslator.CreateStreamTranslator"/>; not thread-safe.
/// <para>
/// Error semantics mirror LiteLLM's Anthropic iterator (the parity reference, same rule Gemini's
/// translator follows): an <c>error</c> event throws <see cref="AnthropicStreamException"/> to
/// terminate the stream; a fragmented event that hasn't yet received its terminating blank line stays
/// buffered rather than being dropped or forwarded raw; and <c>message_stop</c> still emits a final
/// chunk so <c>finish_reason</c> is never lost.
/// </para>
/// <para>
/// Anthropic's event sequence is <c>message_start</c> → repeated
/// (<c>content_block_start</c> → <c>content_block_delta</c>* → <c>content_block_stop</c>) →
/// <c>message_delta</c> → <c>message_stop</c>, with occasional <c>ping</c> events interleaved (ignored).
/// A <c>thinking</c> content block's <c>thinking_delta</c>/<c>signature_delta</c> chunks are accumulated
/// here and emitted as one <c>thinking_blocks</c> delta on that block's <c>content_block_stop</c>, so the
/// client receives the full, signed block needed to round-trip it back on a later turn (see
/// <c>AnthropicPayloadTranslator</c>'s <c>reasoning_content</c>/<c>thinking_blocks</c> handling).
/// </para>
/// </summary>
public sealed class AnthropicStreamTranslator : IStreamTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] DoneLine = "data: [DONE]\n\n"u8.ToArray();

    // Per-content-block-index state, keyed by Anthropic's own content_block index (not necessarily
    // contiguous with the OpenAI tool_calls index, since text/thinking blocks share the same index space).
    private readonly Dictionary<int, string> _blockTypes = [];

    // Raw upstream bytes not yet forming a complete SSE event. Carriage returns are stripped on
    // ingestion so the event delimiter is always "\n\n".
    private readonly List<byte> _buffer = [];
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly Dictionary<int, StringBuilder> _thinkingSignature = [];
    private readonly Dictionary<int, StringBuilder> _thinkingText = [];
    private readonly Dictionary<int, int> _toolCallOpenAiIndex = [];
    private string? _finishReason;

    // Stable across the whole stream, seeded from message_start.
    private string _id = PayloadTranslationHelpers.GenerateCompletionId();
    private string _model = string.Empty;
    private int _nextToolCallIndex;

    // An error event terminates the stream, but only after the valid chunks that preceded it in the
    // same Push have been emitted (otherwise the client loses a good prefix). Held here and re-thrown on
    // the next Push/Flush, once the good bytes are already on the wire.
    private AnthropicStreamException? _pendingError;

    private bool _roleSent;
    private JsonObject? _usage;

    /// <inheritdoc/>
    public byte[] Push(ReadOnlySpan<byte> upstreamChunk)
    {
        if (_pendingError is not null) throw _pendingError;

        foreach (var b in upstreamChunk)
            if (b != (byte)'\r')
                _buffer.Add(b);

        using var output = new MemoryStream();

        int delimiter;
        while ((delimiter = IndexOfDoubleNewline()) >= 0)
        {
            var eventBytes = _buffer.GetRange(0, count: delimiter).ToArray();
            _buffer.RemoveRange(0, count: delimiter + 2);

            byte[]? translated;
            try
            {
                translated = TranslateEvent(eventBytes);
            }
            catch (AnthropicStreamException ex)
            {
                _pendingError = ex;
                break;
            }

            if (translated is not null) output.Write(buffer: translated, 0, count: translated.Length);
        }

        return output.ToArray();
    }

    /// <inheritdoc/>
    public byte[] Flush()
    {
        if (_pendingError is not null) throw _pendingError;

        using var output = new MemoryStream();

        if (_buffer.Count > 0)
        {
            var remaining = _buffer.ToArray();
            _buffer.Clear();
            if (TranslateEvent(remaining) is { } translated)
                output.Write(buffer: translated, 0, count: translated.Length);
        }

        output.Write(buffer: DoneLine, 0, count: DoneLine.Length);
        return output.ToArray();
    }

    /// <summary>
    /// Scans the buffered bytes for the first blank-line separator (<c>\n\n</c>) marking the end of one complete SSE
    /// event, returning -1 if no complete event is buffered yet.
    /// </summary>
    private int IndexOfDoubleNewline()
    {
        for (var i = 0; i + 1 < _buffer.Count; i++)
            if (_buffer[i] == (byte)'\n' && _buffer[i + 1] == (byte)'\n')
                return i;

        return -1;
    }

    /// <summary>
    /// Parses one complete SSE-framed event's bytes into its event type and JSON payload, then dispatches it by type;
    /// returns null when the event carries no data payload (e.g. a bare comment/keepalive).
    /// </summary>
    private byte[]? TranslateEvent(byte[] eventBytes)
    {
        var (eventType, payload) = ParseSseEvent(eventBytes);
        if (payload is null) return null;

        var evt = ParseEventJson(Encoding.UTF8.GetBytes(payload));
        return DispatchEvent(evt: evt, explicitEventType: eventType);
    }

    /// <summary>
    /// Translates one complete native Anthropic-shaped JSON chunk with no SSE <c>event:</c>/<c>data:</c>
    /// framing to parse - the shape Amazon Bedrock's <c>InvokeModelWithResponseStream</c> hands this
    /// codebase, one already-decoded chunk at a time, via <c>AnthropicOnBedrockStreamChunkTranslator</c>.
    /// Reuses the exact same per-event-type handling <see cref="Push"/>'s SSE-driven path uses, since
    /// Bedrock's Claude chunks carry the identical event JSON (including its own <c>"type"</c> field) as
    /// native Anthropic's streaming events - only the transport framing around them differs.
    /// </summary>
    internal byte[]? TranslateNativeJsonChunk(byte[] nativeChunkJson)
    {
        var evt = ParseEventJson(nativeChunkJson);
        return DispatchEvent(evt: evt, null);
    }

    /// <summary>
    /// Parses raw event JSON bytes into a <see cref="JsonObject"/>, wrapping any parse failure or non-object result
    /// in an <see cref="AnthropicStreamException"/>.
    /// </summary>
    private static JsonObject ParseEventJson(byte[] jsonBytes)
    {
        try
        {
            return JsonNode.Parse(jsonBytes) as JsonObject
                   ?? throw new AnthropicStreamException("Anthropic stream event was not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new AnthropicStreamException(message: $"Failed to parse Anthropic stream event: {ex.Message}",
                innerException: ex);
        }
    }

    /// <summary>
    /// Dispatches one parsed event JSON object by its type - <paramref name="explicitEventType"/> (from an SSE
    /// <c>event:</c> line) wins when present, else the JSON's own <c>"type"</c> field is used (always the case for Bedrock's
    /// framing-free chunks).
    /// </summary>
    private byte[]? DispatchEvent(JsonObject evt, string? explicitEventType)
    {
        var type = explicitEventType ?? evt["type"]?.GetValue<string>();

        return type switch
        {
            "ping" => null,
            "error" => throw BuildStreamError(evt),
            "message_start" => HandleMessageStart(evt),
            "content_block_start" => HandleContentBlockStart(evt),
            "content_block_delta" => HandleContentBlockDelta(evt),
            "content_block_stop" => HandleContentBlockStop(evt),
            "message_delta" => HandleMessageDelta(evt),
            "message_stop" => HandleMessageStop(),
            _ => null
        };
    }

    /// <summary>
    /// Builds an <see cref="AnthropicStreamException"/> from an Anthropic <c>error</c> stream event, combining its
    /// error type and message into the exception text.
    /// </summary>
    private static AnthropicStreamException BuildStreamError(JsonObject evt)
    {
        var error = evt["error"] as JsonObject;
        var errorType = error?["type"]?.GetValue<string>() ?? "unknown_error";
        var message = error?["message"]?.GetValue<string>() ?? "Unknown error";
        return new AnthropicStreamException($"{errorType} - {message}");
    }

    /// <summary>
    /// Handles a <c>message_start</c> event by capturing the message's id, model, and initial usage for reuse on
    /// later chunks, then emits the initial role delta.
    /// </summary>
    private byte[]? HandleMessageStart(JsonObject evt)
    {
        if (evt["message"] is not JsonObject message) return null;

        if (message["id"]?.GetValue<string>() is { Length: > 0 } id) _id = id;

        if (message["model"]?.GetValue<string>() is { Length: > 0 } model) _model = model;

        if (message["usage"] is JsonObject usage) _usage = usage.DeepClone().AsObject();

        var delta = new JsonObject { ["role"] = "assistant" };
        _roleSent = true;

        return EmitChunk(delta: delta, null);
    }

    /// <summary>
    /// Handles a <c>content_block_start</c> event: records the block's type by index, and for a <c>tool_use</c> block
    /// assigns it the next OpenAI tool-call index and emits an initial tool_calls delta with its id and name; for a
    /// thinking/redacted_thinking block, initializes accumulator buffers for its text and signature.
    /// </summary>
    private byte[]? HandleContentBlockStart(JsonObject evt)
    {
        var index = evt["index"]?.GetValue<int>() ?? 0;
        if (evt["content_block"] is not JsonObject block) return null;

        var type = block["type"]?.GetValue<string>() ?? string.Empty;
        _blockTypes[index] = type;

        if (type == "tool_use")
        {
            var toolCallIndex = _nextToolCallIndex++;
            _toolCallOpenAiIndex[index] = toolCallIndex;

            var id = block["id"]?.GetValue<string>() is { Length: > 0 } blockId ? blockId : $"toolu_{Guid.NewGuid():N}";
            var name = block["name"]?.GetValue<string>() ?? string.Empty;

            var toolCallDelta = new JsonObject
            {
                ["index"] = toolCallIndex,
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = string.Empty }
            };

            return EmitChunk(delta: new JsonObject { ["tool_calls"] = new JsonArray { toolCallDelta } }, null);
        }

        if (type is "thinking" or "redacted_thinking")
        {
            _thinkingText[index] = new StringBuilder();
            _thinkingSignature[index] = new StringBuilder();
        }

        return null;
    }

    /// <summary>
    /// Handles a <c>content_block_delta</c> event by dispatching on the delta's own type: text_delta emits a content
    /// chunk, input_json_delta appends to the matching tool call's arguments, thinking_delta accumulates and emits reasoning
    /// text, and signature_delta accumulates the thinking block's signature without emitting a chunk.
    /// </summary>
    private byte[]? HandleContentBlockDelta(JsonObject evt)
    {
        var index = evt["index"]?.GetValue<int>() ?? 0;
        if (evt["delta"] is not JsonObject delta) return null;

        switch (delta["type"]?.GetValue<string>())
        {
            case "text_delta":
                if (delta["text"]?.GetValue<string>() is { } text)
                    return EmitChunk(delta: new JsonObject { ["content"] = text }, null);

                return null;

            case "input_json_delta":
                if (_toolCallOpenAiIndex.TryGetValue(key: index, value: out var toolCallIndex) &&
                    delta["partial_json"]?.GetValue<string>() is { } partialJson)
                {
                    var toolCallDelta = new JsonObject
                    {
                        ["index"] = toolCallIndex,
                        ["function"] = new JsonObject { ["arguments"] = partialJson }
                    };

                    return EmitChunk(delta: new JsonObject { ["tool_calls"] = new JsonArray { toolCallDelta } }, null);
                }

                return null;

            case "thinking_delta":
                if (delta["thinking"]?.GetValue<string>() is { } thinking)
                {
                    if (_thinkingText.TryGetValue(key: index, value: out var textBuilder)) textBuilder.Append(thinking);

                    return EmitChunk(delta: new JsonObject { ["reasoning_content"] = thinking }, null);
                }

                return null;

            case "signature_delta":
                if (delta["signature"]?.GetValue<string>() is { } signature &&
                    _thinkingSignature.TryGetValue(key: index, value: out var signatureBuilder))
                    signatureBuilder.Append(signature);

                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Handles a <c>content_block_stop</c> event for a completed thinking/redacted_thinking block by assembling its
    /// accumulated text and signature into a thinking_blocks delta; a no-op for any other block type.
    /// </summary>
    private byte[]? HandleContentBlockStop(JsonObject evt)
    {
        var index = evt["index"]?.GetValue<int>() ?? 0;
        if (!_blockTypes.TryGetValue(key: index, value: out var type) ||
            type is not ("thinking" or "redacted_thinking")) return null;

        if (!_thinkingText.TryGetValue(key: index, value: out var textBuilder)) return null;

        var thinkingBlock = new JsonObject { ["type"] = type, ["thinking"] = textBuilder.ToString() };
        if (_thinkingSignature.TryGetValue(key: index, value: out var signatureBuilder) && signatureBuilder.Length > 0)
            thinkingBlock["signature"] = signatureBuilder.ToString();

        return EmitChunk(delta: new JsonObject { ["thinking_blocks"] = new JsonArray { thinkingBlock } }, null);
    }

    /// <summary>
    /// Handles a <c>message_delta</c> event by recording the mapped finish reason and merging any updated usage
    /// totals into state; carries no client-visible delta of its own, so it always returns null.
    /// </summary>
    private byte[]? HandleMessageDelta(JsonObject evt)
    {
        if (evt["delta"] is JsonObject delta && delta["stop_reason"]?.GetValue<string>() is { } stopReason)
            _finishReason = AnthropicPayloadTranslator.MapStopReason(stopReason: stopReason,
                hasToolCalls: _toolCallOpenAiIndex.Count > 0);

        if (evt["usage"] is JsonObject usage)
        {
            _usage ??= [];
            foreach (var (key, value) in usage) _usage[key] = value?.DeepClone();
        }

        // No client-visible delta here - message_delta only carries metadata (stop reason, running
        // usage), applied to state and surfaced on the terminal message_stop chunk instead.
        return null;
    }

    /// <summary>
    /// Handles the terminal <c>message_stop</c> event by emitting the final chunk carrying the accumulated finish
    /// reason and usage, sending the role delta first if it was never sent.
    /// </summary>
    private byte[]? HandleMessageStop()
    {
        var delta = new JsonObject();
        if (!_roleSent)
        {
            delta["role"] = "assistant";
            _roleSent = true;
        }

        return EmitChunk(delta: delta, finishReason: _finishReason ?? "stop", true);
    }

    /// <summary>
    /// Wraps a delta object into an OpenAI-shaped <c>chat.completion.chunk</c> SSE event, using the stream's captured
    /// id/model/created values and optionally attaching the accumulated usage.
    /// </summary>
    private byte[] EmitChunk(JsonObject delta, string? finishReason, bool includeUsage = false)
    {
        var choice = new JsonObject
        {
            ["index"] = 0,
            ["delta"] = delta,
            ["finish_reason"] = finishReason
        };

        var chunk = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion.chunk",
            ["created"] = _created,
            ["model"] = _model,
            ["choices"] = new JsonArray { choice }
        };

        if (includeUsage && _usage is not null)
        {
            var inputTokens = _usage["input_tokens"]?.GetValue<int>() ?? 0;
            var outputTokens = _usage["output_tokens"]?.GetValue<int>() ?? 0;
            var cacheCreationTokens = _usage["cache_creation_input_tokens"]?.GetValue<int>();
            var cacheReadTokens = _usage["cache_read_input_tokens"]?.GetValue<int>();

            chunk["usage"] = AnthropicPayloadTranslator.BuildEnrichedUsage(inputTokens: inputTokens,
                outputTokens: outputTokens, cacheCreationTokens: cacheCreationTokens, cacheReadTokens: cacheReadTokens);
        }

        var json = JsonSerializer.Serialize(value: chunk, options: SerializerOptions);
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    /// <summary>
    /// Pulls the <c>event:</c> type and concatenated <c>data:</c> payload out of one SSE event block. Returns a null
    /// Data when the event has no data line.
    /// </summary>
    private static (string? EventType, string? Data) ParseSseEvent(byte[] eventBytes)
    {
        var text = Encoding.UTF8.GetString(eventBytes);
        string? eventType = null;
        StringBuilder? data = null;

        foreach (var line in text.Split('\n'))
            if (line.StartsWith(value: "event:", comparisonType: StringComparison.Ordinal))
            {
                eventType = line.Length > 6 && line[6] == ' ' ? line[7..] : line[6..];
            }
            else if (line.StartsWith(value: "data:", comparisonType: StringComparison.Ordinal))
            {
                // Per the SSE spec, multiple data: lines within one event are joined with "\n" (not
                // concatenated directly) - required for a multi-line or pretty-printed JSON payload to
                // parse correctly. No separator before the first line, none trailing after the last.
                if (data is null)
                    data = new StringBuilder();
                else
                    data.Append('\n');

                var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                data.Append(value);
            }

        return (eventType, data?.ToString());
    }
}

/// <summary>
/// Thrown when an Anthropic streaming response carries an <c>error</c> event or an uninterpretable
/// (complete-but-invalid) event, terminating the translated stream. Mirrors
/// <see cref="GeminiStreamException"/>'s role for the Gemini translator.
/// </summary>
public sealed class AnthropicStreamException : Exception
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public AnthropicStreamException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with the given message and inner exception.</summary>
    public AnthropicStreamException(string message, Exception innerException) : base(message: message,
        innerException: innerException)
    {
    }
}