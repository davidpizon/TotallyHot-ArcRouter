using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="AnthropicCostReconciler"/>: response shape parsing and pagination.</summary>
public class AnthropicCostReconcilerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Provider_IsAnthropic()
    {
        var reconciler = new AnthropicCostReconciler(httpClient: new HttpClient(new QueuedResponsesHandler([])),
            adminApiKey: "admin-key");

        Assert.Equal(expected: "anthropic", actual: reconciler.Provider);
    }

    [Fact]
    public async Task GetReportedCostAsync_SinglePage_SumsDecimalStringAmounts()
    {
        const string page = """
                            {
                                "data": [
                                    { "results": [ { "amount": "1.50" }, { "amount": "2.25" } ] }
                                ],
                                "has_more": false,
                                "next_page": null
                            }
                            """;
        var handler = new QueuedResponsesHandler([page]);
        var reconciler = new AnthropicCostReconciler(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var total = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 15), cancellationToken: Ct);

        Assert.Equal(3.75m, actual: total);
    }

    [Fact]
    public async Task GetReportedCostAsync_MultiplePages_FollowsNextPageAndSumsAcrossPages()
    {
        const string page1 = """
                             { "data": [ { "results": [ { "amount": "1.00" } ] } ], "has_more": true, "next_page": "cursor-2" }
                             """;
        const string page2 = """
                             { "data": [ { "results": [ { "amount": "2.00" } ] } ], "has_more": false, "next_page": null }
                             """;
        var handler = new QueuedResponsesHandler([page1, page2]);
        var reconciler = new AnthropicCostReconciler(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var total = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 15), cancellationToken: Ct);

        Assert.Equal(3.00m, actual: total);
        Assert.Equal(2, actual: handler.RequestUris.Count);
        Assert.Contains(expectedSubstring: "page=cursor-2", actualString: handler.RequestUris[1]);
    }

    [Fact]
    public async Task GetReportedCostAsync_NumericAmount_AlsoParses()
    {
        const string page =
            """{ "data": [ { "results": [ { "amount": 4.5 } ] } ], "has_more": false, "next_page": null }""";
        var handler = new QueuedResponsesHandler([page]);
        var reconciler = new AnthropicCostReconciler(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var total = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 15), cancellationToken: Ct);

        Assert.Equal(4.5m, actual: total);
    }

    [Fact]
    public async Task GetReportedCostAsync_NoResults_ReturnsZero()
    {
        const string page = """{ "data": [], "has_more": false, "next_page": null }""";
        var handler = new QueuedResponsesHandler([page]);
        var reconciler = new AnthropicCostReconciler(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var total = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 15), cancellationToken: Ct);

        Assert.Equal(0m, actual: total);
    }
}