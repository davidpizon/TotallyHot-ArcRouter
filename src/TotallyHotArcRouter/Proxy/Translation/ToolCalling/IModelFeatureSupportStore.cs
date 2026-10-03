namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// The read surface for per-model capability records reported by a provider's model list
/// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1), consulted when building
/// each failover candidate's request body.
/// <para>
/// Separate from <see cref="IToolCallCapabilityStore"/> and <see cref="IModelContextWindowStore"/> for the same
/// reason those two are separate from each other: <see cref="ToolCallCapabilityStore"/> implements all three, but
/// each caller depends only on what it reads, and the request path cannot reach the management-only write.
/// </para>
/// </summary>
public interface IModelFeatureSupportStore
{
    /// <summary>
    /// Gets the capability record <paramref name="providerKey"/>'s last scan reported for
    /// <paramref name="modelId"/>, or <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// Served from an in-memory snapshot, not a query: this is called for every router-chosen candidate on
    /// <c>/v1/messages</c>. A <see langword="null"/> result means unknown - no scan has run, the endpoint's list is
    /// not Anthropic-shaped, or the list reported no <c>capabilities</c> for the model - and the caller must send
    /// the request as received.
    /// </remarks>
    /// <param name="providerKey">The <c>ModelRouting:Providers</c> key.</param>
    /// <param name="modelId">The upstream model id (<c>ProviderModelId</c>), not the client-facing name.</param>
    ModelFeatureSupport? GetModelFeatureSupport(string providerKey, string modelId);
}
