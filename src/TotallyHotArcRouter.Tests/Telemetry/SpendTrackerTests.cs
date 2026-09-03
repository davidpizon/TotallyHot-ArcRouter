using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="SpendTracker"/>: the basic token/cost tracking parity pillar.</summary>
public class SpendTrackerTests
{
    private static SpendTracker CreateTracker(bool enabled = true) =>
        new(NullLogger<SpendTracker>.Instance, Options.Create(new SpendTrackingOptions { Enabled = enabled }));

    [Fact]
    public void GetSummary_NoRecordsYet_ReturnsZeroedSummary()
    {
        var tracker = CreateTracker();

        var summary = tracker.GetSummary();

        Assert.Equal(default(SpendSummary), summary);
    }

    [Fact]
    public async Task RecordAsync_AccumulatesRunningTotal_AcrossMultipleCalls()
    {
        var tracker = CreateTracker();

        await tracker.RecordAsync("claude-opus-4.6", 100, 20, 0.0015m, TestContext.Current.CancellationToken);
        var summary = await tracker.RecordAsync("gpt-5.4", 50, 10, 0.0006m, TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.RequestCount);
        Assert.Equal(150L, summary.TotalPromptTokens);
        Assert.Equal(30L, summary.TotalCompletionTokens);
        Assert.Equal(0.0021m, summary.TotalCostUsd);
        Assert.Equal(summary, tracker.GetSummary());
    }

    [Fact]
    public async Task RecordAsync_UnknownUsage_StillIncrementsRequestCount_ButNotCostOrTokens()
    {
        var tracker = CreateTracker();
        await tracker.RecordAsync("claude-opus-4.6", 100, 20, 0.0015m, TestContext.Current.CancellationToken);

        var summary = await tracker.RecordAsync("unsupported-provider-model", null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.RequestCount);
        Assert.Equal(100L, summary.TotalPromptTokens);
        Assert.Equal(20L, summary.TotalCompletionTokens);
        Assert.Equal(0.0015m, summary.TotalCostUsd);
        Assert.Equal(1, summary.UnpricedRequests);
    }

    [Fact]
    public async Task RecordAsync_NullCost_CountsAsUnpriced_NotAsZero()
    {
        var tracker = CreateTracker();

        var summary = await tracker.RecordAsync("unsupported-provider-model", 100, 20, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.RequestCount);
        Assert.Equal(1, summary.UnpricedRequests);
        Assert.Equal(0m, summary.TotalCostUsd);
    }

    [Fact]
    public async Task RecordAsync_KnownCost_DoesNotIncrementUnpricedRequests()
    {
        var tracker = CreateTracker();

        var summary = await tracker.RecordAsync("gpt-5.4", 10, 5, 0.0001m, TestContext.Current.CancellationToken);

        Assert.Equal(0, summary.UnpricedRequests);
    }

    [Fact]
    public async Task RecordAsync_Disabled_DoesNotIncrementTotals()
    {
        var tracker = CreateTracker(enabled: false);

        var summary = await tracker.RecordAsync("claude-opus-4.6", 100, 20, 0.0015m, TestContext.Current.CancellationToken);

        Assert.Equal(default(SpendSummary), summary);
        Assert.Equal(default(SpendSummary), tracker.GetSummary());
    }

    [Fact]
    public async Task RecordAsync_ConcurrentCalls_EveryCallIsCountedExactlyOnce()
    {
        var tracker = CreateTracker();

        var tasks = Enumerable.Range(0, 25)
            .Select(i => tracker.RecordAsync($"model-{i}", 1, 1, 0.000001m, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        Assert.Equal(25, tracker.GetSummary().RequestCount);
    }

    [Fact]
    public async Task NullSpendTracker_RecordAsync_ReturnsDefaultSummary_AndTakesNoAction()
    {
        var tracker = NullSpendTracker.Instance;

        var summary = await tracker.RecordAsync("claude-opus-4.6", 100, 20, 0.0015m, TestContext.Current.CancellationToken);

        Assert.Equal(default(SpendSummary), summary);
        Assert.Equal(default(SpendSummary), tracker.GetSummary());
    }
}
