using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Gives a model reliable tool calling by constraining the server's sampler instead of trusting the model's
/// prose: the request carries a <c>response_format</c> JSON schema, and the reply - a
/// <c>{"content", "tool_calls"}</c> envelope the grammar made mandatory - is unpacked back into a real
/// OpenAI <c>tool_calls</c> field.
/// <para>
/// An <see cref="IClientPathTranslator"/>, like <see cref="ToolCallEmulatingTranslator"/>: the request body
/// is reshaped, but the upstream URL stays the client's own OpenAI-compatible path. Selected by
/// <see cref="ToolCallNormalizerFactory"/> when the provider's endpoint reports
/// <see cref="ProviderEndpointCapabilities.JsonSchemaResponseFormat"/>, which is why it never needs to ask
/// whether the server understands the field.
/// </para>
/// <para>
/// <b>A constrained reply is not evidence.</b> Exactly as with emulation, the envelope arrived because
/// TotallyHot.ArcRouter demanded it, so recording it as an observed dialect would be circular and self-erasing.
/// <see cref="ToolCallNormalizationPlan.IsEmulating"/> suppresses that write while still recording a native
/// <c>tool_calls</c> response - the one thing a constrained request cannot have manufactured, and the
/// signal that this model never needed constraining.
/// </para>
/// <para>
/// Never throws. A reply that is not the envelope is forwarded as ordinary content with a warning, matching
/// <see cref="ToolCallNormalizingTranslator"/>'s fail-open contract: a degraded response the operator can
/// see is recoverable, a truncated or failed one is not.
/// </para>
/// </summary>
internal sealed class ConstrainedToolCallTranslator : IClientPathTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IToolCallCapabilityStore? _capabilityStore;
    private readonly ModelContextWindow? _contextWindow;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ConstrainedToolCallTranslator"/> class.</summary>
    /// <param name="plan">The plan this request's response is read under.</param>
    /// <param name="dialect">The constrained dialect; must carry the instruction prompt used to describe the tools.</param>
    /// <param name="capabilityStore">
    /// The store a native-<c>tool_calls</c> observation is written back to;
    /// <see langword="null"/> disables write-back.
    /// </param>
    /// <param name="logger">Logger for the budget and fail-open warnings.</param>
    /// <param name="contextWindowStore">
    /// The store to read this model's probed context window from, so the tool-schema injection budget can
    /// scale to it instead of the fixed fallback; <see langword="null"/> (or no window on record for this
    /// model) keeps the fallback. Read once here, not per-request, because the plan's (provider, model) pair
    /// - and so the window - is fixed for this translator's lifetime.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="dialect"/> has no instruction prompt, so the model would be
    /// constrained to call tools it was never told about.
    /// </exception>
    public ConstrainedToolCallTranslator(
        ToolCallNormalizationPlan plan,
        ToolCallDialect dialect,
        IToolCallCapabilityStore? capabilityStore,
        ILogger logger,
        IModelContextWindowStore? contextWindowStore = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(logger);

        if (dialect.EmulationPrompt is null)
            // A programming error at the call site, not a runtime condition. Measured live: a schema without
            // tool descriptions produces a perfectly-shaped envelope carrying an invented answer, because the
            // grammar constrains the shape of a reply while telling the model nothing about what it may call.
            throw new ArgumentException(
                message:
                $"Dialect '{dialect.Name}' cannot drive constrained tool calling: it has no instruction prompt, so the tools would never be described to the model.",
                paramName: nameof(dialect));

        Plan = plan;
        Dialect = dialect;
        _capabilityStore = capabilityStore;
        _logger = logger;
        _contextWindow =
            contextWindowStore?.GetModelContextWindow(providerKey: plan.ProviderKey, modelName: plan.ModelName);
    }

    /// <summary>Gets the plan this request's response is read under.</summary>
    /// <remarks>Exposed so <see cref="ToolCallNormalizerFactory"/>'s selection can be asserted without a live response.</remarks>
    public ToolCallNormalizationPlan Plan { get; }

    /// <summary>Gets the dialect being enforced.</summary>
    public ToolCallDialect Dialect { get; }

    /// <inheritdoc/>
    /// <remarks>Never consulted for routing; constrained mode is selected per (provider, model), not per provider key.</remarks>
    public string Provider => "constrained-tool-call";

    /// <inheritdoc/>
    /// <remarks>
    /// Identity - never called, because <see cref="ProxyMiddleware"/> takes the upstream URL from the client's
    /// request path for an <see cref="IClientPathTranslator"/>.
    /// </remarks>
    public Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming)
    {
        return baseUrl;
    }

    /// <inheritdoc/>
    public byte[] TranslateRequest(byte[] openAiShapedBody)
    {
        return ConstrainedToolCallRewriter.Rewrite(openAiShapedBody: openAiShapedBody, dialect: Dialect,
            logger: _logger, contextWindow: _contextWindow);
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
            return nativeShapedBody;
        }

        if (root["choices"] is not JsonArray { Count: 1 } choices || choices[0] is not JsonObject choice)
        {
            if (root["choices"] is JsonArray { Count: > 1 } multiChoice)
                _logger.LogWarning(
                    message:
                    "Constrained tool calling: response had {ChoiceCount} choices; only a single choice per response is supported, so it is forwarded unrewritten to avoid a partial rewrite.",
                    multiChoice.Count);

            return nativeShapedBody;
        }

        if (choice["message"] is not JsonObject message) return nativeShapedBody;

        if (message["tool_calls"] is JsonArray { Count: > 0 })
        {
            // The model produced a real tool_calls field despite being handed a schema. Never overwrite it,
            // and record what that proves - this model never needed constraining.
            //
            // The Count check is load-bearing rather than defensive tidiness, and is the same guard
            // ToolCallNormalizingTranslator documents: LM Studio emits "tool_calls": [] on *every*
            // non-streaming response, so a plain null test would read every reply as a native call and
            // permanently record openai-native at Observed confidence.
            recorder.RecordNative();
            return nativeShapedBody;
        }

        if (message["content"] is not JsonValue contentValue ||
            !contentValue.TryGetValue<string>(out var content) ||
            string.IsNullOrEmpty(content))
            return nativeShapedBody;

        if (!ToolCallEnvelopeParser.TryParse(envelopeJson: content, prose: out var prose, calls: out var calls))
        {
            // The server did not honor the schema. Forward the raw text so the operator sees exactly what
            // came back, rather than an empty reply that looks like the model said nothing.
            _logger.LogWarning(
                message:
                "Constrained tool calling: the reply from {Provider}/{Model} was not the requested JSON envelope; forwarding it as plain content.",
                SanitizeForLog(Plan.ProviderKey),
                SanitizeForLog(Plan.ModelName));
            return nativeShapedBody;
        }

        if (calls.Count == 0)
        {
            // A legal, complete answer: the model declined to call anything. Unwrap the envelope so the
            // client sees prose instead of JSON, and leave finish_reason alone.
            message["content"] = prose.Length > 0 ? prose : null;
            return Encoding.UTF8.GetBytes(root.ToJsonString(SerializerOptions));
        }

        message["content"] = prose.Length > 0 ? prose : null;

        var toolCallsArray = new JsonArray();
        var index = 0;
        foreach (var call in calls)
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

        return Encoding.UTF8.GetBytes(root.ToJsonString(SerializerOptions));
    }

    /// <inheritdoc/>
    public IStreamTranslator CreateStreamTranslator()
    {
        return new ConstrainedToolCallStreamTranslator(plan: Plan, capabilityStore: _capabilityStore, logger: _logger);
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