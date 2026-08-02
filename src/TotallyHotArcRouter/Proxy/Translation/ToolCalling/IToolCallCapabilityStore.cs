namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// The read/record surface the request path uses to decide how - or whether - to normalize a model's
/// tool calls (<c>docs/router/tool-call-normalization.md</c> Phase 1).
///
/// <para>
/// Narrower than the concrete <see cref="ToolCallCapabilityStore"/>, which also owns cache lifecycle
/// (<c>Reload</c>) and change notification. The split mirrors <c>IBudgetEnforcer</c> over
/// <c>ProviderBudgetStore</c>: the hot path depends only on what it actually calls, so it cannot
/// accidentally trigger a reload mid-request.
/// </para>
/// </summary>
public interface IToolCallCapabilityStore
{
    /// <summary>
    /// Gets what is known about how <paramref name="modelName"/> on <paramref name="providerKey"/>
    /// expresses a tool call, or <see langword="null"/> when it has never been classified.
    /// </summary>
    /// <remarks>
    /// Served from an in-memory snapshot, not a query - Phase 4 calls this on every request that carries
    /// <c>tools</c>. A <see langword="null"/> result is the signal to forward natively while arming the
    /// union scanner, which is what makes the first live request its own probe.
    /// </remarks>
    ModelToolCapability? GetModelCapability(string providerKey, string modelName);

    /// <summary>
    /// Gets which API flavors <paramref name="providerKey"/>'s endpoint answers, or
    /// <see langword="null"/> when it has never been scanned.
    /// </summary>
    ProviderEndpointCapabilities? GetProviderCapabilities(string providerKey);

    /// <summary>
    /// Records a model's detected dialect, unless a higher-confidence classification already exists.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the classification was stored; <see langword="false"/> when it was
    /// rejected as outranked - notably, when an operator has pinned the dialect by hand.
    /// </returns>
    bool TryRecordModelCapability(ModelToolCapability capability);
}

