namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Gives a model with no native tool calling a working one, by teaching it a syntax on the way out and
/// reading that syntax back on the way in (<c>docs/router/tool-call-normalization.md</c> Phase 5).
/// <para>
/// The request half is <see cref="ToolCallEmulationRewriter"/>. The response half is
/// <see cref="ToolCallNormalizingTranslator"/> - unchanged, and deliberately so: emulation teaches the
/// <see cref="ToolCallDialectRegistry.Emulated"/> dialect precisely because its framing is one the Phase 4
/// scanner already reads, so a taught reply and a native Hermes reply travel the identical code path.
/// Delegation rather than inheritance, so the response contract has exactly one implementation and this
/// type owns only what emulation adds.
/// </para>
/// <para>
/// <b>A taught reply is not evidence.</b> When a model emits <c>&lt;tool_call&gt;</c> because
/// TotallyHot.ArcRouter's own system prompt told it to, that says nothing about what the model does on its own -
/// and recording it as an observed <c>hermes</c> capability would be self-erasing: the higher-confidence
/// row would outrank the <c>emulated</c> one, emulation would stop, the instructions would vanish, and the
/// model would go back to emitting nothing. <see cref="ToolCallNormalizationPlan.IsEmulating"/> suppresses
/// exactly that write while still recording a native <c>tool_calls</c> response, which is real evidence
/// and the signal that this model should never have been emulated.
/// </para>
/// </summary>
internal sealed class ToolCallEmulatingTranslator : IClientPathTranslator
{
    private readonly ModelContextWindow? _contextWindow;
    private readonly ILogger _logger;
    private readonly ToolCallNormalizingTranslator _responseTranslator;

    /// <summary>Initializes a new instance of the <see cref="ToolCallEmulatingTranslator"/> class.</summary>
    /// <param name="plan">The emulated plan for this request - its candidates are the taught dialect alone.</param>
    /// <param name="dialect">The dialect being taught; must carry a <see cref="ToolCallDialect.EmulationPrompt"/>.</param>
    /// <param name="capabilityStore">
    /// The store a native-<c>tool_calls</c> observation is written back to;
    /// <see langword="null"/> disables write-back.
    /// </param>
    /// <param name="logger">Logger for the injection-budget and fail-open warnings.</param>
    /// <param name="contextWindowStore">
    /// The store to read this model's probed context window from, so the tool-schema injection budget can
    /// scale to it instead of the fixed fallback; <see langword="null"/> (or no window on record for this
    /// model) keeps the fallback. Read once here, not per-request, because the plan's (provider, model) pair
    /// - and so the window - is fixed for this translator's lifetime.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="dialect"/> has no emulation prompt or no delimiters, so it cannot
    /// be taught.
    /// </exception>
    public ToolCallEmulatingTranslator(
        ToolCallNormalizationPlan plan,
        ToolCallDialect dialect,
        IToolCallCapabilityStore? capabilityStore,
        ILogger logger,
        IModelContextWindowStore? contextWindowStore = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(logger);

        if (dialect.EmulationPrompt is null || dialect.Delimiters.Count == 0)
            // A dialect that can be recognized but not taught is a programming error at the call site, not
            // a runtime condition: it would produce a request stripped of its tools with nothing put back
            // in their place, which silently removes tool calling instead of emulating it.
            throw new ArgumentException(
                message:
                $"Dialect '{dialect.Name}' cannot be emulated: it has no emulation system prompt or no delimiters.",
                paramName: nameof(dialect));

        Plan = plan;
        Dialect = dialect;
        _responseTranslator =
            new ToolCallNormalizingTranslator(plan: plan, capabilityStore: capabilityStore, logger: logger);
        _logger = logger;
        _contextWindow =
            contextWindowStore?.GetModelContextWindow(providerKey: plan.ProviderKey, modelName: plan.ModelName);
    }

    /// <summary>Gets the plan this request's response is scanned under.</summary>
    /// <remarks>Exposed so <see cref="ToolCallNormalizerFactory"/>'s selection can be asserted without a live response.</remarks>
    public ToolCallNormalizationPlan Plan { get; }

    /// <summary>Gets the dialect being taught.</summary>
    public ToolCallDialect Dialect { get; }

    /// <inheritdoc/>
    /// <remarks>Never consulted for routing; emulation is selected per (provider, model), not per provider key.</remarks>
    public string Provider => "tool-call-emulator";

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
        return ToolCallEmulationRewriter.Rewrite(openAiShapedBody: openAiShapedBody, dialect: Dialect, logger: _logger,
            contextWindow: _contextWindow);
    }

    /// <inheritdoc/>
    public byte[] TranslateResponse(byte[] nativeShapedBody)
    {
        return _responseTranslator.TranslateResponse(nativeShapedBody);
    }

    /// <inheritdoc/>
    public IStreamTranslator CreateStreamTranslator()
    {
        return _responseTranslator.CreateStreamTranslator();
    }
}