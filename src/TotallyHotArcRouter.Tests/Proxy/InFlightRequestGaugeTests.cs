using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="InFlightRequestGauge"/> (docs/router/routing-roi-regret-plan.md) - the counter's own
/// semantics and, critically, that <see cref="ProxyMiddleware.InvokeAsync"/> holds the gauge for the
/// request's full duration and always releases it, since a leaked increment would pause the ROI drain
/// forever.
/// </summary>
public class InFlightRequestGaugeTests
{
    [Fact]
    public void Track_RaisesTheCountForTheScopeAndReleasesOnDispose()
    {
        var gauge = new InFlightRequestGauge();
        Assert.Equal(0, gauge.Count);

        var outer = gauge.Track();
        var inner = gauge.Track();
        Assert.Equal(2, gauge.Count);

        inner.Dispose();
        Assert.Equal(1, gauge.Count);
        outer.Dispose();
        Assert.Equal(0, gauge.Count);
    }

    [Fact]
    public void Track_DoubleDispose_DecrementsOnlyOnce()
    {
        var gauge = new InFlightRequestGauge();

        var scope = gauge.Track();
        scope.Dispose();
        scope.Dispose();

        // A second Dispose must not drive the count negative - a negative count would read as "idle"
        // while requests are actually in flight.
        Assert.Equal(0, gauge.Count);
    }

    [Fact]
    public async Task InvokeAsync_CountsTheRequestWhileUpstreamIsServing_AndReleasesAfterward()
    {
        var gauge = new InFlightRequestGauge();
        var countDuringUpstreamCall = -1;

        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "model-a",
            providerModelId: "model-a",
            baseUrl: "http://localhost:9/v1",
            providerName: "test",
            apiKey: null);
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var handler = new DelegatingHandlerStub(_ =>
        {
            // Observed at the deepest point of the request - the upstream call - where background work
            // pausing on the gauge matters most.
            countDuringUpstreamCall = gauge.Count;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[]}""", Encoding.UTF8, "application/json"),
            });
        });

        var middleware = new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(), interceptor, new HttpClient(handler), inFlightGauge: gauge);

        await middleware.InvokeAsync(CreateChatContext(), _ => Task.CompletedTask);

        Assert.Equal(1, countDuringUpstreamCall);
        Assert.Equal(0, gauge.Count);
    }

    [Fact]
    public async Task InvokeAsync_UpstreamFailure_StillReleasesTheGauge()
    {
        var gauge = new InFlightRequestGauge();

        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "model-a",
            providerModelId: "model-a",
            baseUrl: "http://localhost:9/v1",
            providerName: "test",
            apiKey: null);
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new HttpRequestException("connection refused"));

        var middleware = new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(), interceptor, new HttpClient(handler), inFlightGauge: gauge);

        await middleware.InvokeAsync(CreateChatContext(), _ => Task.CompletedTask);

        // Whatever the outcome, the tracking scope's disposal must run - a leaked increment pauses the
        // taxonomy-comparison drain permanently.
        Assert.Equal(0, gauge.Count);
    }

    /// <summary>Routes every outgoing upstream request through the given delegate, following the sibling test files' convention.</summary>
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }

    /// <summary>Builds a minimal OpenAI-shaped chat-completions request context.</summary>
    private static DefaultHttpContext CreateChatContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var body = Encoding.UTF8.GetBytes("""{"model":"model-a","messages":[{"role":"user","content":"hi"}]}""");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
