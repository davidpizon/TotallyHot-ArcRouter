using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Per-request, stateful translator for one Gemini <c>streamGenerateContent?alt=sse</c> response.
/// Consumes Gemini's native SSE bytes as they arrive and emits OpenAI <c>chat.completion.chunk</c> SSE
/// bytes for the client. Created per response by
/// <see cref="GeminiPayloadTranslator.CreateStreamTranslator"/>; not thread-safe.
///
/// <para>
/// Error semantics mirror LiteLLM's <c>ModelResponseIterator</c> (the parity reference): a chunk with
/// an embedded <c>error</c> object (e.g. a 429 <c>RESOURCE_EXHAUSTED</c> delivered as an HTTP 200 SSE
/// body) throws <see cref="GeminiStreamException"/> to terminate the stream; a fragmented event that
/// hasn't yet been fully received stays buffered until its terminating blank line arrives, rather than
/// being dropped or forwarded raw (the byte buffer below <em>is</em> that accumulation); and a
/// finish-only or metadata-only chunk still emits a chunk so <c>finish_reason</c> is never lost.
/// </para>
/// </summary>
public sealed class GeminiStreamTranslator : IStreamTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] DoneLine = Encoding.UTF8.GetBytes("data: [DONE]\n\n");

    // Raw upstream bytes not yet forming a complete SSE event. Carriage returns are stripped on
    // ingestion so the event delimiter is always "\n\n" (Gemini's compact JSON never contains a raw
    // 0x0D byte - a CR inside a JSON string is the two-char escape \r, not a literal 0x0D).
    private readonly List<byte> _buffer = new();

    // Stable across the whole stream, seeded from the first chunk that carries them.
    private string _id = PayloadTranslationHelpers.GenerateCompletionId();
    private string _model = string.Empty;
    private long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private bool _roleSent;
    private int _toolCallIndex;
    private bool _hasSeenToolCalls;

    // An embedded-error event terminates the stream, but only after the valid chunks that preceded it
    // in the same Push have been emitted (otherwise the client loses a good prefix). So the error is
    // held here and re-thrown on the next Push/Flush, once the good bytes are already on the wire.
    private GeminiStreamException? _pendingError;

    /// <inheritdoc />
    public byte[] Push(ReadOnlySpan<byte> upstreamChunk)
    {
        if (_pendingError is not null)
        {
            throw _pendingError;
        }

        foreach (var b in upstreamChunk)
        {
            if (b != (byte)'\r')
            {
                _buffer.Add(b);
            }
        }

        using var output = new MemoryStream();

        // Emit every complete event ("...\n\n") currently in the buffer, leaving any trailing partial
        // event buffered for the next Push (or Flush). On an embedded error, stop translating, keep the
        // exception pending, and return the good bytes emitted so far - the error surfaces next call.
        int delimiter;
        while ((delimiter = IndexOfDoubleNewline()) >= 0)
        {
            var eventBytes = _buffer.GetRange(0, delimiter).ToArray();
            _buffer.RemoveRange(0, delimiter + 2);

            byte[]? translated;
            try
            {
                translated = TranslateEvent(eventBytes);
            }
            catch (GeminiStreamException ex)
            {
                _pendingError = ex;
                break;
            }

            if (translated is not null)
            {
                output.Write(translated, 0, translated.Length);
            }
        }

        return output.ToArray();
    }

    /// <inheritdoc />
    public byte[] Flush()
    {
        // A held embedded error terminates the stream: no trailing content, no [DONE].
        if (_pendingError is not null)
        {
            throw _pendingError;
        }

        using var output = new MemoryStream();

        // A final event without its trailing blank line (some servers omit it on the last event).
        if (_buffer.Count > 0)
        {
            var remaining = _buffer.ToArray();
            _buffer.Clear();
            if (TranslateEvent(remaining) is { } translated)
            {
                output.Write(translated, 0, translated.Length);
            }
        }

        output.Write(DoneLine, 0, DoneLine.Length);
        return output.ToArray();
    }

    /// <summary>Scans the buffered bytes for the first blank-line separator (<c>\n\n</c>) marking the end of one complete SSE event, returning -1 if no complete event is buffered yet.</summary>
    private int IndexOfDoubleNewline()
    {
        for (var i = 0; i + 1 < _buffer.Count; i++)
        {
            if (_buffer[i] == (byte)'\n' && _buffer[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Translates one raw SSE event (its <c>data:</c> payload) into an OpenAI SSE chunk line, or null when the event carries nothing client-visible.</summary>
    private byte[]? TranslateEvent(byte[] eventBytes)
    {
        var payload = ExtractDataPayload(eventBytes);
        if (payload is null || payload == "[DONE]")
        {
            return null;
        }

        JsonObject chunk;
        try
        {
            chunk = JsonNode.Parse(payload) as JsonObject
                ?? throw new GeminiStreamException("Gemini stream chunk was not a JSON object.");
        }
        catch (JsonException ex)
        {
            // A complete event (it had its terminating blank line) that still isn't valid JSON is a
            // genuine protocol error, not a fragment to accumulate - terminate rather than forward raw.
            throw new GeminiStreamException($"Failed to parse Gemini stream chunk: {ex.Message}", ex);
        }

        // Embedded provider error (e.g. 429 RESOURCE_EXHAUSTED as an HTTP 200 SSE body) - terminate.
        if (chunk["error"] is JsonObject error)
        {
            var status = error["status"]?.GetValue<string>() ?? "UNKNOWN";
            var message = error["message"]?.GetValue<string>() ?? "Unknown error";
            throw new GeminiStreamException($"{status} - {message}");
        }

        SeedStreamIdentity(chunk);

        var delta = new JsonObject();
        if (!_roleSent)
        {
            delta["role"] = "assistant";
            _roleSent = true;
        }

        string? finishReason = null;
        var contentText = new StringBuilder();
        JsonArray? toolCalls = null;

        if (chunk["candidates"] is JsonArray candidates && candidates.Count > 0 && candidates[0] is JsonObject candidate)
        {
            if (candidate["content"] is JsonObject content && content["parts"] is JsonArray parts)
            {
                foreach (var partNode in parts)
                {
                    if (partNode is not JsonObject part)
                    {
                        continue;
                    }

                    if (part["text"]?.GetValue<string>() is { } text)
                    {
                        contentText.Append(text);
                    }
                    else if (part["functionCall"] is JsonObject functionCall)
                    {
                        toolCalls ??= new JsonArray();
                        toolCalls.Add(BuildToolCallDelta(functionCall));
                        _hasSeenToolCalls = true;
                    }
                }
            }

            if (candidate["finishReason"]?.GetValue<string>() is { } rawFinish)
            {
                finishReason = _hasSeenToolCalls ? "tool_calls" : GeminiPayloadTranslator.MapFinishReason(rawFinish);
            }
        }

        if (contentText.Length > 0)
        {
            delta["content"] = contentText.ToString();
        }

        if (toolCalls is not null)
        {
            delta["tool_calls"] = toolCalls;
        }

        var choice = new JsonObject
        {
            ["index"] = 0,
            ["delta"] = delta,
            ["finish_reason"] = finishReason,
        };

        var openAiChunk = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion.chunk",
            ["created"] = _created,
            ["model"] = _model,
            ["choices"] = new JsonArray { choice },
        };

        if (chunk["usageMetadata"] is JsonObject usageMetadata)
        {
            openAiChunk["usage"] = new JsonObject
            {
                ["prompt_tokens"] = usageMetadata["promptTokenCount"]?.GetValue<int>() ?? 0,
                ["completion_tokens"] = usageMetadata["candidatesTokenCount"]?.GetValue<int>() ?? 0,
                ["total_tokens"] = usageMetadata["totalTokenCount"]?.GetValue<int>() ?? 0,
            };
        }

        var json = JsonSerializer.Serialize(openAiChunk, SerializerOptions);
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    /// <summary>Converts a Gemini streaming functionCall part into an OpenAI-shaped tool_calls delta entry, assigning it the next tool-call index and serializing its args to a JSON string.</summary>
    private JsonObject BuildToolCallDelta(JsonObject functionCall)
    {
        var name = functionCall["name"]?.GetValue<string>() ?? string.Empty;
        var args = functionCall["args"] is JsonNode argsNode ? argsNode.ToJsonString(SerializerOptions) : "{}";
        var index = _toolCallIndex++;

        return new JsonObject
        {
            ["index"] = index,
            ["id"] = $"call_{index}_{Guid.NewGuid():N}",
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = args,
            },
        };
    }

    /// <summary>Captures the stream's id and model from the first chunk that carries them, so subsequent chunks (which may omit these fields) reuse the same stable values.</summary>
    private void SeedStreamIdentity(JsonObject chunk)
    {
        if (chunk["responseId"]?.GetValue<string>() is { Length: > 0 } responseId)
        {
            _id = responseId;
        }

        if (chunk["modelVersion"]?.GetValue<string>() is { Length: > 0 } modelVersion)
        {
            _model = modelVersion;
        }
    }

    /// <summary>Pulls the <c>data:</c> field value out of one SSE event, ignoring comment/other lines. Multiple <c>data:</c> lines are joined with <c>"\n"</c> per the SSE spec, not concatenated directly. Returns null when the event has no data line.</summary>
    private static string? ExtractDataPayload(byte[] eventBytes)
    {
        var text = Encoding.UTF8.GetString(eventBytes);
        StringBuilder? data = null;

        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            // Per the SSE spec, multiple data: lines within one event are joined with "\n" (not
            // concatenated directly) - required for a multi-line or pretty-printed JSON payload to
            // parse correctly. No separator before the first line, none trailing after the last.
            if (data is null)
            {
                data = new StringBuilder();
            }
            else
            {
                data.Append('\n');
            }

            // SSE allows an optional single space after the colon.
            var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
            data.Append(value);
        }

        return data?.ToString();
    }
}

/// <summary>
/// Thrown when a Gemini streaming response carries an embedded provider error or an uninterpretable
/// (complete-but-invalid) chunk, terminating the translated stream. Mirrors LiteLLM raising
/// <c>VertexAIError</c> mid-stream rather than swallowing the error.
/// </summary>
public sealed class GeminiStreamException : Exception
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public GeminiStreamException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with the given message and inner exception.</summary>
    public GeminiStreamException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

