using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="ReportedUsageRepository.UpsertReportedUsage"/>/
/// <see cref="ReportedUsageRepository.GetReportedUsage"/> (docs/router/secrets-at-rest-plan.md §8.1).
/// </summary>
public class ReportedUsageRepositoryTests
{
    private static DateOnly Yesterday => DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));

    [Fact]
    public void GetReportedUsage_NothingStored_ReturnsEmptyAndNullFetchedAt()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);

        var (rows, fetchedAt) = repository.GetReportedUsage("anthropic");

        Assert.Empty(rows);
        Assert.Null(fetchedAt);
    }

    [Fact]
    public void UpsertReportedUsage_RoundTrips_AndReportsTheLatestFetchedAt()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var fetchedAt = DateTimeOffset.UtcNow;

        repository.UpsertReportedUsage(
            providerKey: "anthropic",
            rows:
            [
                new ReportedUsageRow(UsageDay: Yesterday, Model: "claude-opus-4-1", 100, 50, 5, 10),
                new ReportedUsageRow(UsageDay: Yesterday, Model: "claude-sonnet-5", 200, 80, 0, 0)
            ],
            fetchedAtUtc: fetchedAt);

        var (rows, latestFetchedAt) = repository.GetReportedUsage("anthropic");

        Assert.Equal(2, actual: rows.Count);
        Assert.Equal(expected: fetchedAt, actual: latestFetchedAt);
        var opus = Assert.Single(collection: rows, predicate: r => r.Model == "claude-opus-4-1");
        Assert.Equal(100, actual: opus.InputTokens);
        Assert.Equal(50, actual: opus.OutputTokens);
        Assert.Equal(5, actual: opus.CacheCreationTokens);
        Assert.Equal(10, actual: opus.CacheReadTokens);
    }

    [Fact]
    public void UpsertReportedUsage_SameDayAndModel_ReplacesInPlace()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var day = Yesterday;

        repository.UpsertReportedUsage(providerKey: "anthropic",
            rows: [new ReportedUsageRow(UsageDay: day, Model: "claude-opus-4-1", 100, 50, 0, 0)],
            fetchedAtUtc: DateTimeOffset.UtcNow);
        repository.UpsertReportedUsage(providerKey: "anthropic",
            rows: [new ReportedUsageRow(UsageDay: day, Model: "claude-opus-4-1", 150, 75, 0, 0)],
            fetchedAtUtc: DateTimeOffset.UtcNow);

        var (rows, _) = repository.GetReportedUsage("anthropic");

        var row = Assert.Single(rows);
        Assert.Equal(150, actual: row.InputTokens);
        Assert.Equal(75, actual: row.OutputTokens);
    }

    [Fact]
    public void UpsertReportedUsage_DifferentProviders_CoexistIndependently()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var day = Yesterday;

        repository.UpsertReportedUsage(providerKey: "anthropic",
            rows: [new ReportedUsageRow(UsageDay: day, Model: "claude-opus-4-1", 1, 1, 0, 0)],
            fetchedAtUtc: DateTimeOffset.UtcNow);
        repository.UpsertReportedUsage(providerKey: "openai",
            rows: [new ReportedUsageRow(UsageDay: day, Model: "gpt-5.4", 2, 2, 0, 0)],
            fetchedAtUtc: DateTimeOffset.UtcNow);

        Assert.Single(repository.GetReportedUsage("anthropic").Rows);
        Assert.Single(repository.GetReportedUsage("openai").Rows);
    }

    [Fact]
    public void UpsertReportedUsage_EmptyRows_IsANoOp_AndDoesNotClearExistingSnapshot()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var day = Yesterday;
        repository.UpsertReportedUsage(providerKey: "anthropic",
            rows: [new ReportedUsageRow(UsageDay: day, Model: "claude-opus-4-1", 1, 1, 0, 0)],
            fetchedAtUtc: DateTimeOffset.UtcNow);

        repository.UpsertReportedUsage(providerKey: "anthropic", rows: [], fetchedAtUtc: DateTimeOffset.UtcNow);

        Assert.Single(repository.GetReportedUsage("anthropic").Rows);
    }

    [Fact]
    public void UpsertReportedUsage_PrunesRowsOlderThanRetention()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var oldDay = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-100));
        var oldFetchedAt = DateTimeOffset.UtcNow.AddDays(-100);
        repository.UpsertReportedUsage(providerKey: "anthropic",
            rows: [new ReportedUsageRow(UsageDay: oldDay, Model: "claude-opus-4-1", 1, 1, 0, 0)],
            fetchedAtUtc: oldFetchedAt);

        // A later cycle re-fetches the trailing window, which no longer includes oldDay - the retention
        // sweep should drop it rather than let it linger forever.
        repository.UpsertReportedUsage(providerKey: "anthropic",
            rows: [new ReportedUsageRow(UsageDay: Yesterday, Model: "claude-opus-4-1", 1, 1, 0, 0)],
            fetchedAtUtc: DateTimeOffset.UtcNow);

        var (rows, _) = repository.GetReportedUsage("anthropic");
        Assert.DoesNotContain(collection: rows, filter: r => r.UsageDay == oldDay);
    }
}