using System.Net;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="CostReconciliationRetryPolicy"/>.</summary>
public class CostReconciliationRetryPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendWithRetryAsync_FirstAttemptSucceeds_ReturnsImmediately_NoDelay()
    {
        var handler = new SequencedHandlerStub([HttpStatusCode.OK]);
        using var client = new HttpClient(handler);

        var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
            httpClient: client,
            requestFactory: () => new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://example.test/"),
            delayForAttempt: _ => TimeSpan.Zero,
            cancellationToken: Ct);

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        Assert.Equal(1, actual: handler.CallCount);
    }

    [Fact]
    public async Task SendWithRetryAsync_429ThenSuccess_RetriesOnce()
    {
        var handler = new SequencedHandlerStub([HttpStatusCode.TooManyRequests, HttpStatusCode.OK]);
        using var client = new HttpClient(handler);

        var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
            httpClient: client,
            requestFactory: () => new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://example.test/"),
            delayForAttempt: _ => TimeSpan.Zero,
            cancellationToken: Ct);

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        Assert.Equal(2, actual: handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task SendWithRetryAsync_RetryableStatus_EventuallyRetries(HttpStatusCode status)
    {
        var handler = new SequencedHandlerStub([status, HttpStatusCode.OK]);
        using var client = new HttpClient(handler);

        var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
            httpClient: client,
            requestFactory: () => new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://example.test/"),
            delayForAttempt: _ => TimeSpan.Zero,
            cancellationToken: Ct);

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        Assert.Equal(2, actual: handler.CallCount);
    }

    [Fact]
    public async Task SendWithRetryAsync_NonRetryableStatus_ReturnsImmediately_NoRetry()
    {
        var handler = new SequencedHandlerStub([HttpStatusCode.BadRequest, HttpStatusCode.OK]);
        using var client = new HttpClient(handler);

        var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
            httpClient: client,
            requestFactory: () => new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://example.test/"),
            delayForAttempt: _ => TimeSpan.Zero,
            cancellationToken: Ct);

        Assert.Equal(expected: HttpStatusCode.BadRequest, actual: response.StatusCode);
        Assert.Equal(1, actual: handler.CallCount);
    }

    [Fact]
    public async Task SendWithRetryAsync_AlwaysRetryable_StopsAtMaxAttempts_ReturnsLastResponse()
    {
        var handler = new SequencedHandlerStub([
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK // never reached - MaxAttempts is 4
        ]);
        using var client = new HttpClient(handler);

        var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
            httpClient: client,
            requestFactory: () => new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://example.test/"),
            delayForAttempt: _ => TimeSpan.Zero,
            cancellationToken: Ct);

        Assert.Equal(expected: HttpStatusCode.ServiceUnavailable, actual: response.StatusCode);
        Assert.Equal(expected: CostReconciliationRetryPolicy.MaxAttempts, actual: handler.CallCount);
    }

    private sealed class SequencedHandlerStub(IReadOnlyList<HttpStatusCode> statuses) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var status = statuses[Math.Min(val1: CallCount, val2: statuses.Count - 1)];
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}