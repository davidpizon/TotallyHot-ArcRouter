using System.Globalization;
using System.Net;
using System.Text;

namespace TotallyHot.ArcRouter.Gui.Admin.Tests;

/// <summary>
/// Unit coverage for <see cref="UsageQueryClient"/>: request URLs/headers and response (de)serialization
/// against a stubbed transport, plus error-envelope handling. Mirrors <see cref="ProviderAdminClientTests"/>
/// in structure because the two clients are intentional siblings.
/// </summary>
public sealed class UsageQueryClientTests
{
    private static UsageQueryClient CreateClient(HttpMessageHandler handler, string? token = null)
    {
        return new UsageQueryClient(
            httpClient: new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5001/") },
            adminToken: token);
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(content: body, encoding: Encoding.UTF8, mediaType: "application/json") };
    }

    // --- GetSummaryAsync ---

    [Fact]
    public async Task GetSummaryAsync_SendsGetToSummaryUrl()
    {
        const string json = """
                            {
                              "requests": 120,
                              "unpricedRequests": 5,
                              "promptTokens": 80000,
                              "completionTokens": 20000,
                              "cacheCreationTokens": 1000,
                              "cacheReadTokens": 500,
                              "costUsd": 3.75
                            }
                            """;
        var handler = new StubHandler(_ => Json(json));
        var client = CreateClient(handler);

        var summary =
            await client.GetSummaryAsync(window: "day", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Get, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/usage/summary?window=day",
            actual: handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(120L, actual: summary.Requests);
        Assert.Equal(5L, actual: summary.UnpricedRequests);
        Assert.Equal(80000L, actual: summary.PromptTokens);
        Assert.Equal(20000L, actual: summary.CompletionTokens);
        Assert.Equal(1000L, actual: summary.CacheCreationTokens);
        Assert.Equal(500L, actual: summary.CacheReadTokens);
        Assert.Equal(3.75m, actual: summary.CostUsd);
    }

    [Fact]
    public async Task GetSummaryAsync_EscapesWindowParameter()
    {
        var handler = new StubHandler(_ => Json("""
                                                {"requests":0,"unpricedRequests":0,"promptTokens":0,"completionTokens":0,"cacheCreationTokens":0,"cacheReadTokens":0,"costUsd":0}
                                                """));
        var client = CreateClient(handler);

        await client.GetSummaryAsync(window: "all&special", cancellationToken: TestContext.Current.CancellationToken);

        // Ampersand must be percent-encoded so it does not split the query string.
        Assert.Contains(expectedSubstring: "window=all%26special",
            actualString: handler.LastRequest!.RequestUri!.ToString(), comparisonType: StringComparison.Ordinal);
    }

    // --- GetRollupAsync ---

    [Fact]
    public async Task GetRollupAsync_SendsGetToRollupUrlWithAllParameters()
    {
        const string json = """
                            [
                              {
                                "bucketStartUtc": "2026-01-01T00:00:00Z",
                                "bucketWidth": "P1D",
                                "groupKey": "gpt-5.4",
                                "requests": 50,
                                "unpricedRequests": 2,
                                "promptTokens": 40000,
                                "completionTokens": 10000,
                                "cacheCreationTokens": 0,
                                "cacheReadTokens": 0,
                                "costUsd": 1.20
                              }
                            ]
                            """;
        var handler = new StubHandler(_ => Json(json));
        var client = CreateClient(handler);

        var from = DateTimeOffset.Parse(input: "2026-01-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(input: "2026-02-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);

        var buckets = await client.GetRollupAsync(from: from, to: to, width: "day", groupBy: "model",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Get, actual: handler.LastRequest!.Method);
        var url = handler.LastRequest.RequestUri!.ToString();
        Assert.StartsWith(expectedStartString: "http://localhost:5001/admin/usage/rollup?", actualString: url,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "width=day", actualString: url, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "groupBy=model", actualString: url,
            comparisonType: StringComparison.Ordinal);

        var bucket = Assert.Single(buckets);
        Assert.Equal(expected: "gpt-5.4", actual: bucket.GroupKey);
        Assert.Equal(50L, actual: bucket.Requests);
        Assert.Equal(1.20m, actual: bucket.CostUsd);
        Assert.Equal(expected: "P1D", actual: bucket.BucketWidth);
    }

    [Fact]
    public async Task GetRollupAsync_ReturnsEmptyListForEmptyArray()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var client = CreateClient(handler);

        var from = DateTimeOffset.Parse(input: "2026-01-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(input: "2026-02-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);

        var buckets = await client.GetRollupAsync(from: from, to: to, width: "hour", groupBy: "provider",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(buckets);
    }

    // --- GetRoutingRoiAsync ---

    [Fact]
    public async Task GetRoutingRoiAsync_SendsGetAndDeserializesTheCounterfactual()
    {
        const string json = """
                            [
                              {
                                "comparedAtUtc": "2026-01-05T10:00:00Z",
                                "sessionId": "session-7",
                                "routedModel": "kimi-k2.5",
                                "baselineModel": "glm-5",
                                "actualCostUsd": 0.02,
                                "baselineEstimatedCostUsd": 0.11,
                                "estimatedNetSavingsUsd": 0.09,
                                "isExploratory": true
                              }
                            ]
                            """;
        var handler = new StubHandler(_ => Json(json));
        var client = CreateClient(handler);

        var from = DateTimeOffset.Parse(input: "2026-01-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(input: "2026-02-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);

        var points = await client.GetRoutingRoiAsync(from: from, to: to,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Get, actual: handler.LastRequest!.Method);
        Assert.StartsWith(
            expectedStartString: "http://localhost:5001/admin/usage/routing-roi?",
            actualString: handler.LastRequest.RequestUri!.ToString(), comparisonType: StringComparison.Ordinal);

        var point = Assert.Single(points);
        Assert.Equal(expected: "session-7", actual: point.SessionId);
        Assert.Equal(expected: "kimi-k2.5", actual: point.RoutedModel);
        Assert.Equal(expected: "glm-5", actual: point.BaselineModel);
        Assert.Equal(0.09m, actual: point.EstimatedNetSavingsUsd);
        Assert.True(point.IsExploratory);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_OmitsTheSessionParameterWhenNotFiltering()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var client = CreateClient(handler);

        var from = DateTimeOffset.Parse(input: "2026-01-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        await client.GetRoutingRoiAsync(from: from, to: from.AddDays(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(expectedSubstring: "session=", actualString: handler.LastRequest!.RequestUri!.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_EscapesTheSessionParameter()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var client = CreateClient(handler);

        var from = DateTimeOffset.Parse(input: "2026-01-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        await client.GetRoutingRoiAsync(from: from, to: from.AddDays(1), sessionId: "a&b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expectedSubstring: "session=a%26b", actualString: handler.LastRequest!.RequestUri!.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_NullCostsRoundTripAsNullNotZero()
    {
        // An abstaining baseline yields nulls; collapsing them to 0 would read as "routing broke even".
        const string json = """
                            [
                              {
                                "comparedAtUtc": "2026-01-05T10:00:00Z",
                                "sessionId": "s",
                                "routedModel": "kimi-k2.5",
                                "baselineModel": null,
                                "actualCostUsd": 0.02,
                                "baselineEstimatedCostUsd": null,
                                "estimatedNetSavingsUsd": null,
                                "isExploratory": false
                              }
                            ]
                            """;
        var client = CreateClient(new StubHandler(_ => Json(json)));

        var from = DateTimeOffset.Parse(input: "2026-01-01T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        var points = await client.GetRoutingRoiAsync(from: from, to: from.AddDays(30),
            cancellationToken: TestContext.Current.CancellationToken);

        var point = Assert.Single(points);
        Assert.Null(point.BaselineModel);
        Assert.Null(point.BaselineEstimatedCostUsd);
        Assert.Null(point.EstimatedNetSavingsUsd);
    }

    // --- admin token header ---

    [Fact]
    public async Task AdminToken_WhenConfigured_IsSentAsHeader()
    {
        var handler = new StubHandler(_ => Json("""
                                                {"requests":0,"unpricedRequests":0,"promptTokens":0,"completionTokens":0,"cacheCreationTokens":0,"cacheReadTokens":0,"costUsd":0}
                                                """));
        var client = CreateClient(handler: handler, token: "s3cret");

        await client.GetSummaryAsync(window: "week", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(handler.LastRequest!.Headers.TryGetValues(name: "X-Admin-Token", values: out var values));
        Assert.Equal(expected: "s3cret", actual: Assert.Single(values));
    }

    [Fact]
    public async Task AdminToken_WhenNotConfigured_IsNotSent()
    {
        var handler = new StubHandler(_ => Json("""
                                                {"requests":0,"unpricedRequests":0,"promptTokens":0,"completionTokens":0,"cacheCreationTokens":0,"cacheReadTokens":0,"costUsd":0}
                                                """));
        var client = CreateClient(handler);

        await client.GetSummaryAsync(window: "week", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(handler.LastRequest!.Headers.Contains("X-Admin-Token"));
    }

    // --- error handling ---

    [Fact]
    public async Task ErrorResponse_ThrowsWithServerMessage()
    {
        const string errorJson =
            """{ "error": { "message": "Usage data unavailable.", "type": "server_error", "code": "500" } }""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(content: errorJson, encoding: Encoding.UTF8, mediaType: "application/json")
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetSummaryAsync(window: "day", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedSubstring: "Usage data unavailable", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorResponse_WithNonJsonBody_FallsBackToRawBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(content: "service overloaded", encoding: Encoding.UTF8, mediaType: "text/plain")
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetSummaryAsync(window: "day", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(expected: "service overloaded", actual: ex.Message);
    }

    [Fact]
    public async Task ErrorResponse_WithEmptyBody_FallsBackToTheStatusCode()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(string.Empty)
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetSummaryAsync(window: "day", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(expected: "The proxy management API returned 404.", actual: ex.Message);
    }

    [Fact]
    public async Task TransportFailure_ThrowsWithTheUnderlyingExceptionAsInnerException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetSummaryAsync(window: "day", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedSubstring: "Could not reach the proxy management API", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task MalformedJsonResponse_ThrowsWithTheParseError()
    {
        var handler = new StubHandler(_ => Json("{ not valid json"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetSummaryAsync(window: "day", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedSubstring: "unreadable response", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task NullJsonResponse_ThrowsAnEmptyResponseError()
    {
        var handler = new StubHandler(_ => Json("null"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetSummaryAsync(window: "day", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedSubstring: "empty response", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UsageQueryClient(null!));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class ThrowingHandler(HttpRequestException exception) : HttpMessageHandler
    {
        private readonly HttpRequestException _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }
}