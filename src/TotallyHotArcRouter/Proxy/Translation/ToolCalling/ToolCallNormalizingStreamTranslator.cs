using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Per-request, stateful translator for one already-OpenAI-shaped streaming response, scanning each
/// chunk's <c>delta.content</c> for a tool call framed in any of the dialects
/// <see cref="ToolCallNormalizerFactory"/> armed for this request, and rewriting it into a real
/// <c>tool_calls</c> delta before it reaches the client (<c>docs/router/tool-call-normalization.md</c>
/// Phase 4). Created per response by
/// <see cref="ToolCallNormalizingTranslator.CreateStreamTranslator"/>; not thread-safe.
///
/// <para>
/// This is the layer that exists because a slow local model streams one token per SSE event, so a
/// delimiter - or its closing counterpart - can be split across arbitrarily many chunks. That
/// cross-chunk accumulation is the only thing this type owns; the moment it holds a complete region body
/// it hands off to <see cref="DialectMatcher.ExtractFromRegionBody"/>, so the streaming and buffered
/// paths cannot drift about what counts as a call.
/// </para>
///
/// <para>
/// The upstream body needs no structural reshaping - it already is an OpenAI
/// <c>chat.completion.chunk</c> - so only <c>choices[0].delta.content</c>/<c>.tool_calls</c> and
/// <c>choices[0].finish_reason</c> are ever rewritten; every other field's <em>value</em> is carried
/// over unchanged via <see cref="JsonNode.DeepClone"/>. This is not a byte-for-byte passthrough, though:
/// each event is parsed and re-serialized, so exact formatting is not preserved. Scoped to
/// <c>choices[0]</c>, matching this codebase's single-choice assumption elsewhere: a chunk with more
/// than one choice (a client-supplied <c>n</c> &gt; 1) fails open rather than being rewritten, so no
/// choice's content is lost.
/// </para>
///
/// <para>
/// Never throws. A region that fails to parse is forwarded as ordinary content, delimiters included,
/// with a warning - see <see cref="ToolCallNormalizingTranslator"/>'s remarks.
/// </para>
/// </summary>
internal sealed class ToolCallNormalizingStreamTranslator : IStreamTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] DoneLine = Encoding.UTF8.GetBytes("data: [DONE]\n\n");

    /// <summary>
    /// The cap on text held inside an open region before it is abandoned and flushed as ordinary content
    /// (§3.4 performance rule 4). Without it, a single stray <c>&lt;tool_call&gt;</c> - or a Mistral
    /// <c>[TOOL_CALLS]</c> opener, which by design has no closing token and so runs to the end of the
    /// message - holds an entire response in memory and delays every byte of it. 64 KiB is far above any
    /// real tool call's arguments and far below a response worth buffering whole.
    /// </summary>
    private const int MaxBufferedRegionChars = 64 * 1024;

    private readonly ToolCallNormalizationPlan _plan;
    private readonly ToolCallObservationRecorder _recorder;
    private readonly ILogger _logger;

    // Every armed opener, paired with the dialect that owns it, so a match knows what it matched.
    private readonly IReadOnlyList<(ToolCallDialect Dialect, DialectDelimiter Delimiter)> _openers;
    private readonly IReadOnlyList<string> _openerTokens;

    // Raw upstream bytes not yet forming a complete SSE event - the same "\n\n"-delimited framing every
    // stream translator here implements; CR stripped on ingestion.
    private readonly List<byte> _sseBuffer = new();

    // Text carried across Push calls that is not yet classifiable as safe-to-emit content, an opening
    // delimiter, or an open region's accumulated body.
    private string _pending = string.Empty;
    private bool _insideRegion;
    private ToolCallDialect? _activeDialect;
    private DialectDelimiter? _activeDelimiter;
    private readonly StringBuilder _regionInner = new();

    private int _toolCallIndex;
    private bool _hasSynthesizedToolCalls;

    // Set once this response has proved it emits native OpenAI tool_calls. From that point every event is
    // forwarded raw: a model that already speaks the protocol must never have its prose *example* of a
    // tool call rewritten into a real invocation, which is the false-positive provider-wide arming caused.
    private bool _disarmed;

    // Set once this stream has logged its one multi-choice ("n" > 1) fail-open warning, so a long
    // multi-choice stream doesn't warn on every chunk.
    private bool _loggedMultiChoiceWarning;

    // Captured from the most recently parsed upstream chunk so a synthesized chunk built in Flush - which
    // has no upstream chunk of its own - still carries the id/created/model fields real OpenAI clients
    // expect on every chunk.
    private JsonNode? _lastChunkId;
    private JsonNode? _lastChunkCreated;
    private JsonNode? _lastChunkModel;

    /// <summary>Initializes a new instance of the <see cref="ToolCallNormalizingStreamTranslator"/> class.</summary>
    /// <param name="plan">Which dialects to scan for, and whether to record what this response reveals.</param>
    /// <param name="capabilityStore">The store observations are written back to; <see langword="null"/> disables write-back.</param>
    /// <param name="logger">Logger for the fail-open warnings.</param>
    public ToolCallNormalizingStreamTranslator(
        ToolCallNormalizationPlan plan,
        IToolCallCapabilityStore? capabilityStore,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(logger);

        _plan = plan;
        _logger = logger;
        _recorder = new ToolCallObservationRecorder(plan, capabilityStore);

        _openers = [.. plan.Candidates.SelectMany(d => d.Delimiters.Select(delim => (Dialect: d, Delimiter: delim)))];
        _openerTokens = [.. _openers.Select(o => o.Delimiter.Open)];

        // The factory only ever arms scannable dialects, so this is a programming error rather than a
        // runtime condition - but the scan below indexes _openers[0] unconditionally, so state it here
        // instead of surfacing it as an IndexOutOfRangeException on the first content chunk.
        if (_openers.Count == 0)
        {
            throw new ArgumentException("A normalization plan must arm at least one scannable dialect.", nameof(plan));
        }
    }

    /// <inheritdoc />
    public byte[] Push(ReadOnlySpan<byte> upstreamChunk)
    {
        foreach (var b in upstreamChunk)
        {
            if (b != (byte)'\r')
            {
                _sseBuffer.Add(b);
            }
        }

        using var output = new MemoryStream();

        int delimiter;
        while ((delimiter = IndexOfDoubleNewline()) >= 0)
        {
            var eventBytes = _sseBuffer.GetRange(0, delimiter).ToArray();
            _sseBuffer.RemoveRange(0, delimiter + 2);

            if (ProcessEvent(eventBytes) is { } translated)
            {
                output.Write(translated, 0, translated.Length);
            }
        }

        return output.ToArray();
    }

    /// <inheritdoc />
    public byte[] Flush()
    {
        using var output = new MemoryStream();

        // A final event without its trailing blank line (some servers omit it on the last event).
        if (_sseBuffer.Count > 0)
        {
            var remaining = _sseBuffer.ToArray();
            _sseBuffer.Clear();
            if (ProcessEvent(remaining) is { } translated)
            {
                output.Write(translated, 0, translated.Length);
            }
        }

        // Anything still held back is lost data if silently dropped. This is the backstop for a stream
        // that ended without ever sending a finish_reason - when one arrives, the region is resolved there
        // instead, which is what lets a close-less dialect's calls be emitted before the finish chunk
        // rather than after it.
        var contentOut = new StringBuilder();
        var callsOut = new List<ExtractedToolCall>();
        FinishOpenRegion(contentOut, callsOut);

        var leftover = contentOut.ToString();
        if ((leftover.Length > 0 || callsOut.Count > 0) &&
            BuildSyntheticChunk(leftover, callsOut) is { } leftoverChunk)
        {
            output.Write(leftoverChunk, 0, leftoverChunk.Length);
        }

        output.Write(DoneLine, 0, DoneLine.Length);
        return output.ToArray();
    }

    /// <summary>Finds the first blank-line separator (<c>\n\n</c>) marking a complete SSE event, or -1 when none is buffered yet.</summary>
    private int IndexOfDoubleNewline()
    {
        for (var i = 0; i + 1 < _sseBuffer.Count; i++)
        {
            if (_sseBuffer[i] == (byte)'\n' && _sseBuffer[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Forwards one raw SSE event exactly as buffered, reattaching the <c>"\n\n"</c> delimiter Push/Flush
    /// stripped off. The shared fail-open primitive for every case where this translator declines to
    /// rewrite an event: re-serializing the parsed JSON instead would corrupt an event with multiple
    /// <c>data:</c> lines (SSE joins them with <c>"\n"</c>, not one) or silently drop any non-<c>data:</c>
    /// line (<c>event:</c>/<c>id:</c>). Not a literal byte-for-byte passthrough of the wire bytes -
    /// <see cref="Push"/> discards every <c>'\r'</c> before buffering, so a CRLF-terminated upstream event
    /// is normalized to LF here as everywhere else.
    /// </summary>
    private static byte[] PassthroughRawEvent(byte[] eventBytes)
    {
        var passthrough = new byte[eventBytes.Length + 2];
        eventBytes.CopyTo(passthrough, 0);
        passthrough[^2] = (byte)'\n';
        passthrough[^1] = (byte)'\n';
        return passthrough;
    }

    /// <summary>Translates one raw SSE event, or returns null when nothing is client-visible yet (fully buffered) or the event carries no choices to inspect.</summary>
    private byte[]? ProcessEvent(byte[] eventBytes)
    {
        var payload = ExtractDataPayload(eventBytes);
        if (payload is null || payload == "[DONE]")
        {
            return null;
        }

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
        {
            // Nothing to scan (e.g. a trailing usage-only chunk) - forward unchanged.
            return PassthroughRawEvent(eventBytes);
        }

        if (choices.Count > 1)
        {
            // Outside this translator's scope - its region state machine tracks one choices[0] stream.
            // Rewriting choices[0] and replacing the array with just that entry would silently drop every
            // other choice's content, so fail open with all choices intact.
            if (!_loggedMultiChoiceWarning)
            {
                _loggedMultiChoiceWarning = true;
                _logger.LogWarning(
                    "Tool-call normalization: chunk had {ChoiceCount} choices; only a single choice per chunk is supported, so it is forwarded unrewritten to avoid dropping data. Logged once per stream.",
                    choices.Count);
            }

            return PassthroughRawEvent(eventBytes);
        }

        if (_disarmed)
        {
            return PassthroughRawEvent(eventBytes);
        }

        var originalDelta = choice["delta"] as JsonObject;

        if (originalDelta?["tool_calls"] is JsonArray { Count: > 0 })
        {
            // The model speaks the protocol natively. Record that - every later request for it skips this
            // translator entirely - and stop scanning for the rest of this response.
            //
            // Count-checked for the same reason as the buffered path: an empty tool_calls array is a
            // server emitting the field unconditionally, not a model calling a tool. LM Studio does exactly
            // that on its non-streaming responses. Its streaming deltas omit the field entirely today, so
            // this half was not actually broken - but the two paths must agree about what a native call is,
            // and a server that starts sending "tool_calls": [] in a delta must not disarm the scanner.
            _recorder.RecordNative();
            _disarmed = true;

            // Whatever was being held back was ordinary content after all; emit it verbatim ahead of this
            // chunk so it is not lost. Deliberately *not* resolved through the region logic first - this
            // model has just proved it does not speak a text dialect, so extracting calls out of text it
            // wrote would be the exact false positive disarming exists to prevent.
            var held = TakeHeldTextRaw();

            var rawEvent = PassthroughRawEvent(eventBytes);
            if (held.Length == 0 || BuildSyntheticChunk(held, []) is not { } heldChunk)
            {
                return rawEvent;
            }

            var combined = new byte[heldChunk.Length + rawEvent.Length];
            heldChunk.CopyTo(combined, 0);
            rawEvent.CopyTo(combined, heldChunk.Length);
            return combined;
        }

        var hasContent = originalDelta?["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out _);
        var contentText = hasContent ? originalDelta!["content"]!.GetValue<string>() : null;

        var emitted = new StringBuilder();
        var emittedCalls = new List<ExtractedToolCall>();
        if (contentText is not null)
        {
            ProcessContent(contentText, emitted, emittedCalls);
        }

        var originalFinishReason = choice["finish_reason"]?.GetValue<string>();

        // A finish_reason means the assistant message is complete, which is the only place a close-less
        // dialect's region can end (Mistral's [TOOL_CALLS] has no closing token by design). Resolving it
        // here rather than in Flush is what lets its calls be emitted on the finish chunk itself, so the
        // client sees finish_reason "tool_calls" rather than "stop" followed by an orphaned tool call.
        if (originalFinishReason is not null)
        {
            FinishOpenRegion(emitted, emittedCalls);
        }

        // Clone the original delta's other fields (role, or anything a future upstream adds) - only
        // "content" is ever replaced or removed.
        var outputDelta = originalDelta is not null
            ? originalDelta.DeepClone() as JsonObject ?? new JsonObject()
            : new JsonObject();
        outputDelta.Remove("content");

        var emittedContent = emitted.ToString();
        if (emittedContent.Length > 0)
        {
            outputDelta["content"] = emittedContent;
        }

        if (emittedCalls.Count > 0)
        {
            outputDelta["tool_calls"] = BuildToolCallsArray(emittedCalls);
            _hasSynthesizedToolCalls = true;
        }

        var finishReason = originalFinishReason is not null && _hasSynthesizedToolCalls
            ? "tool_calls"
            : originalFinishReason;

        // Nothing changed and nothing to say for this event (fully buffered mid-region, no role, no finish
        // reason) - suppress the chunk rather than emitting an empty delta.
        if (outputDelta.Count == 0 && finishReason is null)
        {
            return null;
        }

        var outputChoice = new JsonObject
        {
            ["index"] = choice["index"]?.DeepClone() ?? 0,
            ["delta"] = outputDelta,
            ["finish_reason"] = finishReason,
        };

        if (choice["logprobs"] is { } logprobs)
        {
            outputChoice["logprobs"] = logprobs.DeepClone();
        }

        var outputChunk = chunk.DeepClone() as JsonObject ?? new JsonObject();
        outputChunk["choices"] = new JsonArray { outputChoice };

        var json = JsonSerializer.Serialize(outputChunk, SerializerOptions);
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    /// <summary>
    /// Feeds newly-arrived content text through the region state machine, appending whatever plain text is
    /// now safe to emit to <paramref name="contentOut"/> and any calls whose region completed during this
    /// call to <paramref name="callsOut"/>. Text that might still turn into a delimiter - or a region body
    /// still awaiting its close - stays buffered for the next call.
    /// </summary>
    private void ProcessContent(string newContent, StringBuilder contentOut, List<ExtractedToolCall> callsOut)
    {
        _pending += newContent;

        while (true)
        {
            if (!_insideRegion)
            {
                // Performance rule 3: one vectorized scan for any armed opener's first character, before
                // any per-delimiter search. Ordinary prose - nearly all output - stops here. A partial
                // suffix that could still become a delimiter necessarily contains that first character
                // too, so a miss here is also proof there is nothing to hold back.
                if (_pending.IndexOfAny(_plan.OpenerFirstChars) < 0)
                {
                    contentOut.Append(_pending);
                    _pending = string.Empty;
                    return;
                }

                if (TryFindEarliestOpener(_pending, out var openIndex, out var dialect, out var delimiter))
                {
                    contentOut.Append(_pending, 0, openIndex);
                    _pending = _pending[(openIndex + delimiter.Open.Length)..];
                    _insideRegion = true;
                    _activeDialect = dialect;
                    _activeDelimiter = delimiter;
                    _regionInner.Clear();
                    continue;
                }

                var holdBack = Math.Min(_pending.Length, LongestPartialSuffixMatch(_pending, _openerTokens));
                var safeLength = _pending.Length - holdBack;
                if (safeLength <= 0)
                {
                    return;
                }

                contentOut.Append(_pending, 0, safeLength);
                _pending = _pending[safeLength..];
                continue;
            }

            var closeToken = _activeDelimiter!.Close;

            if (closeToken is null)
            {
                // No closing token: the payload owns the rest of the message, so it can only be resolved
                // once a finish_reason arrives (or the stream ends). Accumulate and wait.
                _regionInner.Append(_pending);
                _pending = string.Empty;
                AbandonRegionIfOversized(contentOut);
                return;
            }

            var closeIndex = _pending.IndexOf(closeToken, StringComparison.Ordinal);
            if (closeIndex >= 0)
            {
                _regionInner.Append(_pending, 0, closeIndex);
                _pending = _pending[(closeIndex + closeToken.Length)..];
                CloseRegion(closeToken, contentOut, callsOut);
                continue;
            }

            var closeHoldBack = Math.Min(_pending.Length, LongestPartialSuffixMatch(_pending, [closeToken]));
            var safeInnerLength = _pending.Length - closeHoldBack;
            if (safeInnerLength > 0)
            {
                _regionInner.Append(_pending, 0, safeInnerLength);
                _pending = _pending[safeInnerLength..];
            }

            if (AbandonRegionIfOversized(contentOut))
            {
                continue;
            }

            if (safeInnerLength <= 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Resolves a region whose closing token has just arrived: its body becomes tool calls, or - when it
    /// framed nothing parseable, most often the tool *schema* echoed back - is forwarded verbatim with its
    /// delimiters intact.
    /// </summary>
    private void CloseRegion(string closeToken, StringBuilder contentOut, List<ExtractedToolCall> callsOut)
    {
        var dialect = _activeDialect!;
        var openToken = _activeDelimiter!.Open;
        var inner = _regionInner.ToString();

        _regionInner.Clear();
        _insideRegion = false;
        _activeDialect = null;
        _activeDelimiter = null;

        var extracted = DialectMatcher.ExtractFromRegionBody(inner, dialect);
        if (extracted.Count > 0)
        {
            callsOut.AddRange(extracted);
            _recorder.RecordMatch(dialect);
            return;
        }

        _logger.LogWarning(
            "Tool-call normalization: {OpenToken}...{CloseToken} did not contain a valid tool call; forwarding the raw text as plain content.",
            openToken,
            closeToken);
        contentOut.Append(openToken).Append(inner).Append(closeToken);
    }

    /// <summary>
    /// Resolves whatever is still held back because the assistant message has ended - either a
    /// finish_reason arrived or the stream did. A close-less region's body is complete at this point and
    /// is extracted; a region still waiting for a closing token never got one, so it fails open with its
    /// raw text.
    /// </summary>
    private void FinishOpenRegion(StringBuilder contentOut, List<ExtractedToolCall> callsOut)
    {
        if (!_insideRegion)
        {
            contentOut.Append(_pending);
            _pending = string.Empty;
            return;
        }

        _regionInner.Append(_pending);
        _pending = string.Empty;

        var dialect = _activeDialect!;
        var delimiter = _activeDelimiter!;
        var inner = _regionInner.ToString();

        _regionInner.Clear();
        _insideRegion = false;
        _activeDialect = null;
        _activeDelimiter = null;

        if (delimiter.Close is null)
        {
            var extracted = DialectMatcher.ExtractFromRegionBody(inner, dialect);
            if (extracted.Count > 0)
            {
                callsOut.AddRange(extracted);
                _recorder.RecordMatch(dialect);
                return;
            }

            _logger.LogWarning(
                "Tool-call normalization: the text after {OpenToken} did not contain a valid tool call; forwarding it as plain content.",
                delimiter.Open);
        }
        else
        {
            _logger.LogWarning(
                "Tool-call normalization: the message ended with an unterminated {OpenToken} block; forwarding the raw text as plain content.",
                delimiter.Open);
        }

        contentOut.Append(delimiter.Open).Append(inner);
    }

    /// <summary>
    /// Takes everything currently held back - an open region's opening delimiter and accumulated body,
    /// plus any unclassified trailing text - as literal text, and clears the state machine. Used only on
    /// the disarming path, where the point is to hand the text back untouched.
    /// </summary>
    private string TakeHeldTextRaw()
    {
        var held = new StringBuilder();

        if (_insideRegion)
        {
            held.Append(_activeDelimiter!.Open).Append(_regionInner);
            _regionInner.Clear();
            _insideRegion = false;
            _activeDialect = null;
            _activeDelimiter = null;
        }

        held.Append(_pending);
        _pending = string.Empty;
        return held.ToString();
    }

    /// <summary>
    /// Enforces performance rule 4: an open region that has swallowed more than
    /// <see cref="MaxBufferedRegionChars"/> is abandoned and its text flushed as ordinary content, so a
    /// stray delimiter cannot hold an entire response in memory.
    /// </summary>
    /// <returns><see langword="true"/> when the region was abandoned.</returns>
    private bool AbandonRegionIfOversized(StringBuilder contentOut)
    {
        if (_regionInner.Length <= MaxBufferedRegionChars)
        {
            return false;
        }

        _logger.LogWarning(
            "Tool-call normalization: a {OpenToken} block exceeded {Limit} buffered characters without completing; forwarding it as plain content.",
            _activeDelimiter!.Open,
            MaxBufferedRegionChars);

        contentOut.Append(_activeDelimiter.Open).Append(_regionInner);
        _regionInner.Clear();
        _insideRegion = false;
        _activeDialect = null;
        _activeDelimiter = null;
        return true;
    }

    /// <summary>Converts extracted calls into OpenAI-shaped tool_calls delta entries, assigning each the next index.</summary>
    private JsonArray BuildToolCallsArray(IReadOnlyList<ExtractedToolCall> calls)
    {
        var array = new JsonArray();
        foreach (var call in calls)
        {
            var index = _toolCallIndex++;
            array.Add(new JsonObject
            {
                ["index"] = index,
                ["id"] = $"call_{index}_{Guid.NewGuid():N}",
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                },
            });
        }

        return array;
    }

    /// <summary>
    /// Wraps leftover content and/or calls into a synthetic chunk, reusing the id/created/model of the
    /// last real upstream chunk so it matches the shape every other chunk on this stream had. Returns
    /// null when there is nothing to say.
    /// </summary>
    private byte[]? BuildSyntheticChunk(string content, IReadOnlyList<ExtractedToolCall> calls)
    {
        if (content.Length == 0 && calls.Count == 0)
        {
            return null;
        }

        var delta = new JsonObject();
        if (content.Length > 0)
        {
            delta["content"] = content;
        }

        if (calls.Count > 0)
        {
            delta["tool_calls"] = BuildToolCallsArray(calls);
            _hasSynthesizedToolCalls = true;
        }

        var chunk = new JsonObject
        {
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,

                    // A call synthesized here was resolved after the upstream's own finish chunk (or with
                    // no finish chunk at all), so this is the only place its finish_reason can be stated.
                    ["finish_reason"] = calls.Count > 0 ? "tool_calls" : null,
                },
            },
        };

        if (_lastChunkId is not null)
        {
            chunk["id"] = _lastChunkId.DeepClone();
        }

        if (_lastChunkCreated is not null)
        {
            chunk["created"] = _lastChunkCreated.DeepClone();
        }

        if (_lastChunkModel is not null)
        {
            chunk["model"] = _lastChunkModel.DeepClone();
        }

        var json = JsonSerializer.Serialize(chunk, SerializerOptions);
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    /// <summary>
    /// Finds the earliest armed opening delimiter in <paramref name="text"/>. On a tie at the same index
    /// the longer token wins, so a more specific delimiter is never shadowed by a shorter one that happens
    /// to be its prefix - the same rule <see cref="DialectMatcher"/> applies.
    /// </summary>
    private bool TryFindEarliestOpener(
        string text,
        out int openIndex,
        out ToolCallDialect dialect,
        out DialectDelimiter delimiter)
    {
        openIndex = -1;
        dialect = _openers[0].Dialect;
        delimiter = _openers[0].Delimiter;

        foreach (var (candidateDialect, candidateDelimiter) in _openers)
        {
            var index = text.IndexOf(candidateDelimiter.Open, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            if (openIndex < 0 || index < openIndex ||
                (index == openIndex && candidateDelimiter.Open.Length > delimiter.Open.Length))
            {
                openIndex = index;
                dialect = candidateDialect;
                delimiter = candidateDelimiter;
            }
        }

        return openIndex >= 0;
    }

    /// <summary>
    /// The length of the longest suffix of <paramref name="text"/> that is also a strict prefix of one of
    /// <paramref name="candidates"/> - i.e. how much trailing text might still turn into one of those
    /// tokens once more bytes arrive, and so must not be emitted yet.
    /// </summary>
    private static int LongestPartialSuffixMatch(string text, IReadOnlyList<string> candidates)
    {
        var best = 0;

        foreach (var candidate in candidates)
        {
            var limit = Math.Min(text.Length, candidate.Length - 1);
            for (var length = limit; length > best; length--)
            {
                if (length > 0 && string.CompareOrdinal(text, text.Length - length, candidate, 0, length) == 0)
                {
                    best = length;
                    break;
                }
            }
        }

        return best;
    }

    /// <summary>Pulls the <c>data:</c> field value out of one SSE event. Multiple <c>data:</c> lines within one event are joined with <c>"\n"</c> per the SSE spec. Returns null when the event has no data line.</summary>
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

            if (data is null)
            {
                data = new StringBuilder();
            }
            else
            {
                data.Append('\n');
            }

            var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
            data.Append(value);
        }

        return data?.ToString();
    }
}

