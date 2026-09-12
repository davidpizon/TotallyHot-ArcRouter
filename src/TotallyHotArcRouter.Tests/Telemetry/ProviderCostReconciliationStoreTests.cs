using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="ProviderCostReconciliationStore"/>.</summary>
public class ProviderCostReconciliationStoreTests
{
    [Fact]
    public void GetLastReconciledDay_NoCheckpointYet_ReturnsNull()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateCostReconciliationStore();

        Assert.Null(store.GetLastReconciledDay("openai"));
    }

    [Fact]
    public void SetLastReconciledDay_ThenGet_RoundTrips()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateCostReconciliationStore();
        var day = new DateOnly(2026, 1, 15);

        store.SetLastReconciledDay(provider: "openai", day: day);

        Assert.Equal(expected: day, actual: store.GetLastReconciledDay("openai"));
    }

    [Fact]
    public void SetLastReconciledDay_CalledTwice_OverwritesRatherThanDuplicating()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateCostReconciliationStore();

        store.SetLastReconciledDay(provider: "openai", day: new DateOnly(2026, 1, 15));
        store.SetLastReconciledDay(provider: "openai", day: new DateOnly(2026, 1, 16));

        Assert.Equal(expected: new DateOnly(2026, 1, 16), actual: store.GetLastReconciledDay("openai"));
    }

    [Fact]
    public void SetLastReconciledDay_DifferentProviders_TrackedIndependently()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateCostReconciliationStore();

        store.SetLastReconciledDay(provider: "openai", day: new DateOnly(2026, 1, 15));
        store.SetLastReconciledDay(provider: "anthropic", day: new DateOnly(2026, 1, 10));

        Assert.Equal(expected: new DateOnly(2026, 1, 15), actual: store.GetLastReconciledDay("openai"));
        Assert.Equal(expected: new DateOnly(2026, 1, 10), actual: store.GetLastReconciledDay("anthropic"));
    }

    [Fact]
    public void InsertReconciliation_MultipleRows_AllPersisted()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateCostReconciliationStore();
        var windowStart = new DateTimeOffset(2026, 1, 15, 0, 0, 0, offset: TimeSpan.Zero);

        store.InsertReconciliation(new ProviderCostReconciliationEntry(
            Provider: "openai", WindowStartUtc: windowStart, WindowEndUtc: windowStart.AddDays(1), 10.50m, 10.00m,
            ScopeNote: "scope note", FetchedAtUtc: DateTimeOffset.UtcNow));
        store.InsertReconciliation(new ProviderCostReconciliationEntry(
            Provider: "openai", WindowStartUtc: windowStart.AddDays(1), WindowEndUtc: windowStart.AddDays(2), 5.00m,
            5.00m, ScopeNote: "scope note", FetchedAtUtc: DateTimeOffset.UtcNow));

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_cost_reconciliation WHERE provider = 'openai';";
        Assert.Equal(2L, actual: (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void GetLatestReconciliation_NoSnapshotYet_ReturnsNull()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateCostReconciliationStore();

        Assert.Null(store.GetLatestReconciliation("openai"));
    }

    [Fact]
    public void GetLatestReconciliation_MultipleSnapshots_ReturnsTheMostRecentWindow()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateCostReconciliationStore();
        var windowStart = new DateTimeOffset(2026, 1, 15, 0, 0, 0, offset: TimeSpan.Zero);

        store.InsertReconciliation(new ProviderCostReconciliationEntry(
            Provider: "openai", WindowStartUtc: windowStart, WindowEndUtc: windowStart.AddDays(1), 10.50m, 10.00m,
            ScopeNote: "older", FetchedAtUtc: DateTimeOffset.UtcNow));
        store.InsertReconciliation(new ProviderCostReconciliationEntry(
            Provider: "openai", WindowStartUtc: windowStart.AddDays(1), WindowEndUtc: windowStart.AddDays(2), 5.25m,
            5.00m, ScopeNote: "newer", FetchedAtUtc: DateTimeOffset.UtcNow));

        var latest = store.GetLatestReconciliation("openai");

        Assert.NotNull(latest);
        Assert.Equal(expected: windowStart.AddDays(1), actual: latest.WindowStartUtc);
        Assert.Equal(expected: 5.25m, actual: latest.ProviderReportedCostUsd);
        Assert.Equal(expected: 5.00m, actual: latest.LocalEstimatedCostUsd);
        Assert.Equal(expected: "newer", actual: latest.ScopeNote);
    }

    [Fact]
    public void GetLatestReconciliation_DifferentProviders_TrackedIndependently()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateCostReconciliationStore();
        var windowStart = new DateTimeOffset(2026, 1, 15, 0, 0, 0, offset: TimeSpan.Zero);

        store.InsertReconciliation(new ProviderCostReconciliationEntry(
            Provider: "openai", WindowStartUtc: windowStart, WindowEndUtc: windowStart.AddDays(1), 1m, 1m,
            ScopeNote: "openai", FetchedAtUtc: DateTimeOffset.UtcNow));
        store.InsertReconciliation(new ProviderCostReconciliationEntry(
            Provider: "anthropic", WindowStartUtc: windowStart, WindowEndUtc: windowStart.AddDays(1), 2m, 2m,
            ScopeNote: "anthropic", FetchedAtUtc: DateTimeOffset.UtcNow));

        Assert.Equal(expected: "openai", actual: store.GetLatestReconciliation("openai")?.ScopeNote);
        Assert.Equal(expected: "anthropic", actual: store.GetLatestReconciliation("anthropic")?.ScopeNote);
    }
}