using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Per-request, stateful translator for one streaming constrained-decoding response: it streams the
/// envelope's <c>content</c> through as ordinary prose deltas while accumulating the whole envelope, then
/// emits the <c>tool_calls</c> delta once the reply is complete. Created per response by
/// <see cref="ConstrainedToolCallTranslator.CreateStreamTranslator"/>; not thread-safe.
/// <para>
/// The split is forced by the shape of the data rather than chosen. Prose can be forwarded the moment it is
/// decoded, so it is - see <see cref="EnvelopeContentScanner"/>. Tool calls cannot: a call is only a call
/// once its arguments object is closed, and emitting a half-parsed one would hand the client a malformed
/// invocation. So calls wait for the end of the envelope, which is also when a client expects them.
/// </para>
/// <para>
/// Mirrors <see cref="ToolCallNormalizingStreamTranslator"/>'s SSE framing exactly - the same
/// <c>"\n\n"</c>-delimited event buffering, the same CR stripping, the same
/// <see cref="PassthroughRawEvent"/> fail-open primitive, and the same single-<c>choices[0]</c> scope - so
/// the two streaming paths cannot drift on protocol handling.
/// </para>
/// </summary>
internal sealed class ConstrainedToolCallStreamTranslator : IStreamTranslator
{
    /// <summary>
    /// The cap on accumulated envelope text before this translator gives up and forwards what it has as
    /// ordinary content. The same rule-4 concern as the delimiter scanner's region cap: a server that
    /// ignored the schema and streamed an ordinary long reply must not be buffered whole. 256 KiB is far
    /// above any real envelope - the prose inside it has already been streamed out, so what accumulates here
    /// is mostly tool arguments - and far below a response worth holding in memory.
    /// </summary>
    private const int MaxEnvelopeChars = 256 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] DoneLine = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
    private readonly StringBuilder _envelope = new();
    private readonly ILogger _logger;

    private readonly ToolCallNormalizationPlan _plan;
    private readonly ToolCallObservationRecorder _recorder;
    private readonly EnvelopeContentScanner _scanner = new();

    private readonly List<byte> _sseBuffer = new();
    private bool _abandoned;

    private bool _disarmed;
    private JsonNode? _lastChunkCreated;

    private JsonNode? _lastChunkId;
    private JsonNode? _lastChunkModel;
    private bool _loggedMultiChoiceWarning;
    private bool _resolved;

    /// <summary>Initializes a new instance of the <see cref="ConstrainedToolCallStreamTranslator"/> class.</summary>
    /// <param name="plan">The plan this response is read under; supplies the store key and observation policy.</param>
    /// <param name="capabilityStore">The store observations are written back to; <see langword="null"/> disables write-back.</param>
    /// <param name="logger">Logger for the fail-open warnings.</param>
    public ConstrainedToolCallStreamTranslator(
        ToolCallNormalizationPlan plan,
        IToolCallCapabilityStore? capabilityStore,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(logger);

        _plan = plan;
        _logger = logger;
        _recorder = new ToolCallObservationRecorder(plan: plan, store: capabilityStore);
    }

    /// <inheritdoc/>
    public byte[] Push(ReadOnlySpan<byte> upstreamChunk)
    {
        foreach (var b in upstreamChunk)
            if (b != (byte)'\r')
                _sseBuffer.Add(b);

        using var output = new MemoryStream();

        int delimiter;
        while ((delimiter = IndexOfDoubleNewline()) >= 0)
        {
            var eventBytes = _sseBuffer.GetRange(0, count: delimiter).ToArray();
            _sseBuffer.RemoveRange(0, count: delimiter + 2);

            if (ProcessEvent(eventBytes) is { } translated)
                output.Write(buffer: translated, 0, count: translated.Length);
        }

        return output.ToArray();
    }

    /// <inheritdoc/>
    public byte[] Flush()
    {
        using var output = new MemoryStream();

        // A final event without its trailing blank line (some servers omit it on the last event).
        if (_sseBuffer.Count > 0)
        {
            var remaining = _sseBuffer.ToArray();
            _sseBuffer.Clear();
            if (ProcessEvent(remaining) is { } translated)
                output.Write(buffer: translated, 0, count: translated.Length);
        }

        // Backstop for a stream that ended without ever sending a finish_reason. When one arrives the
        // envelope is resolved there instead, so the client sees finish_reason "tool_calls" on the finish
        // chunk rather than "stop" followed by an orphaned call.
        if (ResolveTrailingEnvelope() is { } trailing) output.Write(buffer: trailing, 0, count: trailing.Length);

        output.Write(buffer: DoneLine, 0, count: DoneLine.Length);
        return output.ToArray();
    }

    /// <summary>
    /// Finds the first blank-line separator (<c>\n\n</c>) marking a complete SSE event, or -1 when none is buffered
    /// yet.
    /// </summary>
    private int IndexOfDoubleNewline()
    {
        for (var i = 0; i + 1 < _sseBuffer.Count; i++)
            if (_sseBuffer[i] == (byte)'\n' && _sseBuffer[i + 1] == (byte)'\n')
                return i;

        return -1;
    }

    /// <summary>
    /// Forwards one raw SSE event exactly as buffered, reattaching the <c>"\n\n"</c> delimiter Push/Flush
    /// stripped off. Re-serializing the parsed JSON instead would corrupt an event with multiple
    /// <c>data:</c> lines or silently drop any non-<c>data:</c> line.
    /// </summary>
    private static byte[] PassthroughRawEvent(byte[] eventBytes)
    {
        var passthrough = new byte[eventBytes.Length + 2];
        eventBytes.CopyTo(array: passthrough, 0);
        passthrough[^2] = (byte)'\n';
        passthrough[^1] = (byte)'\n';
        return passthrough;
    }

    /// <summary>Translates one raw SSE event, or returns null when nothing is client-visible yet.</summary>
    private byte[]? ProcessEvent(byte[] eventBytes)
    {
        var payload = ExtractDataPayload(eventBytes);
        if (payload is null || payload == "[DONE]") return null;

        JsonObject chunk;
        try
        {
            chunk = JsonNode.Parse(payload) as JsonObject ?? throw new JsonException("Chunk was not a JSON object.");
        }
        catch (JsonException)
        {
            return PassthroughRawEvent(eventBytes);
        }

        _lastChunkId = chunk["id"]?.DeepClone();
        _lastChunkCreated = chunk["created"]?.DeepClone();
        _lastChunkModel = chunk["model"]?.DeepClone();

        if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
            return PassthroughRawEvent(eventBytes);

        if (choices.Count > 1)
        {
            if (!_loggedMultiChoiceWarning)
            {
                _loggedMultiChoiceWarning = true;
                _logger.LogWarning(
                    message:
                    "Constrained tool calling: chunk had {ChoiceCount} choices; only a single choice per chunk is supported, so it is forwarded unrewritten to avoid dropping data. Logged once per stream.",
                    choices.Count);
            }

            return PassthroughRawEvent(eventBytes);
        }

        if (_disarmed || _abandoned) return PassthroughRawEvent(eventBytes);

        var originalDelta = choice["delta"] as JsonObject;

        if (originalDelta?["tool_calls"] is JsonArray { Count: > 0 })
        {
            // The model emitted a real tool_calls delta despite the schema. Record it - this model never
            // needed constraining - and forward the rest of the stream untouched.
            //
            // Count-checked for the same reason the buffered path checks it: an empty tool_calls array is a
            // server emitting the field unconditionally, not a model calling a tool.
            _recorder.RecordNative();
            _disarmed = true;
            return PassthroughRawEvent(eventBytes);
        }

        var contentText = originalDelta?["content"] is JsonValue contentValue &&
                          contentValue.TryGetValue<string>(out var text)
            ? text
            : null;

        var prose = string.Empty;
        if (contentText is not null)
        {
            _envelope.Append(contentText);
            prose = _scanner.Push(contentText);

            if (_envelope.Length > MaxEnvelopeChars) return AbandonAndFlush(eventBytes);
        }

        var originalFinishReason = choice["finish_reason"]?.GetValue<string>();

        // Clone the original delta's other fields (role, or anything a future upstream adds) - only
        // "content" is ever replaced or removed.
        var outputDelta = originalDelta is not null
            ? originalDelta.DeepClone() as JsonObject ?? new JsonObject()
            : new JsonObject();
        outputDelta.Remove("content");

        if (prose.Length > 0) outputDelta["content"] = prose;

        if (originalFinishReason is null)
        {
            // Mid-stream with nothing decodable yet (the envelope's opening braces, or a partial escape)
            // - suppress the chunk rather than emitting an empty delta.
            if (outputDelta.Count == 0) return null;

            return BuildChunk(chunk: chunk, choice: choice, outputDelta: outputDelta, null);
        }

        // The assistant message is complete: the envelope is whole, so its calls can be resolved and stated
        // on this same chunk. That is what lets the client see finish_reason "tool_calls" here rather than
        // "stop" followed by an orphaned call.
        ResolveEnvelopeParts(calls: out var calls, unparsedRemainder: out var unparsedRemainder);

        if (unparsedRemainder is not null) outputDelta["content"] = prose + unparsedRemainder;

        if (calls.Count > 0) outputDelta["tool_calls"] = BuildToolCallsArray(calls);

        return BuildChunk(chunk: chunk, choice: choice, outputDelta: outputDelta,
            finishReason: calls.Count > 0 ? "tool_calls" : originalFinishReason);
    }

    /// <summary>
    /// Gives up on the envelope, forwarding everything accumulated so far as ordinary content, and lets the
    /// remainder of the stream through untouched.
    /// </summary>
    /// <remarks>
    /// Only reached when a server ignored the schema entirely, since a real envelope's prose is streamed out
    /// as it arrives rather than accumulating. What is emitted is the raw text minus nothing - the client
    /// may see JSON scaffolding, which is the visible-and-recoverable failure this codebase prefers to a
    /// silently truncated reply.
    /// </remarks>
    private byte[] AbandonAndFlush(byte[] eventBytes)
    {
        _abandoned = true;

        _logger.LogWarning(
            message:
            "Constrained tool calling: the reply from {Provider}/{Model} exceeded {Limit} buffered characters without completing a JSON envelope; forwarding it as plain content.",
            SanitizeForLog(_plan.ProviderKey),
            SanitizeForLog(_plan.ModelName),
            MaxEnvelopeChars);

        var raw = _envelope.ToString();
        _envelope.Clear();

        var passthrough = PassthroughRawEvent(eventBytes);
        if (_scanner.HasEmitted || BuildSyntheticChunk(content: raw, calls: []) is not { } heldChunk)
            // Prose already went out decoded; re-emitting the raw envelope would repeat it.
            return passthrough;

        var combined = new byte[heldChunk.Length + passthrough.Length];
        heldChunk.CopyTo(array: combined, 0);
        passthrough.CopyTo(array: combined, index: heldChunk.Length);
        return combined;
    }

    /// <summary>
    /// Parses the accumulated envelope once, yielding its calls and - when it was not an envelope at all -
    /// the text still owed to the client.
    /// </summary>
    /// <param name="calls">The calls the envelope carried, empty when it carried none or failed to parse.</param>
    /// <param name="unparsedRemainder">
    /// Raw text to emit as content because the envelope could not be parsed, or <see langword="null"/> when
    /// nothing is owed. Non-null only when no prose was streamed: once the scanner has emitted decoded text,
    /// forwarding the raw envelope too would duplicate it and expose the JSON around it.
    /// </param>
    /// <returns><see langword="true"/> when the envelope parsed.</returns>
    private bool ResolveEnvelopeParts(out IReadOnlyList<ExtractedToolCall> calls, out string? unparsedRemainder)
    {
        calls = [];
        unparsedRemainder = null;

        if (_resolved || _abandoned) return true;

        _resolved = true;

        var raw = _envelope.ToString();
        _envelope.Clear();

        if (raw.Length == 0) return true;

        if (ToolCallEnvelopeParser.TryParse(envelopeJson: raw, prose: out _, calls: out var parsedCalls))
        {
            calls = parsedCalls;
            return true;
        }

        _logger.LogWarning(
            message:
            "Constrained tool calling: the streamed reply from {Provider}/{Model} was not the requested JSON envelope; forwarding it as plain content.",
            SanitizeForLog(_plan.ProviderKey),
            SanitizeForLog(_plan.ModelName));

        // Nothing decoded means nothing has reached the client yet, so the raw text is still owed. If prose
        // did stream, the user has already seen the readable part and repeating it would be worse than
        // losing the scaffolding.
        unparsedRemainder = _scanner.HasEmitted ? null : raw;
        return false;
    }

    /// <summary>
    /// Resolves the envelope outside a finish chunk (i.e. from <see cref="Flush"/>, for a stream that ended
    /// without one) and packages whatever it yielded into a synthetic chunk, or returns null when there is
    /// nothing left to say.
    /// </summary>
    private byte[]? ResolveTrailingEnvelope()
    {
        if (_resolved || _abandoned) return null;

        ResolveEnvelopeParts(calls: out var calls, unparsedRemainder: out var remainder);
        return BuildSyntheticChunk(content: remainder ?? string.Empty, calls: calls);
    }

    /// <summary>Rebuilds one upstream chunk with a replaced delta and finish reason.</summary>
    private static byte[] BuildChunk(JsonObject chunk, JsonObject choice, JsonObject outputDelta, string? finishReason)
    {
        var outputChoice = new JsonObject
        {
            ["index"] = choice["index"]?.DeepClone() ?? 0,
            ["delta"] = outputDelta,
            ["finish_reason"] = finishReason
        };

        if (choice["logprobs"] is { } logprobs) outputChoice["logprobs"] = logprobs.DeepClone();

        var outputChunk = chunk.DeepClone() as JsonObject ?? new JsonObject();
        outputChunk["choices"] = new JsonArray { outputChoice };

        var json = JsonSerializer.Serialize(value: outputChunk, options: SerializerOptions);
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    /// <summary>Converts extracted calls into OpenAI-shaped tool_calls delta entries.</summary>
    private static JsonArray BuildToolCallsArray(IReadOnlyList<ExtractedToolCall> calls)
    {
        var array = new JsonArray();
        for (var index = 0; index < calls.Count; index++)
            array.Add(new JsonObject
            {
                ["index"] = index,
                ["id"] = $"call_{index}_{Guid.NewGuid():N}",
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = calls[index].Name,
                    ["arguments"] = calls[index].ArgumentsJson
                }
            });

        return array;
    }

    /// <summary>
    /// Wraps leftover content and/or calls into a synthetic chunk, reusing the id/created/model of the last
    /// real upstream chunk so it matches every other chunk on this stream. Returns null when there is
    /// nothing to say.
    /// </summary>
    private byte[]? BuildSyntheticChunk(string content, IReadOnlyList<ExtractedToolCall> calls)
    {
        if (content.Length == 0 && calls.Count == 0) return null;

        var delta = new JsonObject();
        if (content.Length > 0) delta["content"] = content;

        if (calls.Count > 0) delta["tool_calls"] = BuildToolCallsArray(calls);

        var chunk = new JsonObject
        {
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = calls.Count > 0 ? "tool_calls" : null
                }
            }
        };

        if (_lastChunkId is not null) chunk["id"] = _lastChunkId.DeepClone();

        if (_lastChunkCreated is not null) chunk["created"] = _lastChunkCreated.DeepClone();

        if (_lastChunkModel is not null) chunk["model"] = _lastChunkModel.DeepClone();

        var json = JsonSerializer.Serialize(value: chunk, options: SerializerOptions);
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    /// <summary>
    /// Pulls the <c>data:</c> field value out of one SSE event, joining multiple <c>data:</c> lines with <c>"\n"</c>
    /// per the SSE spec.
    /// </summary>
    private static string? ExtractDataPayload(byte[] eventBytes)
    {
        var text = Encoding.UTF8.GetString(eventBytes);
        StringBuilder? data = null;

        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith(value: "data:", comparisonType: StringComparison.Ordinal)) continue;

            if (data is null)
                data = new StringBuilder();
            else
                data.Append('\n');

            var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
            data.Append(value);
        }

        return data?.ToString();
    }

    /// <summary>
    /// Strips CR/LF from a config-controlled value before it enters a log template, so a crafted provider or
    /// model name cannot forge additional log lines (CodeQL: log forging, CWE-117).
    /// </summary>
    private static string SanitizeForLog(string value)
    {
        return value.Replace(oldValue: "\r", newValue: " ").Replace(oldValue: "\n", newValue: " ");
    }
}