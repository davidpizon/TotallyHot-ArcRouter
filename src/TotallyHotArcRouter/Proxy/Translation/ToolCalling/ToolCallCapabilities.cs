namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Which API flavors one provider's endpoint actually answers, as discovered by probing well-known paths
/// (<c>docs/router/tool-call-normalization.md</c> §3.3).
///
/// <para>
/// Routing speaks OpenAI-compatible only, today and for the foreseeable phases. The other three flags are
/// recorded because a native endpoint exposes model metadata the OpenAI-shaped one does not - Ollama's
/// <c>/api/show</c> returns the literal chat template, which is the cheapest and most reliable source of a
/// model's tool-call dialect. So this scan pays for itself immediately via detection, rather than being
/// speculative groundwork for a future routing change.
/// </para>
/// </summary>
/// <param name="ProviderKey">The <c>ModelRouting:Providers</c> key this describes.</param>
/// <param name="OpenAiCompatible">Whether <c>GET {base}/v1/models</c> answered - the only flavor routing uses.</param>
/// <param name="LmStudioNative">Whether LM Studio's native <c>GET {base}/api/v0/models</c> answered.</param>
/// <param name="OllamaNative">Whether Ollama's native <c>GET {base}/api/tags</c> answered, implying <c>/api/show</c> is available for template reads.</param>
/// <param name="AnthropicCompatible">
/// Whether the endpoint answered an Anthropic-shaped probe: primarily <c>GET {base}/v1/models</c>'s
/// pagination markers, and additionally a <c>POST {base}/v1/messages</c> probe when
/// <see cref="TotallyHot.ArcRouter.Models.ProviderOptions.ProbeAnthropicMessages"/> opts in and the model
/// list alone did not already say yes - the case of a local runtime that lists models in plain OpenAI
/// shape but separately answers the Anthropic dialect too.
/// </param>
/// <param name="JsonSchemaResponseFormat">
/// Whether this endpoint honors <c>response_format: {"type": "json_schema", ...}</c> by actually
/// constraining generation, which is what makes
/// <see cref="ToolCallDialectRegistry.Constrained"/> usable for its models.
///
/// <para>
/// <b>Inferred from the native flavors above, not from a live POST probe</b>, and the distinction is
/// deliberate. Schema support is not discoverable from any GET, so a real probe would have to POST a
/// completion - which on a local server means JIT-loading whatever model it names, turning a cheap
/// capability scan into a multi-second, multi-gigabyte side effect on the "Refresh from endpoint" button.
/// LM Studio (llama.cpp GBNF grammar sampling) and Ollama both back this field for every model they
/// serve, so their flavor flags answer the question without asking it.
/// </para>
///
/// <para>
/// The inference is narrow on purpose: a plain OpenAI-compatible endpoint does <em>not</em> set this,
/// even though most such servers do support the field, because that flag is true for every cloud
/// provider and a false positive there would attach a schema to a request the upstream might reject
/// outright. Constrained mode is for local models whose free-text tool calls are unreliable; those are
/// exactly the two flavors named here. An operator whose server supports it anyway can say so through
/// the capability override rather than being second-guessed by a heuristic.
/// </para>
/// </param>
/// <param name="ScannedAtUtc">When the scan ran, so a stale result can be refreshed.</param>
/// <param name="ScanError">
/// Why the scan could not complete, or <see langword="null"/> when it did. A provider that is simply
/// unreachable records all-false plus this message rather than throwing - the scan is best-effort and must
/// never fail a provider save.
/// </param>
public sealed record ProviderEndpointCapabilities(
    string ProviderKey,
    bool OpenAiCompatible,
    bool LmStudioNative,
    bool OllamaNative,
    bool AnthropicCompatible,
    bool JsonSchemaResponseFormat,
    DateTimeOffset ScannedAtUtc,
    string? ScanError = null);

/// <summary>
/// How one model, as served by one provider, expresses its intent to invoke a tool - the row the
/// normalizing translator consults to decide which dialect to arm, or whether to arm at all.
///
/// <para>
/// Keyed on (provider, model) rather than model alone because the dialect comes from the loaded model's
/// chat template, not the server: one LM Studio process can serve a Qwen that needs the Hermes dialect and
/// a natively tool-calling model that must never be scanned.
/// </para>
/// </summary>
/// <param name="ProviderKey">The <c>ModelRouting:Providers</c> key.</param>
/// <param name="ModelName">The client-facing <c>ModelRouting:ModelList[].ModelName</c>.</param>
/// <param name="Dialect">
/// The <see cref="ToolCallDialect.Name"/> detected. Stored as the string rather than the resolved dialect
/// so a row written by a newer build - naming a dialect this one has never heard of - reads back as
/// "unknown" and degrades to no scanning, instead of failing to load.
/// </param>
/// <param name="Confidence">How this was learned; gates whether a later write may overwrite it.</param>
/// <param name="Evidence">
/// A short, human-readable note on what produced this classification - the delimiter that matched, or the
/// template pattern it was read from. <b>Never raw model output.</b> A matched tool call carries the user's
/// own arguments, so persisting the snippet itself would put request content in a database that has no
/// retention policy and is not treated as sensitive. <see cref="ToolCallCapabilityStore"/> truncates this
/// defensively, but callers are responsible for passing a description rather than a payload.
/// </param>
/// <param name="ObservationCount">
/// How many live responses have been classified for this model. Incremented by SQL on an
/// <see cref="DetectionConfidence.Observed"/> write, never by the caller - see
/// <see cref="ToolCallCapabilityRepository.TryUpsertModelCapability"/> for why.
/// <para>
/// The plan originally sketched this as a counter of *unmatched* responses, to condemn a model to
/// emulation after several. Phase 4 rejected that reading and Phase 5 did not restore it: an unmatched
/// response is recorded as nothing at all, because "chose not to call a tool" and "cannot call tools"
/// produce identical evidence at this layer. Emulation is selected from the model's chat template instead.
/// What this number actually counts is confirmations, and today nothing reads it - it exists so a future
/// diagnostic can show how settled a classification is.
/// </para>
/// </param>
/// <param name="DetectedAtUtc">When this classification was last written.</param>
public sealed record ModelToolCapability(
    string ProviderKey,
    string ModelName,
    string Dialect,
    DetectionConfidence Confidence,
    string? Evidence = null,
    int ObservationCount = 0,
    DateTimeOffset DetectedAtUtc = default);

