using System.ComponentModel;
using ModelContextProtocol.Server;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Mcp.Tools;

/// <summary>
/// MCP tools for reading spend and budget aggregates. Deliberately read-only and aggregate-only: there is
/// no tool that streams raw routing telemetry or request/response text, since that traffic can carry a
/// user-pasted secret (see docs/router/signalr-hub-security.md's remarks on the unauthenticated telemetry
/// stream) - a running total and a provider's budget status are useful to an agent managing the router
/// without that risk.
/// </summary>
[McpServerToolType]
public sealed class TelemetryMcpTools
{
    private readonly ProviderBudgetStore _budgetStore;
    private readonly ISpendTracker _spendTracker;

    /// <summary>Initializes a new instance of the <see cref="TelemetryMcpTools"/> class.</summary>
    public TelemetryMcpTools(ProviderBudgetStore budgetStore, ISpendTracker spendTracker)
    {
        ArgumentNullException.ThrowIfNull(budgetStore);
        ArgumentNullException.ThrowIfNull(spendTracker);

        _budgetStore = budgetStore;
        _spendTracker = spendTracker;
    }

    /// <summary>Gets a provider's current-month budget caps, spend, and breach state.</summary>
    [McpServerTool(Name = "get_budget_status")]
    [Description(
        "Gets a provider's current-month budget caps, spend, and whether it has breached a cap. An unbudgeted or unknown provider reports an all-zero, no-cap state.")]
    public ProviderBudgetState GetBudgetStatus(
        [Description("The provider key.")] string providerKey)
    {
        return _budgetStore.GetStatus(providerKey);
    }

    /// <summary>Gets the router's running spend/usage totals since process start.</summary>
    [McpServerTool(Name = "get_spend_summary")]
    [Description("Gets the router's running request count, token totals, and estimated USD cost since process start.")]
    public SpendSummary GetSpendSummary()
    {
        return _spendTracker.GetSummary();
    }
}