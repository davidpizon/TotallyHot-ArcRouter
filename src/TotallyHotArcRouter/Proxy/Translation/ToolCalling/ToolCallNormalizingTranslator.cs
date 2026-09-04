using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Rewrites a model's dialect-framed tool call into a real OpenAI <c>tool_calls</c> field, for one
/// request (<c>docs/router/tool-call-normalization.md</c> Phase 4). Replaces
/// <c>ToolCallEchoGuardTranslator</c>, which did the same job for exactly one dialect on a
/// provider-wide flag.
/// <para>
/// Constructed per request by <see cref="ToolCallNormalizerFactory"/> rather than registered as a
/// singleton in <c>ServiceCollectionExtensions</c>' provider-keyed translator map, because what it scans
/// for is a property of the (provider, model) pair and the request's own <c>tools</c> field - none of
/// which a provider-keyed lookup can express. <see cref="Provider"/> is therefore a placeholder, never
/// consulted for routing.
/// </para>
/// <para>
/// Response-only (<see cref="IResponseOnlyTranslator"/>): the request is forwarded exactly as it would be
/// with no translator at all, <c>tools</c> intact. Rewriting the request belongs to
/// <see cref="ToolCallEmulatingTranslator"/>, which wraps this type unchanged for its own response half.
/// </para>
/// <para>
/// Never throws. A framed region that does not parse into a call is a heuristic miss, not an upstream
/// protocol error, so it fails open - the raw text is forwarded with its delimiters intact and a warning
/// logged, exactly as <c>unified-api-translation.md</c> §4.5 specifies. A wrong rewrite that degrades to
/// "the operator sees the model's raw output" is recoverable; one that truncates or fails the response is
/// not.
/// </para>
/// </summary>
internal sealed class ToolCallNormalizingTranslator : IResponseOnlyTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IToolCallCapabilityStore? _capabilityStore;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolCallNormalizingTranslator"/> class.</summary>
    /// <param name="plan">Which dialects to scan for, and whether to record what this response reveals.</param>
    /// <param name="capabilityStore">The store observations are written back to; <see langword="null"/> disables write-back.</param>
    /// <param name="logger">Logger for the fail-open warnings.</param>
    public ToolCallNormalizingTranslator(
        ToolCallNormalizationPlan plan,
        IToolCallCapabilityStore? capabilityStore,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(logger);

        Plan = plan;
        _capabilityStore = capabilityStore;
        _logger = logger;
    }

    /// <summary>Gets the dialects this request's response is armed to scan for.</summary>
    /// <remarks>Exposed so <see cref="ToolCallNormalizerFactory"/>'s arming decisions can be asserted without a live response.</remarks>
    public ToolCallNormalizationPlan Plan { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Never consulted for routing - see this type's remarks. A descriptive placeholder rather than an empty string,
    /// in case it surfaces in a log or debugger view.
    /// </remarks>
    public string Provider => "tool-call-normalizer";

    /// <inheritdoc/>
    /// <remarks>Identity - <see cref="ProxyMiddleware"/> never calls this for an <see cref="IResponseOnlyTranslator"/>.</remarks>
    public Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming)
    {
        return baseUrl;
    }

    /// <inheritdoc/>
    /// <remarks>Identity - the request is forwarded with <c>tools</c> intact; only the response is scanned.</remarks>
    public byte[] TranslateRequest(byte[] openAiShapedBody)
    {
        return openAiShapedBody;
    }

    /// <inheritdoc/>
    public byte[] TranslateResponse(byte[] nativeShapedBody)
    {
        var recorder = new ToolCallObservationRecorder(plan: Plan, store: _capabilityStore);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(nativeShapedBody) as JsonObject ??
                   throw new JsonException("Response body was not a JSON object.");
        }
        catch (JsonException)
        {
            // Not a JSON object we can safely rewrite - forward unchanged (defensive; a real
            // OpenAI-shaped response always parses).
            return nativeShapedBody;
        }

        if (root["choices"] is not JsonArray { Count: 1 } choices || choices[0] is not JsonObject choice)
        {
            // Zero choices is nothing to scan; more than one (a client-supplied "n" > 1) is outside this
            // translator's scope - it rewrites a single choice, so rewriting choices[0] and leaving the
            // rest untouched would be an inconsistent, half-applied response.
            if (root["choices"] is JsonArray { Count: > 1 } multiChoice)
                _logger.LogWarning(
                    message:
                    "Tool-call normalization: response had {ChoiceCount} choices; only a single choice per response is supported, so it is forwarded unrewritten to avoid a partial rewrite.",
                    multiChoice.Count);

            return nativeShapedBody;
        }

        if (choice["message"] is not JsonObject message) return nativeShapedBody;

        if (message["tool_calls"] is JsonArray { Count: > 0 })
        {
            // The model already produced a real tool_calls field. Never overwrite it - and record what
            // that proves, so every later request for this model skips this translator entirely
            // (performance rule 2). Content might still contain dialect-shaped text, but a model that
            // emitted a structured call and *also* wrote an example of one in prose is exactly the
            // false-positive case per-model detection exists to avoid.
            //
            // The Count check is load-bearing, not defensive tidiness. LM Studio emits "tool_calls": []
            // on *every* non-streaming response, prose included - so a plain `is not null` test read every
            // reply from the server at the center of this whole workstream as a native tool call. That
            // silently disabled normalization for the response in hand, and, far worse, recorded
            // openai-native at Observed confidence: the highest automatic tier, which no template or
            // model-id scan may overwrite. One non-streaming request was enough to permanently disable
            // tool-call normalization for that model, including for the streaming clients that were
            // working. Found by probing a live LM Studio, not by reading.
            recorder.RecordNative();
            return nativeShapedBody;
        }

        if (message["content"] is not JsonValue contentValue ||
            !contentValue.TryGetValue<string>(out var content) ||
            string.IsNullOrEmpty(content))
            return nativeShapedBody;

        var match = DialectMatcher.MatchAny(content: content, candidates: Plan.Candidates);
        if (match is null)
        {
            WarnIfARegionWentUnparsed(content);

            // No evidence either way: the model answered in prose, or a framed region failed to parse and
            // was failed open. Nothing to rewrite, and nothing worth recording - see
            // ToolCallObservationRecorder on why an unmatched response must not count against the model.
            return nativeShapedBody;
        }

        message["content"] = match.ResidualContent.Length > 0 ? match.ResidualContent : null;

        var toolCallsArray = new JsonArray();
        var index = 0;
        foreach (var call in match.ToolCalls)
        {
            toolCallsArray.Add(new JsonObject
            {
                ["id"] = $"call_{index}_{Guid.NewGuid():N}",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson }
            });
            index++;
        }

        message["tool_calls"] = toolCallsArray;
        choice["finish_reason"] = "tool_calls";

        recorder.RecordMatch(match.Dialect);

        return Encoding.UTF8.GetBytes(root.ToJsonString(SerializerOptions));
    }

    /// <inheritdoc/>
    public IStreamTranslator CreateStreamTranslator()
    {
        return new ToolCallNormalizingStreamTranslator(plan: Plan, capabilityStore: _capabilityStore, logger: _logger);
    }

    /// <summary>
    /// Logs the fail-open warning when the content carried a region that looked like a tool call and
    /// yielded none - the schema-echo and malformed-body cases.
    /// <para>
    /// The three messages here are deliberately the same three
    /// <see cref="ToolCallNormalizingStreamTranslator"/> emits, chosen by the same conditions, because a
    /// response that arrives buffered and one that arrives streamed must not be described differently. An
    /// earlier version collapsed all of them into one "content framed by X" line, which was wrong twice
    /// over: an opener with no closer is not framed, and a close-less dialect's opener appearing in prose
    /// is not a failed tool call at all.
    /// </para>
    /// <para>
    /// A close-less dialect (Mistral's <c>[TOOL_CALLS]</c>, Llama's <c>&lt;|python_tag|&gt;</c>) owns the
    /// rest of the message by definition, so its opener alone proves nothing - a model naming the token
    /// while explaining tool calling would warn on every such response. It is reported only when a
    /// <c>{</c> follows, which is the cheapest available evidence that a payload was actually attempted.
    /// </para>
    /// <para>
    /// Guarded by the same <see cref="string.IndexOfAny(char[])"/> pre-filter the scanner uses, so
    /// ordinary prose - nearly all output - costs one vectorized scan rather than a delimiter search per
    /// dialect.
    /// </para>
    /// </summary>
    private void WarnIfARegionWentUnparsed(string content)
    {
        if (content.IndexOfAny(Plan.OpenerFirstChars) < 0) return;

        foreach (var dialect in Plan.Candidates)
            foreach (var delimiter in dialect.Delimiters)
            {
                var openIndex = content.IndexOf(value: delimiter.Open, comparisonType: StringComparison.Ordinal);
                if (openIndex < 0) continue;

                var bodyStart = openIndex + delimiter.Open.Length;

                if (delimiter.Close is null)
                {
                    // No payload was even attempted - keep looking, since another dialect may own a real
                    // region later in the same message.
                    if (content.IndexOf('{', startIndex: bodyStart) < 0) continue;

                    _logger.LogWarning(
                        message:
                        "Tool-call normalization: the text after {OpenToken} did not contain a valid tool call; forwarding it as plain content.",
                        delimiter.Open);
                    return;
                }

                var closeIndex = content.IndexOf(value: delimiter.Close, startIndex: bodyStart,
                    comparisonType: StringComparison.Ordinal);
                if (closeIndex < 0)
                    _logger.LogWarning(
                        message:
                        "Tool-call normalization: the message ended with an unterminated {OpenToken} block; forwarding the raw text as plain content.",
                        delimiter.Open);
                else
                    _logger.LogWarning(
                        message:
                        "Tool-call normalization: {OpenToken}...{CloseToken} did not contain a valid tool call; forwarding the raw text as plain content.",
                        delimiter.Open,
                        delimiter.Close);

                return;
            }
    }
}