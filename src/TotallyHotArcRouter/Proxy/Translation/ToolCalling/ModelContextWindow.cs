namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// How many tokens of context one model, as served by one provider, will actually accept - read from the
/// provider's own metadata during an endpoint scan and reported back to Ollama-shaped clients through
/// <c>POST /api/show</c>'s <c>model_info</c>
/// (<c>docs/router/ollama-show-capabilities-plan.md</c>).
/// <para>
/// Keyed on (provider, model) for the same reason <see cref="ModelToolCapability"/> is: the window belongs
/// to the loaded model rather than to the server, so one LM Studio process can serve a 32k model and a
/// 128k one at once. It is deliberately <em>not</em> a field on that record - the two share a key and a
/// probe but not a write lifecycle, and the request path rewrites tool-call rows without knowing anything
/// about a context window. See <c>docs/adr/0002-store-probed-model-context-windows-in-their-own-table.md</c>
/// for the two corruption paths that forced the split.
/// </para>
/// </summary>
/// <param name="ProviderKey">The <c>ModelRouting:Providers</c> key.</param>
/// <param name="ModelName">The client-facing <c>ModelRouting:ModelList[].ModelName</c>.</param>
/// <param name="ContextLength">
/// The window in tokens, always positive. Non-nullable on purpose: this record exists only when a probe
/// actually read a value, so "unknown" is the absence of the row rather than a sentinel inside it. That is
/// what lets <c>POST /api/show</c> omit <c>model_info</c> entirely rather than fabricate a default, and it
/// removes any need for callers to distinguish zero from unset.
/// </param>
/// <param name="Architecture">
/// The model architecture the provider reported (<c>qwen2</c>, <c>llama</c>, ...), or
/// <see langword="null"/> when it named none. Carried because Ollama's <c>model_info</c> keys the window by
/// architecture (<c>{arch}.context_length</c>) and clients resolve it by indirection through
/// <c>general.architecture</c>, so reporting the length without the architecture would break the standard
/// read path. Costs nothing to capture: both probes already parse the document that carries it.
/// </param>
/// <param name="Evidence">
/// A short, human-readable note on which probe produced this. Unlike
/// <see cref="ModelToolCapability.Evidence"/> this can never contain request content - a context length is
/// server metadata, not model output - but it is kept to a description for consistency with that contract.
/// </param>
/// <param name="DetectedAtUtc">When this window was last read from the provider.</param>
public sealed record ModelContextWindow(
    string ProviderKey,
    string ModelName,
    int ContextLength,
    string? Architecture = null,
    string? Evidence = null,
    DateTimeOffset DetectedAtUtc = default);