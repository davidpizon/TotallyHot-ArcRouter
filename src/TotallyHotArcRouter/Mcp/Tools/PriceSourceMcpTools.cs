using System.ComponentModel;
using ModelContextProtocol.Server;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Mcp.Tools;

/// <summary>
/// MCP tools for managing the price catalog's source feeds (D6 per-source enable/disable, rank, and
/// on-demand ingestion) and looking up a model's catalog price. None of this carries credential material -
/// price sources are public feeds - so, unlike <see cref="ProviderMcpTools"/>, these call the underlying
/// stores directly rather than going through <see cref="TotallyHot.ArcRouter.Proxy.Management.ManagementFacade"/>.
/// </summary>
[McpServerToolType]
public sealed class PriceSourceMcpTools
{
    private readonly PriceCatalogIngestionService _ingestionService;
    private readonly IModelPriceLookup _priceLookup;
    private readonly PriceSourceToggleStore _toggleStore;

    /// <summary>Initializes a new instance of the <see cref="PriceSourceMcpTools"/> class.</summary>
    public PriceSourceMcpTools(
        PriceSourceToggleStore toggleStore,
        PriceCatalogIngestionService ingestionService,
        IModelPriceLookup priceLookup)
    {
        ArgumentNullException.ThrowIfNull(toggleStore);
        ArgumentNullException.ThrowIfNull(ingestionService);
        ArgumentNullException.ThrowIfNull(priceLookup);

        _toggleStore = toggleStore;
        _ingestionService = ingestionService;
        _priceLookup = priceLookup;
    }

    /// <summary>Lists every known price-source feed with its state, rank, and price-row count.</summary>
    [McpServerTool(Name = "list_price_sources")]
    [Description(
        "Lists every known price-source feed with its enabled state, priority rank, and how many price rows it owns.")]
    public IReadOnlyList<PriceSourceState> ListPriceSources()
    {
        return _toggleStore.List();
    }

    /// <summary>Enables or disables a price-source feed.</summary>
    [McpServerTool(Name = "set_price_source_enabled")]
    [Description("Enables or disables a price-source feed. Disabling cancels its in-flight fetch, if any.")]
    public object SetPriceSourceEnabled(
        [Description("The source's registry name.")]
        string sourceName,
        [Description("Whether the source should be polled and served.")]
        bool enabled)
    {
        var found = _toggleStore.SetEnabled(sourceName: sourceName, enabled: enabled);
        return found
            ? new { success = true }
            : new { error = $"Price source '{sourceName}' not found.", type = "NotFound" };
    }

    /// <summary>
    /// Rewrites every price source's priority rank from a full name order, then re-resolves every contested
    /// cell from prices already in storage under the new order - no live pull. Mirrors the Governance panel's
    /// drag-to-reorder so an MCP-driven reorder takes effect just as immediately.
    /// </summary>
    [McpServerTool(Name = "reorder_price_sources")]
    [Description(
        "Rewrites every price source's priority rank from the given name order (highest priority first). The name set must match every existing source exactly once.")]
    public async Task<object> ReorderPriceSourcesAsync(
        [Description("Every source's registry name, in the desired priority order.")]
        IReadOnlyList<string> namesInPriorityOrder,
        CancellationToken cancellationToken)
    {
        var applied = _toggleStore.Reorder(namesInPriorityOrder);
        if (!applied)
            return new
            {
                error = "The name set does not match every existing price source exactly once.", type = "InvalidRequest"
            };

        await _ingestionService.RecomputeWinnersAsync(cancellationToken).ConfigureAwait(false);
        return new { success = true };
    }

    /// <summary>Runs one price-catalog ingestion cycle now.</summary>
    [McpServerTool(Name = "refresh_price_sources")]
    [Description("Runs one price-catalog ingestion cycle now, fetching every enabled source.")]
    public Task<IngestionCycleSummary> RefreshPriceSourcesAsync(CancellationToken cancellationToken)
    {
        return _ingestionService.RunCycleAsync(cancellationToken);
    }

    /// <summary>Looks up a model's current per-token price from the catalog.</summary>
    [McpServerTool(Name = "get_model_price")]
    [Description(
        "Looks up a model's current per-token price from the catalog. Returns null when the catalog has no fresh price for it.")]
    public ModelPrice? GetModelPrice(
        [Description("The client-facing model name.")]
        string modelName,
        [Description("The provider key the model routes to.")]
        string provider)
    {
        return _priceLookup.TryGetPrice(new ModelKey(ModelName: modelName, Provider: provider));
    }
}