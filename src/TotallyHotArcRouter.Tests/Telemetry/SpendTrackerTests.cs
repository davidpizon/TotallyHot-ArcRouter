using System.Text.Json;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="SpendTracker"/>: the basic token/cost tracking parity pillar.</summary>
public class SpendTrackerTests : IDisposable
{
    private readonly string _logFilePath = Path.Combine(Path.GetTempPath(), $"spend-tracker-tests-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_logFilePath))
        {
            File.Delete(_logFilePath);
        }
    }

    private SpendTracker CreateTracker(bool enabled = true) =>
        new(NullLogger<SpendTracker>.Instance, Options.Create(new SpendTrackingOptions { Enabled = enabled, LogPath = _logFilePath }));

    [Fact]
    public void GetSummary_NoRecordsYet_ReturnsZeroedSummary()
    {
        using var tracker = CreateTracker();

        var summary = tracker.GetSummary();

        Assert.Equal(default(SpendSummary), summary);
    }

    [Fact]
    public async Task RecordAsync_AccumulatesRunningTotal_AcrossMultipleCalls()
    {
        using var tracker = CreateTracker();

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
        using var tracker = CreateTracker();
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
        using var tracker = CreateTracker();

        var summary = await tracker.RecordAsync("unsupported-provider-model", 100, 20, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.RequestCount);
        Assert.Equal(1, summary.UnpricedRequests);
        Assert.Equal(0m, summary.TotalCostUsd);
    }

    [Fact]
    public async Task RecordAsync_KnownCost_DoesNotIncrementUnpricedRequests()
    {
        using var tracker = CreateTracker();

        var summary = await tracker.RecordAsync("gpt-5.4", 10, 5, 0.0001m, TestContext.Current.CancellationToken);

        Assert.Equal(0, summary.UnpricedRequests);
    }

    [Fact]
    public async Task RecordAsync_Disabled_DoesNotIncrementTotals_AndDoesNotWriteFile()
    {
        using var tracker = CreateTracker(enabled: false);

        var summary = await tracker.RecordAsync("claude-opus-4.6", 100, 20, 0.0015m, TestContext.Current.CancellationToken);

        Assert.Equal(default(SpendSummary), summary);
        Assert.Equal(default(SpendSummary), tracker.GetSummary());
        Assert.False(File.Exists(_logFilePath));
    }

    [Fact]
    public async Task RecordAsync_AppendsOneJsonLineEntry_WithExpectedFields()
    {
        using var tracker = CreateTracker();

        await tracker.RecordAsync("claude-opus-4.6", 100, 20, 0.0015m, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_logFilePath));
        var lines = await File.ReadAllLinesAsync(_logFilePath, TestContext.Current.CancellationToken);
        var entry = Assert.Single(lines);

        using var json = JsonDocument.Parse(entry);
        var root = json.RootElement;
        Assert.Equal("claude-opus-4.6", root.GetProperty("Model").GetString());
        Assert.Equal(100, root.GetProperty("PromptTokens").GetInt32());
        Assert.Equal(20, root.GetProperty("CompletionTokens").GetInt32());
        Assert.Equal(0.0015m, root.GetProperty("CostUsd").GetDecimal());
        Assert.Equal(0.0015m, root.GetProperty("RunningTotalCostUsd").GetDecimal());
        Assert.Equal(1, root.GetProperty("RequestCount").GetInt32());
    }

    [Fact]
    public async Task RecordAsync_MultipleCalls_AppendsOneLinePerCall_AllParseable()
    {
        using var tracker = CreateTracker();

        for (var i = 0; i < 5; i++)
        {
            await tracker.RecordAsync("gpt-5.4", 10, 5, 0.0001m, TestContext.Current.CancellationToken);
        }

        var lines = await File.ReadAllLinesAsync(_logFilePath, TestContext.Current.CancellationToken);
        Assert.Equal(5, lines.Length);
        Assert.All(lines, line => JsonDocument.Parse(line).Dispose());
    }

    [Fact]
    public async Task RecordAsync_ConcurrentCalls_EveryLineIsWrittenAndParseable_NoInterleavedCorruption()
    {
        using var tracker = CreateTracker();

        var tasks = Enumerable.Range(0, 25)
            .Select(i => tracker.RecordAsync($"model-{i}", 1, 1, 0.000001m, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        Assert.Equal(25, tracker.GetSummary().RequestCount);

        var lines = await File.ReadAllLinesAsync(_logFilePath, TestContext.Current.CancellationToken);
        Assert.Equal(25, lines.Length);
        Assert.All(lines, line => JsonDocument.Parse(line).Dispose());
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

