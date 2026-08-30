namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// The read surface for probed per-model context windows, consulted when answering Ollama's
/// <c>POST /api/show</c> (<c>docs/router/ollama-show-capabilities-plan.md</c>).
///
/// <para>
/// Deliberately separate from <see cref="IToolCallCapabilityStore"/> even though
/// <see cref="ToolCallCapabilityStore"/> implements both. That interface is documented as the surface the
/// request path uses to decide how to normalize tool calls; a context window is not that, and the three
/// translators depending on it would otherwise gain a member none of them calls. Same class, narrower
/// contracts.
/// </para>
/// </summary>
public interface IModelContextWindowStore
{
    /// <summary>
    /// Gets the context window probed for <paramref name="modelName"/> on <paramref name="providerKey"/>,
    /// or <see langword="null"/> when none has been read.
    /// </summary>
    /// <remarks>
    /// Served from an in-memory snapshot, not a query: this is called from the proxy's <c>/api/show</c>
    /// handler, which answers a client model picker that polls. A <see langword="null"/> result means the
    /// window is genuinely unknown - because no scan has run, or because the provider is one that publishes
    /// no context length at all (every hosted OpenAI-shaped and Anthropic endpoint) - and the caller must
    /// omit <c>model_info</c> rather than substitute a default.
    /// </remarks>
    /// <param name="providerKey">The <c>ModelRouting:Providers</c> key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    ModelContextWindow? GetModelContextWindow(string providerKey, string modelName);
}
