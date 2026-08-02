using TotallyHot.ArcRouter.Mcp.Tools;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Mcp.Tools;

/// <summary>
/// Covers <see cref="TelemetryMcpTools"/>: read-only delegation to the budget store and spend tracker.
/// There is deliberately no tool here for raw telemetry/prompt text - only these two aggregate reads.
/// </summary>
public sealed class TelemetryMcpToolsTests
{
    [Fact]
    public void GetBudgetStatus_UnbudgetedProvider_ReturnsAllZeroNoCapState()
    {
        using var temp = new TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        var tools = new TelemetryMcpTools(budgetStore, Mock.Of<ISpendTracker>());

        var status = tools.GetBudgetStatus("unbudgeted-provider");

        Assert.Null(status.DollarCap);
        Assert.Null(status.TokenCap);
        Assert.Equal(0m, status.DollarSpent);
    }

    [Fact]
    public void GetBudgetStatus_BudgetedProvider_ReflectsPersistedCaps()
    {
        using var temp = new TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        budgetStore.SetBudget("openai", 100m, 1_000L);
        var tools = new TelemetryMcpTools(budgetStore, Mock.Of<ISpendTracker>());

        var status = tools.GetBudgetStatus("openai");

        Assert.Equal(100m, status.DollarCap);
        Assert.Equal(1_000L, status.TokenCap);
    }

    [Fact]
    public void GetSpendSummary_DelegatesToSpendTracker()
    {
        using var temp = new TempDatabase();
        var expected = new SpendSummary(RequestCount: 3, TotalPromptTokens: 100, TotalCompletionTokens: 50, TotalCostUsd: 1.23m);
        var spendTracker = new Mock<ISpendTracker>();
        spendTracker.Setup(t => t.GetSummary()).Returns(expected);
        var tools = new TelemetryMcpTools(temp.CreateBudgetStore(), spendTracker.Object);

        var summary = tools.GetSpendSummary();

        Assert.Equal(expected, summary);
    }
}

