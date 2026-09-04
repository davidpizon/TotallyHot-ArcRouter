using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Identifies which response-body shape a provider's captured telemetry bytes are parsed as, once
/// dispatch has been reduced to "which parser" (see <see cref="ProviderRegistration"/>'s remarks for why
/// this is the only axis <see cref="TotallyHot.ArcRouter.Telemetry.IUsageExtractor"/> and
/// <see cref="TotallyHot.ArcRouter.Telemetry.IResponseTextExtractor"/> actually vary on).
/// </summary>
public enum UsageParserShape
{
    /// <summary>
    /// OpenAI's own <c>choices[].message</c> + <c>usage.prompt_tokens</c>/<c>completion_tokens</c> shape.
    /// Every translated provider (Gemini, Ollama, and the three Bedrock-routed providers) ends up here:
    /// each one's translator converts the provider's native response into this shape before
    /// <c>ProxyMiddleware</c>/<c>BedrockInvocationHandler</c> ever captures bytes for telemetry, so the
    /// captured bytes are OpenAI-shaped regardless of which upstream actually served the request.
    /// </summary>
    OpenAiCompatible,

    /// <summary>
    /// Anthropic's own <c>content[]</c> + <c>usage.input_tokens</c>/<c>output_tokens</c> shape. Only
    /// native (non-Bedrock) Anthropic traffic reaches this shape today: native Anthropic is dual-mode
    /// (<see cref="TotallyHot.ArcRouter.Proxy.Translation.IPayloadTranslator.ShouldTranslate"/> lets real
    /// Claude Code traffic on <c>/v1/messages</c> pass through untranslated), so its own captured bytes can
    /// still be Anthropic-native rather than OpenAI-shaped. Bedrock-routed Anthropic does NOT use this
    /// shape - see <c>AnthropicOnBedrockPayloadTranslator.TranslateResponse</c>, which always converts to
    /// <see cref="OpenAiCompatible"/> before the bytes are captured, exactly like the other two Bedrock
    /// translators.
    /// </summary>
    Native
}

/// <summary>
/// One provider's telemetry-dispatch registration: which parser shape its captured response bytes are in,
/// and (optionally) which <see cref="IProviderCostReconciler"/> reconciles its billed spend against the
/// local ledger. Reading <c>UsageExtractor</c>/<c>ResponseTextExtractor</c> shows the two only ever vary on
/// one thing per provider - which parser to call - so this record carries exactly that axis rather than a
/// speculative set of fields nothing consumes yet; it deliberately mirrors the shape of the existing
/// <c>IReadOnlyDictionary&lt;string, IPayloadTranslator&gt;</c> built in
/// <c>ServiceCollectionExtensions.AddTelemetryAndTranslation</c> (keyed the same way, by each provider's own
/// key), rather than inventing a new lookup pattern.
/// </summary>
/// <param name="ProviderKey">
/// The provider key this registration answers for (e.g. <c>"anthropic"</c>, <c>"bedrock-titan"</c>),
/// matching the same string used to key the <c>IPayloadTranslator</c> dictionary and passed as the
/// <c>provider</c> argument to <see cref="TotallyHot.ArcRouter.Telemetry.IUsageExtractor.TryExtractUsage"/>
/// and <see cref="TotallyHot.ArcRouter.Telemetry.IResponseTextExtractor.TryExtractText"/>.
/// </param>
/// <param name="UsageParserShape">Which response-body shape this provider's captured telemetry bytes are parsed as.</param>
/// <param name="CostReconciler">
/// The provider's billing-reconciliation collaborator, or <see langword="null"/> when none is wired up yet.
/// Every provider registered by this task's <c>ServiceCollectionExtensions</c> change leaves this
/// <see langword="null"/> - Anthropic's and OpenAI's existing <c>IProviderCostReconciler</c> registrations
/// are untouched and keep working exactly as they did before this record existed; this field exists so a
/// future task can populate it per provider without another dispatch table appearing alongside this one.
/// </param>
public sealed record ProviderRegistration(
    string ProviderKey,
    UsageParserShape UsageParserShape,
    IProviderCostReconciler? CostReconciler);