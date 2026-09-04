namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Builds the canonical <c>provider key -&gt; <see cref="ProviderRegistration"/></c> table: the single
/// source of truth for which response-body shape each known provider's captured telemetry bytes are parsed
/// as. Used both by <c>ServiceCollectionExtensions.AddTelemetryAndTranslation</c> (to register the shared
/// DI singleton) and by <c>UsageExtractor</c>/<c>ResponseTextExtractor</c>'s own no-arg constructors (so a
/// caller that builds one of them directly - production fallback construction in
/// <c>ProxyMiddleware</c>, or a test - gets the same dispatch table DI would have supplied, rather than an
/// empty one that fails every lookup).
/// </summary>
internal static class ProviderRegistrations
{
    /// <summary>
    /// Builds a fresh copy of the default registration table. Every provider here leaves
    /// <see cref="ProviderRegistration.CostReconciler"/> <see langword="null"/> - Anthropic's and OpenAI's
    /// existing <c>IProviderCostReconciler</c> registrations are unrelated to this table and are left
    /// exactly as they were wired before it existed.
    /// </summary>
    public static IReadOnlyDictionary<string, ProviderRegistration> BuildDefault()
    {
        var registrations = new[]
        {
            // Native (non-Bedrock) Anthropic traffic is the one case that can still reach this extractor
            // as Anthropic's own content[]/usage shape: IPayloadTranslator.ShouldTranslate lets real
            // Claude Code traffic on /v1/messages pass through untranslated (docs/router/unified-api-
            // translation.md §4.4), so its own captured bytes stay native rather than becoming OpenAI-shaped.
            new ProviderRegistration(ProviderKey: "anthropic", UsageParserShape: UsageParserShape.Native, null),

            // OpenAI's own traffic needs no translation at all - the captured bytes are already its shape.
            new ProviderRegistration(ProviderKey: "openai", UsageParserShape: UsageParserShape.OpenAiCompatible, null),

            // Ollama's OpenAI-compatible routes answer in OpenAI's own shape with no translator in front of
            // them (docs/router/unified-api-translation.md §4.1, pinned by OllamaProviderTests).
            new ProviderRegistration(ProviderKey: "ollama", UsageParserShape: UsageParserShape.OpenAiCompatible, null),

            // Gemini always translates: GeminiPayloadTranslator converts its response to OpenAI's shape
            // before ProxyMiddleware ever captures it (docs/router/unified-api-translation.md §4.3).
            new ProviderRegistration(ProviderKey: "gemini", UsageParserShape: UsageParserShape.OpenAiCompatible, null),

            // The three Bedrock-routed providers (docs/router/unified-api-translation.md §4.2): each
            // translator's TranslateResponse always converts AWS's native response body into OpenAI's
            // shape before BedrockInvocationHandler captures it for telemetry - verified by reading
            // TitanPayloadTranslator/LlamaPayloadTranslator/AnthropicOnBedrockPayloadTranslator's own
            // TranslateResponse. "bedrock-anthropic" is deliberately OpenAiCompatible, not Native, despite
            // reusing AnthropicPayloadTranslator's response-conversion logic internally - that reuse is
            // what performs the native-to-OpenAI conversion, so the bytes this extractor ever sees for it
            // are OpenAI-shaped, never native Anthropic content[]/usage.
            new ProviderRegistration(ProviderKey: "bedrock-titan", UsageParserShape: UsageParserShape.OpenAiCompatible,
                null),
            new ProviderRegistration(ProviderKey: "bedrock-llama", UsageParserShape: UsageParserShape.OpenAiCompatible,
                null),
            new ProviderRegistration(ProviderKey: "bedrock-anthropic",
                UsageParserShape: UsageParserShape.OpenAiCompatible, null)
        };

        return registrations.ToDictionary(keySelector: r => r.ProviderKey, comparer: StringComparer.OrdinalIgnoreCase);
    }
}