using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="AnthropicUsageReportService"/>: the no-key no-op and the fetch-and-upsert path.</summary>
public class AnthropicUsageReportServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunCycleAsync_NoAdminApiKeyResolvable_DoesNothing()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var handler = new QueuedResponsesHandler([]);
        var service = new AnthropicUsageReportService(
            httpClient: new HttpClient(handler), repository: repository, resolveAdminApiKey: () => null,
            logger: NullLogger<AnthropicUsageReportService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Empty(handler.RequestUris);
        Assert.Empty(repository.GetReportedUsage("anthropic").Rows);
    }

    [Fact]
    public async Task RunCycleAsync_AdminApiKeyResolvable_FetchesAndUpsertsTheTrailingWindow()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var startingAt = DateTime.UtcNow.Date.AddDays(-1).ToString(format: "O", provider: CultureInfo.InvariantCulture);
        var page = $$"""
                     {
                         "data": [
                             { "starting_at": "{{startingAt}}", "results": [ { "model": "claude-opus-4-1", "output_tokens": 10 } ] }
                         ],
                         "has_more": false,
                         "next_page": null
                     }
                     """;
        var handler = new QueuedResponsesHandler([page]);
        var service = new AnthropicUsageReportService(
            httpClient: new HttpClient(handler), repository: repository, resolveAdminApiKey: () => "sk-ant-admin01-abc",
            logger: NullLogger<AnthropicUsageReportService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Single(handler.RequestUris);
        var (rows, fetchedAt) = repository.GetReportedUsage("anthropic");
        var row = Assert.Single(rows);
        Assert.Equal(expected: "claude-opus-4-1", actual: row.Model);
        Assert.Equal(10, actual: row.OutputTokens);
        Assert.NotNull(fetchedAt);
    }

    [Fact]
    public async Task RunCycleAsync_HttpFailure_IsSwallowed_AndDoesNotThrow()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        // No queued responses - the handler throws IndexOutOfRangeException on the first request, exercising
        // the catch-and-log path rather than an HTTP error status.
        var handler = new QueuedResponsesHandler([]);
        var service = new AnthropicUsageReportService(
            httpClient: new HttpClient(handler), repository: repository, resolveAdminApiKey: () => "sk-ant-admin01-abc",
            logger: NullLogger<AnthropicUsageReportService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Empty(repository.GetReportedUsage("anthropic").Rows);
    }
}