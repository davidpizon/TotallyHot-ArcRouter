using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Decides, per request, whether tool calls need translating at all - and if so, whether the model's own
/// syntax merely has to be *read* on the way back, or a syntax has to be *taught* on the way out
/// (<c>docs/router/tool-call-normalization.md</c> §3.4 and Phase 5). This is where the phase's
/// central correction lands: arming moves from the provider-wide
/// <see cref="Models.ProviderOptions.EnableToolCallGuard"/> flag to a per-(provider, model) capability
/// lookup, because a model's tool-call syntax comes from its chat template, not from the server hosting
/// it - one LM Studio process serves both a Qwen that needs rewriting and a natively tool-calling model
/// that must never be scanned.
///
/// <para>
/// Returning <see langword="null"/> is the common and desirable outcome: it restores true byte-for-byte
/// forwarding. Two of the four performance rules are enforced here rather than inside the scanner,
/// because the cheapest scan is the one never installed - rule 1 (no <c>tools</c> in the request means
/// nothing to normalize) and rule 2 (a model known to emit native <c>tool_calls</c> is never scanned).
/// </para>
/// </summary>
public sealed class ToolCallNormalizerFactory
{
    private readonly IToolCallCapabilityStore? _capabilityStore;
    private readonly ILogger<ToolCallNormalizerFactory> _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolCallNormalizerFactory"/> class.</summary>
    /// <param name="capabilityStore">
    /// The capability store to read classifications from and record observations to. When
    /// <see langword="null"/> (direct construction outside DI), every model reads as unclassified, so a
    /// tools-carrying request is scanned with the union of dialects and nothing is written back - the same
    /// behavior as a first request against a never-seen model, minus the persistence.
    /// </param>
    /// <param name="logger">Logger passed to each per-request translator, which uses it for the fail-open warnings.</param>
    public ToolCallNormalizerFactory(
        IToolCallCapabilityStore? capabilityStore = null,
        ILogger<ToolCallNormalizerFactory>? logger = null)
    {
        _capabilityStore = capabilityStore;
        _logger = logger ?? NullLogger<ToolCallNormalizerFactory>.Instance;
    }

    /// <summary>
    /// Builds this request's tool-call translator, or <see langword="null"/> when it should be forwarded
    /// untouched.
    /// </summary>
    /// <returns>
    /// A <see cref="ToolCallNormalizingTranslator"/> for a model that needs only its *response* rewritten,
    /// a <see cref="ToolCallEmulatingTranslator"/> for one that also needs its *request* rewritten
    /// (Phase 5), or <see langword="null"/> to forward byte-for-byte.
    /// <para>
    /// The two differ in more than degree, which is why the distinction is stated here rather than left to
    /// the concrete types: the emulating translator is an
    /// <see cref="IClientPathTranslator"/>, so <see cref="ProxyMiddleware"/> routes it through
    /// <see cref="IPayloadTranslator.TranslateRequest"/> and the upstream sees a body the client never
    /// sent. A caller that assumes everything from here is response-only would be wrong about what reaches
    /// the provider.
    /// </para>
    /// </returns>
    /// <param name="route">The resolved route about to be attempted.</param>
    /// <param name="requestCarriesTools">
    /// Whether the client's request body carried a non-empty <c>tools</c> array, as determined by
    /// <c>RequestInterceptor</c> from the <c>JsonObject</c> it had already parsed to rewrite <c>model</c>
    /// (performance rule 1 - the body is never parsed a second time for this).
    /// </param>
    /// <param name="requestCarriesToolHistory">
    /// Whether the conversation already contains tool-calling turns. Read from the same already-parsed
    /// body, and consulted only on the emulation path, where a follow-up turn needs its history re-rendered
    /// whether or not the client re-offered the tools - see <c>RouteCandidate.CarriesToolHistory</c>.
    /// </param>
    /// <param name="requestCarriesResponseFormat">
    /// Whether the client set its own <c>response_format</c>. Disables the constrained path only - that mode
    /// works by setting the field itself, and the client's value must win. Everything else is unaffected.
    /// </param>
    public IPayloadTranslator? TryCreate(
        ResolvedModelRoute route,
        bool requestCarriesTools,
        bool requestCarriesToolHistory = false,
        bool requestCarriesResponseFormat = false)
    {
        ArgumentNullException.ThrowIfNull(route);

        // Retained for one release as a forced-on override, and removed in Phase 6 in favor of the
        // per-model dialect display. An operator who set it did so because a model on that provider was
        // mangling tool calls, and honoring it here keeps that fix working through the transition even for
        // a request this factory would otherwise decline to arm.
        var forced = route.EnableToolCallGuard;

        var capability = _capabilityStore?.GetModelCapability(route.Provider, route.ModelName);

        // Constrained decoding is checked first, ahead of both emulation and rule 1, and the order is
        // load-bearing in two directions. It must precede the emulation branch because the constrained
        // dialect *also* carries an instruction prompt - it needs one, since a response_format schema never
        // enters the model's context - so the "has an EmulationPrompt" test below would otherwise claim it
        // and hand it to ToolCallEmulatingTranslator, which throws on a dialect with no delimiters. And it
        // must precede rule 1 for the same reason emulation does: a follow-up turn carrying tool history but
        // no re-offered tools still needs that history flattened.
        if (ShouldConstrain(route, capability, requestCarriesResponseFormat))
        {
            if (!requestCarriesTools && !requestCarriesToolHistory && !forced)
            {
                return null;
            }

            var constrainedPlan = new ToolCallNormalizationPlan(
                route.Provider,
                route.ModelName,
                [ToolCallDialectRegistry.Constrained],
                // Still observing: a native tool_calls response is the one piece of evidence a constrained
                // request cannot manufacture, and it is exactly the signal that this model never needed
                // constraining. IsEmulating suppresses the *dialect* write for the same reason it does under
                // emulation - the envelope came back because we demanded it.
                IsObserving: true,
                IsEmulating: true);

            return new ConstrainedToolCallTranslator(
                constrainedPlan, ToolCallDialectRegistry.Constrained, _capabilityStore, _logger);
        }

        // Emulation is checked before rule 1, because its gate is a different question. Rule 1 asks whether
        // there is anything to *scan for*; emulation also has to clean up a conversation that already
        // contains tool-calling turns, which is true of a follow-up request whether or not the client
        // re-offered the tools. Everything else below keeps rule 1 exactly as Phase 4 wrote it.
        //
        // IsScannable is required as well as a prompt, and it is a correctness guard rather than a tidy
        // extra condition. Teaching a dialect is only half of emulation: the reply still has to be *read
        // back*, and a dialect with no delimiters gives the scanner nothing to find. `Constrained` is
        // exactly that shape - it carries a prompt (a response_format schema never enters the model's
        // context, so the tools still have to be described) but declares no delimiters, because its reply
        // is a whole JSON envelope parsed strictly.
        //
        // Relying on branch order alone to keep it out of here was not enough. ShouldConstrain declines
        // when the client sent its own response_format, and that early return arrives *before* its
        // operator-pin check - so a model pinned to `constrained` whose request also carries a
        // response_format fell through to this branch, matched on the prompt, and threw ArgumentException
        // out of ToolCallEmulatingTranslator's constructor on a live request. Testing the property that
        // actually matters closes that path and every future one like it.
        if (capability is not null &&
            ToolCallDialectRegistry.TryGet(capability.Dialect, out var known) &&
            known.EmulationPrompt is not null &&
            known.IsScannable)
        {
            if (!requestCarriesTools && !requestCarriesToolHistory && !forced)
            {
                return null;
            }

            var emulatedPlan = new ToolCallNormalizationPlan(
                route.Provider,
                route.ModelName,
                [known],
                // Still observing: a native tool_calls response is the one piece of evidence an emulated
                // request cannot manufacture, and it is exactly the signal that this classification was
                // wrong. The recorder suppresses the *dialect* write instead - see IsEmulating.
                IsObserving: true,
                IsEmulating: true);

            return new ToolCallEmulatingTranslator(emulatedPlan, known, _capabilityStore, _logger);
        }

        if (!requestCarriesTools && !forced)
        {
            // Rule 1. The shipping guard scanned every response on a guarded route regardless of whether
            // tools were ever offered, which is most of them.
            return null;
        }

        IReadOnlyList<ToolCallDialect> candidates;

        if (capability is null)
        {
            // Tier 4: the model has never been classified, so this request is its own probe. It is
            // forwarded natively and unmodified - the union scan reads the answer off whatever comes back,
            // so the request still works and pays no probe latency.
            candidates = ToolCallDialectRegistry.ScannableDialects;
        }
        else if (!ToolCallDialectRegistry.TryGet(capability.Dialect, out var dialect))
        {
            // A row naming a dialect this build has never heard of was written by a newer build (or by
            // hand). Degrading to no scanning is the documented contract on ModelToolCapability.Dialect:
            // guessing would mean scanning with delimiters the newer build already decided were wrong.
            _logger.LogDebug(
                "Model {Provider}/{Model} is classified as dialect {Dialect}, which this build does not know; forwarding without normalization.",
                SanitizeForLog(route.Provider),
                SanitizeForLog(route.ModelName),
                SanitizeForLog(capability.Dialect));
            return null;
        }
        else if (!dialect.IsScannable)
        {
            // Rule 2. openai-native: the model already emits real tool_calls deltas, so there is nothing
            // to rewrite and scanning anyway is exactly what turns a capable model's prose *example* of a
            // tool call into a real invocation. The forced override still wins, since an operator who set
            // the flag is asserting they have seen this route misbehave.
            if (!forced)
            {
                return null;
            }

            candidates = ToolCallDialectRegistry.ScannableDialects;
        }
        else if (capability.Confidence >= DetectionConfidence.Observed)
        {
            // Settled: seen in a real response, or pinned by an operator. Arm only what it speaks.
            candidates = [dialect];
        }
        else
        {
            // Believed but unconfirmed - read off a chat template or guessed from the model id. Arming
            // only the believed dialect would make a wrong guess permanent, because the evidence that
            // would correct it is exactly what a single-dialect scan discards. So arm the union with the
            // believed dialect first, which is what "tier 4 confirms or corrects them" (§3.2) requires.
            candidates = [dialect, .. ToolCallDialectRegistry.ScannableDialects.Where(d => d != dialect)];
        }

        var plan = new ToolCallNormalizationPlan(
            route.Provider,
            route.ModelName,
            candidates,
            IsObserving: capability is null || capability.Confidence < DetectionConfidence.Observed);

        return new ToolCallNormalizingTranslator(plan, _capabilityStore, _logger);
    }

    /// <summary>
    /// Decides whether this request should be answered under constrained decoding rather than by scanning
    /// or teaching a text dialect.
    /// </summary>
    /// <remarks>
    /// The policy is "prefer it wherever the endpoint supports it", which makes it self-configuring: no
    /// per-model setup, and the fix reaches an operator who never learns the feature exists. The three
    /// conditions that hold it back are each a case where constraining would be wrong rather than merely
    /// unnecessary.
    /// <para>
    /// <b>The client's own <c>response_format</c> outranks everything, including an operator pin.</b> It is
    /// tested first and is the one condition a pin cannot override, because constrained mode works by
    /// setting that field: honoring the pin here would silently replace a structured-output contract the
    /// caller is about to parse against. A pinned model in that situation is forwarded unrewritten rather
    /// than emulated - <c>constrained</c> declares no delimiters, so there would be nothing to read its
    /// reply back with.
    /// </para>
    /// <para>
    /// <b>Below that, an operator pin wins outright</b> - decided by the pin alone and returned either way,
    /// so a human is never second-guessed by detection. That includes a human who pinned <c>constrained</c>
    /// on an endpoint whose support this build failed to detect, which is the escape hatch for the
    /// conservative inference behind
    /// <see cref="ProviderEndpointCapabilities.JsonSchemaResponseFormat"/>.
    /// </para>
    /// </remarks>
    private bool ShouldConstrain(
        ResolvedModelRoute route,
        ModelToolCapability? capability,
        bool requestCarriesResponseFormat)
    {
        // The client owns response_format. Constrained mode is the one path that would overwrite it.
        if (requestCarriesResponseFormat)
        {
            return false;
        }

        if (capability?.Confidence == DetectionConfidence.Operator)
        {
            return string.Equals(capability.Dialect, ToolCallDialectRegistry.Constrained.Name, StringComparison.OrdinalIgnoreCase);
        }

        // A model that already emits real tool_calls needs nothing done to it, and constraining it would
        // replace a working native path with a rewritten one - performance rule 2's concern, applied here.
        if (capability is not null &&
            ToolCallDialectRegistry.TryGet(capability.Dialect, out var known) &&
            known == ToolCallDialectRegistry.OpenAiNative)
        {
            return false;
        }

        return _capabilityStore?.GetProviderCapabilities(route.Provider)?.JsonSchemaResponseFormat == true;
    }

    /// <summary>
    /// Strips CR/LF from a config-controlled value before it enters a log template, so a crafted provider
    /// or model name cannot forge additional log lines (CodeQL: log forging, CWE-117). Chained Replace on
    /// the tainted value directly is the sanitizer shape the analysis recognizes - mirrors
    /// <c>ProxyMiddleware</c>'s own <c>SanitizeForLog</c>.
    /// </summary>
    private static string SanitizeForLog(string value) => value.Replace("\r", " ").Replace("\n", " ");
}

