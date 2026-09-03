using ModelContextProtocol.Server;
using System.ComponentModel;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Mcp.Tools;

/// <summary>
/// MCP tools for managing providers, model routes, and per-provider budgets. Every read and write goes
/// through <see cref="ManagementFacade"/> - the same facade the hardened REST <c>/admin/*</c> API calls -
/// so secrets are masked identically on both surfaces: a custom header's literal value comes back only
/// when the operator has left that header unlocked (see <see cref="HeaderView"/>).
/// </summary>
[McpServerToolType]
public sealed class ProviderMcpTools
{
    private readonly ManagementFacade _facade;

    /// <summary>Initializes a new instance of the <see cref="ProviderMcpTools"/> class.</summary>
    public ProviderMcpTools(ManagementFacade facade)
    {
        ArgumentNullException.ThrowIfNull(facade);
        _facade = facade;
    }

    /// <summary>Lists every configured provider, masked, with its models and budget.</summary>
    [McpServerTool(Name = "list_providers")]
    [Description("Lists every configured provider with its models, budget, and current-month spend. Credentials are masked: a custom header's value is returned only when that header is unlocked; a locked header reports its source alone.")]
    public ProvidersResponse ListProviders() => _facade.ListProviders();

    /// <summary>Adds or edits a provider.</summary>
    [McpServerTool(Name = "upsert_provider")]
    [Description("Adds a new provider or edits an existing one. Authentication is expressed as an ordinary entry in Headers (e.g. an 'Authorization' or 'x-api-key' header); a header's value is write-only and never echoed back by any tool. A blank literal header value preserves the currently stored value; set Locked to withhold it from future reads.")]
    public async Task<object> UpsertProviderAsync(
        [Description("The provider key, e.g. 'openai'.")] string key,
        [Description("The provider fields to write; non-credential fields fall back to the existing value when omitted.")] ProviderWriteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _facade.UpsertProviderAsync(key, request, cancellationToken).ConfigureAwait(false);
        return ToToolResult(result);
    }

    /// <summary>Removes a provider by key.</summary>
    [McpServerTool(Name = "remove_provider")]
    [Description("Removes a provider by key, along with every model route that points at it. Rejected if the provider is unknown. Historical spend and usage metrics are retained.")]
    public async Task<object> RemoveProviderAsync(
        [Description("The provider key to remove.")] string key,
        CancellationToken cancellationToken)
    {
        var result = await _facade.RemoveProviderAsync(key, cancellationToken).ConfigureAwait(false);
        return ToToolResult(result);
    }

    /// <summary>Adds or edits a model route under a provider.</summary>
    [McpServerTool(Name = "upsert_model")]
    [Description("Adds a model route under a provider, or edits an existing one.")]
    public async Task<object> UpsertModelAsync(
        [Description("The provider key the model routes to.")] string providerKey,
        [Description("The client-facing model name.")] string modelName,
        [Description("The upstream model identifier; defaults to modelName when blank.")] ModelWriteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _facade.UpsertModelAsync(providerKey, modelName, request, cancellationToken).ConfigureAwait(false);
        return ToToolResult(result);
    }

    /// <summary>Removes a model route by its client-facing name.</summary>
    [McpServerTool(Name = "remove_model")]
    [Description("Removes a model route by its client-facing name.")]
    public async Task<object> RemoveModelAsync(
        [Description("The client-facing model name to remove.")] string modelName,
        CancellationToken cancellationToken)
    {
        var result = await _facade.RemoveModelAsync(modelName, cancellationToken).ConfigureAwait(false);
        return ToToolResult(result);
    }

    /// <summary>Sets or clears a provider's monthly budget caps.</summary>
    [McpServerTool(Name = "set_provider_budget")]
    [Description("Sets or clears a provider's monthly USD and/or token budget cap. A null cap clears that dimension; both null removes the budget.")]
    public object SetProviderBudget(
        [Description("The provider key.")] string providerKey,
        [Description("The caps to write.")] ProviderBudgetWriteRequest request) =>
        ToToolResult(_facade.SetBudget(providerKey, request));

    /// <summary>Queries a provider's own OpenAI-shaped model list, when it supports one.</summary>
    [McpServerTool(Name = "discover_models")]
    [Description("Queries a provider's own OpenAI-shaped model list, when it supports one.")]
    public async Task<object> DiscoverModelsAsync(
        [Description("The provider key to query.")] string providerKey,
        CancellationToken cancellationToken)
    {
        var result = await _facade.DiscoverModelsAsync(providerKey, cancellationToken).ConfigureAwait(false);
        return ToToolResult(result);
    }

    /// <summary>Re-probes which API flavors a provider's endpoint answers, and persists the result.</summary>
    [McpServerTool(Name = "scan_provider_capabilities")]
    [Description("Probes which API flavors a provider's endpoint answers (OpenAI-compatible, LM Studio native, Ollama native, Anthropic-compatible) and records the result.")]
    public async Task<object> ScanProviderCapabilitiesAsync(
        [Description("The provider key to scan.")] string providerKey,
        CancellationToken cancellationToken)
    {
        var result = await _facade.ScanCapabilitiesAsync(providerKey, cancellationToken).ConfigureAwait(false);
        return ToToolResult(result);
    }

    /// <summary>Maps a facade <see cref="ManagementResult{T}"/> to either the success value or a small error-shaped object (<c>{ error, type }</c>).</summary>
    private static object ToToolResult<T>(ManagementResult<T> result) =>
        result.Success
            ? result.Value!
            : new { error = result.ErrorMessage, type = result.ErrorType.ToString() };
}

