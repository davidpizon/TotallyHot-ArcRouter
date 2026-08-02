using System.ComponentModel;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry;
using ModelContextProtocol.Server;

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
    private readonly PriceSourceToggleStore _toggleStore;
    private readonly PriceCatalogIngestionService _ingestionService;
    private readonly IModelPriceLookup _priceLookup;

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
    [Description("Lists every known price-source feed with its enabled state, priority rank, and how many price rows it owns.")]
    public IReadOnlyList<PriceSourceState> ListPriceSources() => _toggleStore.List();

    /// <summary>Enables or disables a price-source feed.</summary>
    [McpServerTool(Name = "set_price_source_enabled")]
    [Description("Enables or disables a price-source feed. Disabling cancels its in-flight fetch, if any.")]
    public object SetPriceSourceEnabled(
        [Description("The source's registry name.")] string sourceName,
        [Description("Whether the source should be polled and served.")] bool enabled)
    {
        var found = _toggleStore.SetEnabled(sourceName, enabled);
        return found
            ? new { success = true }
            : new { error = $"Price source '{sourceName}' not found.", type = "NotFound" };
    }

    /// <summary>Rewrites every price source's priority rank from a full name order.</summary>
    [McpServerTool(Name = "reorder_price_sources")]
    [Description("Rewrites every price source's priority rank from the given name order (highest priority first). The name set must match every existing source exactly once.")]
    public object ReorderPriceSources(
        [Description("Every source's registry name, in the desired priority order.")] IReadOnlyList<string> namesInPriorityOrder)
    {
        var applied = _toggleStore.Reorder(namesInPriorityOrder);
        return applied
            ? new { success = true }
            : new { error = "The name set does not match every existing price source exactly once.", type = "InvalidRequest" };
    }

    /// <summary>Runs one price-catalog ingestion cycle now.</summary>
    [McpServerTool(Name = "refresh_price_sources")]
    [Description("Runs one price-catalog ingestion cycle now, fetching every enabled source.")]
    public Task<IngestionCycleSummary> RefreshPriceSourcesAsync(CancellationToken cancellationToken) =>
        _ingestionService.RunCycleAsync(cancellationToken);

    /// <summary>Looks up a model's current per-token price from the catalog.</summary>
    [McpServerTool(Name = "get_model_price")]
    [Description("Looks up a model's current per-token price from the catalog. Returns null when the catalog has no fresh price for it.")]
    public ModelPrice? GetModelPrice(
        [Description("The client-facing model name.")] string modelName,
        [Description("The provider key the model routes to.")] string provider) =>
        _priceLookup.TryGetPrice(new ModelKey(ModelName: modelName, Provider: provider));
}

