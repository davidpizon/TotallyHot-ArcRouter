using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProxyMiddleware"/>'s routing kill switch (<see cref="IRoutingGate"/>), toggled from the
/// GUI system tray: a disabled gate rejects LLM-forwarding requests with 503 before any routing/upstream
/// work begins, but must not affect <c>/v1/models</c> - clients can still discover models while routing is
/// paused (docs discussion: administrative/discovery surfaces stay available).
/// </summary>
public sealed class ProxyMiddlewareRoutingGateTests
{
    private const string PrimaryHost = "primary.test";

    [Fact]
    public async Task InvokeAsync_GateDisabled_Returns503_AndNeverCallsUpstream()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"));

        var upstreamCalled = false;
        var handler = new RoutingHandlerStub(_ =>
        {
            upstreamCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var context = await RunChatCompletionAsync(resolver, handler, new FakeRoutingGate(isEnabled: false));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(upstreamCalled);
        Assert.Contains("disabled", await ReadBodyAsync(context), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_GateEnabled_ServesNormally()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("served", Encoding.UTF8, "text/plain"),
        });

        var context = await RunChatCompletionAsync(resolver, handler, new FakeRoutingGate(isEnabled: true));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_NoGateSupplied_ServesNormally()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("served", Encoding.UTF8, "text/plain"),
        });

        var context = await RunChatCompletionAsync(resolver, handler, routingGate: null);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_GateDisabled_ModelsListStillWorks()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"));

        var upstreamCalled = false;
        var handler = new RoutingHandlerStub(_ =>
        {
            upstreamCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                RoutingGate = new FakeRoutingGate(isEnabled: false)
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/models";
        context.Response.Body = new MemoryStream();
        context.RequestAborted = TestContext.Current.CancellationToken;

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(upstreamCalled);
    }

    // -- helpers -------------------------------------------------------------

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpContext> RunChatCompletionAsync(
        IModelRouteResolver resolver,
        RoutingHandlerStub handler,
        IRoutingGate? routingGate)
    {
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                RoutingGate = routingGate
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var body = Encoding.UTF8.GetBytes("""{"model":"primary"}""");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        context.RequestAborted = TestContext.Current.CancellationToken;

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);
        return context;
    }

    private sealed class RoutingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class FakeRoutingGate(bool isEnabled) : IRoutingGate
    {
        public bool IsEnabled { get; private set; } = isEnabled;

        public void SetEnabled(bool enabled) => IsEnabled = enabled;
    }
}
