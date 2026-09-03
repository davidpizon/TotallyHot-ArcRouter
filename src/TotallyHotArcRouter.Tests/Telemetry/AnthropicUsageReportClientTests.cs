using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="AnthropicUsageReportClient"/>: response shape parsing and pagination.</summary>
public class AnthropicUsageReportClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetUsageReportAsync_SingleBucketSingleModel_ParsesTokenCounts()
    {
        const string page = """
                            {
                                "data": [
                                    {
                                        "starting_at": "2026-01-15T00:00:00Z",
                                        "results": [
                                            {
                                                "model": "claude-opus-4-1-20250805",
                                                "uncached_input_tokens": 100,
                                                "output_tokens": 50,
                                                "cache_read_input_tokens": 10,
                                                "cache_creation": { "ephemeral_5m_input_tokens": 5, "ephemeral_1h_input_tokens": 2 }
                                            }
                                        ]
                                    }
                                ],
                                "has_more": false,
                                "next_page": null
                            }
                            """;
        var handler = new QueuedResponsesHandler([page]);
        var client = new AnthropicUsageReportClient(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var rows = await client.GetUsageReportAsync(startingDay: new DateOnly(2026, 1, 15),
            endingDayExclusive: new DateOnly(2026, 1, 16), cancellationToken: Ct);

        var row = Assert.Single(rows);
        Assert.Equal(expected: new DateOnly(2026, 1, 15), actual: row.UsageDay);
        Assert.Equal(expected: "claude-opus-4-1-20250805", actual: row.Model);
        Assert.Equal(100, actual: row.InputTokens);
        Assert.Equal(50, actual: row.OutputTokens);
        Assert.Equal(7, actual: row.CacheCreationTokens); // 5 + 2 across the two duration buckets
        Assert.Equal(10, actual: row.CacheReadTokens);
    }

    [Fact]
    public async Task GetUsageReportAsync_MultipleModelsInOneBucket_ProducesOneRowPerModel()
    {
        const string page = """
                            {
                                "data": [
                                    {
                                        "starting_at": "2026-01-15T00:00:00Z",
                                        "results": [
                                            { "model": "claude-opus-4-1", "uncached_input_tokens": 10, "output_tokens": 5 },
                                            { "model": "claude-sonnet-5", "uncached_input_tokens": 20, "output_tokens": 15 }
                                        ]
                                    }
                                ],
                                "has_more": false,
                                "next_page": null
                            }
                            """;
        var handler = new QueuedResponsesHandler([page]);
        var client = new AnthropicUsageReportClient(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var rows = await client.GetUsageReportAsync(startingDay: new DateOnly(2026, 1, 15),
            endingDayExclusive: new DateOnly(2026, 1, 16), cancellationToken: Ct);

        Assert.Equal(2, actual: rows.Count);
        Assert.Contains(collection: rows,
            filter: r => r.Model == "claude-opus-4-1" && r.InputTokens == 10 && r.OutputTokens == 5);
        Assert.Contains(collection: rows,
            filter: r => r.Model == "claude-sonnet-5" && r.InputTokens == 20 && r.OutputTokens == 15);
    }

    [Fact]
    public async Task GetUsageReportAsync_MultiplePages_FollowsNextPageAndCombinesRows()
    {
        const string page1 = """
                             {
                                 "data": [ { "starting_at": "2026-01-15T00:00:00Z", "results": [ { "model": "m1", "output_tokens": 1 } ] } ],
                                 "has_more": true,
                                 "next_page": "cursor-2"
                             }
                             """;
        const string page2 = """
                             {
                                 "data": [ { "starting_at": "2026-01-16T00:00:00Z", "results": [ { "model": "m1", "output_tokens": 2 } ] } ],
                                 "has_more": false,
                                 "next_page": null
                             }
                             """;
        var handler = new QueuedResponsesHandler([page1, page2]);
        var client = new AnthropicUsageReportClient(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var rows = await client.GetUsageReportAsync(startingDay: new DateOnly(2026, 1, 15),
            endingDayExclusive: new DateOnly(2026, 1, 17), cancellationToken: Ct);

        Assert.Equal(2, actual: rows.Count);
        Assert.Equal(2, actual: handler.RequestUris.Count);
        Assert.Contains(expectedSubstring: "page=cursor-2", actualString: handler.RequestUris[1]);
        Assert.Contains(expectedSubstring: "group_by[]=model", actualString: handler.RequestUris[0]);
        Assert.Contains(expectedSubstring: "bucket_width=1d", actualString: handler.RequestUris[0]);
    }

    [Fact]
    public async Task GetUsageReportAsync_MissingModelField_SkipsThatResult()
    {
        const string page = """
                            {
                                "data": [
                                    {
                                        "starting_at": "2026-01-15T00:00:00Z",
                                        "results": [ { "output_tokens": 99 }, { "model": "kept", "output_tokens": 1 } ]
                                    }
                                ],
                                "has_more": false,
                                "next_page": null
                            }
                            """;
        var handler = new QueuedResponsesHandler([page]);
        var client = new AnthropicUsageReportClient(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var rows = await client.GetUsageReportAsync(startingDay: new DateOnly(2026, 1, 15),
            endingDayExclusive: new DateOnly(2026, 1, 16), cancellationToken: Ct);

        var row = Assert.Single(rows);
        Assert.Equal(expected: "kept", actual: row.Model);
    }

    [Fact]
    public async Task GetUsageReportAsync_NoData_ReturnsEmpty()
    {
        const string page = """{ "data": [], "has_more": false, "next_page": null }""";
        var handler = new QueuedResponsesHandler([page]);
        var client = new AnthropicUsageReportClient(httpClient: new HttpClient(handler), adminApiKey: "admin-key");

        var rows = await client.GetUsageReportAsync(startingDay: new DateOnly(2026, 1, 15),
            endingDayExclusive: new DateOnly(2026, 1, 16), cancellationToken: Ct);

        Assert.Empty(rows);
    }
}