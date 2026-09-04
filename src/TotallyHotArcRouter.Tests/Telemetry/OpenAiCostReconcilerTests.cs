using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="OpenAiCostReconciler"/>: response shape parsing and pagination.</summary>
public class OpenAiCostReconcilerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Provider_IsOpenAi()
    {
        var reconciler = new OpenAiCostReconciler(httpClient: new HttpClient(new QueuedResponsesHandler([])),
            adminApiKey: "admin-key");

        Assert.Equal(expected: "openai", actual: reconciler.Provider);
    }

    [Fact]
    public async Task GetReportedCostAsync_SinglePage_SumsAllResults()
    {
        const string page = """
                            {
                                "data": [
                                    { "results": [ { "amount": { "value": 1.50, "currency": "usd" } }, { "amount": { "value": 2.25, "currency": "usd" } } ] }
                                ],
                                "has_more": false,
                                "next_page": null
                            }
                            """;
        var handler = new QueuedResponsesHandler([page]);
        var reconciler = new OpenAiCostReconciler(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var total = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 15), cancellationToken: Ct);

        Assert.Equal(3.75m, actual: total);
    }

    [Fact]
    public async Task GetReportedCostAsync_MultiplePages_FollowsNextPageAndSumsAcrossPages()
    {
        const string page1 = """
                             { "data": [ { "results": [ { "amount": { "value": 1.00 } } ] } ], "has_more": true, "next_page": "cursor-2" }
                             """;
        const string page2 = """
                             { "data": [ { "results": [ { "amount": { "value": 2.00 } } ] } ], "has_more": false, "next_page": null }
                             """;
        var handler = new QueuedResponsesHandler([page1, page2]);
        var reconciler = new OpenAiCostReconciler(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var total = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 15), cancellationToken: Ct);

        Assert.Equal(3.00m, actual: total);
        Assert.Equal(2, actual: handler.RequestUris.Count);
        Assert.Contains(expectedSubstring: "page=cursor-2", actualString: handler.RequestUris[1]);
    }

    [Fact]
    public async Task GetReportedCostAsync_NoResults_ReturnsZero()
    {
        const string page = """{ "data": [], "has_more": false, "next_page": null }""";
        var handler = new QueuedResponsesHandler([page]);
        var reconciler = new OpenAiCostReconciler(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var total = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 15), cancellationToken: Ct);

        Assert.Equal(0m, actual: total);
    }

    [Fact]
    public async Task GetReportedCostAsync_SendsBearerAuthorizationHeader()
    {
        const string page = """{ "data": [], "has_more": false, "next_page": null }""";
        var handler = new QueuedResponsesHandler([page]);
        var reconciler = new OpenAiCostReconciler(httpClient: new HttpClient(handler), adminApiKey: "my-admin-key");

        await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 15), cancellationToken: Ct);

        Assert.Equal(expected: "Bearer", actual: handler.LastRequestAuthScheme);
        Assert.Equal(expected: "my-admin-key", actual: handler.LastRequestAuthParameter);
    }
}