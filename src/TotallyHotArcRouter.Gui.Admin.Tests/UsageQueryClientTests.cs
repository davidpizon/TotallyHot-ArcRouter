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
    private static UsageQueryClient CreateClient(HttpMessageHandler handler, string? token = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5001/") }, token);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly HttpRequestException _exception;

        public ThrowingHandler(HttpRequestException exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _exception;
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

        var summary = await client.GetSummaryAsync("day", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("http://localhost:5001/admin/usage/summary?window=day", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(120L, summary.Requests);
        Assert.Equal(5L, summary.UnpricedRequests);
        Assert.Equal(80000L, summary.PromptTokens);
        Assert.Equal(20000L, summary.CompletionTokens);
        Assert.Equal(1000L, summary.CacheCreationTokens);
        Assert.Equal(500L, summary.CacheReadTokens);
        Assert.Equal(3.75m, summary.CostUsd);
    }

    [Fact]
    public async Task GetSummaryAsync_EscapesWindowParameter()
    {
        var handler = new StubHandler(_ => Json("""
            {"requests":0,"unpricedRequests":0,"promptTokens":0,"completionTokens":0,"cacheCreationTokens":0,"cacheReadTokens":0,"costUsd":0}
            """));
        var client = CreateClient(handler);

        await client.GetSummaryAsync("all&special", TestContext.Current.CancellationToken);

        // Ampersand must be percent-encoded so it does not split the query string.
        Assert.Contains("window=all%26special", handler.LastRequest!.RequestUri!.ToString(), StringComparison.Ordinal);
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

        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture);

        var buckets = await client.GetRollupAsync(from, to, "day", "model", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        var url = handler.LastRequest.RequestUri!.ToString();
        Assert.StartsWith("http://localhost:5001/admin/usage/rollup?", url, StringComparison.Ordinal);
        Assert.Contains("width=day", url, StringComparison.Ordinal);
        Assert.Contains("groupBy=model", url, StringComparison.Ordinal);

        var bucket = Assert.Single(buckets);
        Assert.Equal("gpt-5.4", bucket.GroupKey);
        Assert.Equal(50L, bucket.Requests);
        Assert.Equal(1.20m, bucket.CostUsd);
        Assert.Equal("P1D", bucket.BucketWidth);
    }

    [Fact]
    public async Task GetRollupAsync_ReturnsEmptyListForEmptyArray()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var client = CreateClient(handler);

        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture);

        var buckets = await client.GetRollupAsync(from, to, "hour", "provider", TestContext.Current.CancellationToken);

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

        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture);

        var points = await client.GetRoutingRoiAsync(from, to, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.StartsWith(
            "http://localhost:5001/admin/usage/routing-roi?", handler.LastRequest.RequestUri!.ToString(), StringComparison.Ordinal);

        var point = Assert.Single(points);
        Assert.Equal("session-7", point.SessionId);
        Assert.Equal("kimi-k2.5", point.RoutedModel);
        Assert.Equal("glm-5", point.BaselineModel);
        Assert.Equal(0.09m, point.EstimatedNetSavingsUsd);
        Assert.True(point.IsExploratory);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_OmitsTheSessionParameterWhenNotFiltering()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var client = CreateClient(handler);

        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        await client.GetRoutingRoiAsync(from, from.AddDays(1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("session=", handler.LastRequest!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_EscapesTheSessionParameter()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var client = CreateClient(handler);

        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        await client.GetRoutingRoiAsync(from, from.AddDays(1), "a&b", TestContext.Current.CancellationToken);

        Assert.Contains("session=a%26b", handler.LastRequest!.RequestUri!.ToString(), StringComparison.Ordinal);
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

        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var points = await client.GetRoutingRoiAsync(from, from.AddDays(30), cancellationToken: TestContext.Current.CancellationToken);

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
        var client = CreateClient(handler, token: "s3cret");

        await client.GetSummaryAsync("week", TestContext.Current.CancellationToken);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("X-Admin-Token", out var values));
        Assert.Equal("s3cret", Assert.Single(values!));
    }

    [Fact]
    public async Task AdminToken_WhenNotConfigured_IsNotSent()
    {
        var handler = new StubHandler(_ => Json("""
            {"requests":0,"unpricedRequests":0,"promptTokens":0,"completionTokens":0,"cacheCreationTokens":0,"cacheReadTokens":0,"costUsd":0}
            """));
        var client = CreateClient(handler);

        await client.GetSummaryAsync("week", TestContext.Current.CancellationToken);

        Assert.False(handler.LastRequest!.Headers.Contains("X-Admin-Token"));
    }

    // --- error handling ---

    [Fact]
    public async Task ErrorResponse_ThrowsWithServerMessage()
    {
        const string errorJson = """{ "error": { "message": "Usage data unavailable.", "type": "server_error", "code": "500" } }""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(
            () => client.GetSummaryAsync("day", TestContext.Current.CancellationToken));

        Assert.Contains("Usage data unavailable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorResponse_WithNonJsonBody_FallsBackToRawBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("service overloaded", Encoding.UTF8, "text/plain")
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(
            () => client.GetSummaryAsync("day", TestContext.Current.CancellationToken));

        Assert.Equal("service overloaded", ex.Message);
    }

    [Fact]
    public async Task ErrorResponse_WithEmptyBody_FallsBackToTheStatusCode()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(string.Empty)
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(
            () => client.GetSummaryAsync("day", TestContext.Current.CancellationToken));

        Assert.Equal("The proxy management API returned 404.", ex.Message);
    }

    [Fact]
    public async Task TransportFailure_ThrowsWithTheUnderlyingExceptionAsInnerException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(
            () => client.GetSummaryAsync("day", TestContext.Current.CancellationToken));

        Assert.Contains("Could not reach the proxy management API", ex.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task MalformedJsonResponse_ThrowsWithTheParseError()
    {
        var handler = new StubHandler(_ => Json("{ not valid json"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(
            () => client.GetSummaryAsync("day", TestContext.Current.CancellationToken));

        Assert.Contains("unreadable response", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task NullJsonResponse_ThrowsAnEmptyResponseError()
    {
        var handler = new StubHandler(_ => Json("null"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(
            () => client.GetSummaryAsync("day", TestContext.Current.CancellationToken));

        Assert.Contains("empty response", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UsageQueryClient(null!));
    }
}
