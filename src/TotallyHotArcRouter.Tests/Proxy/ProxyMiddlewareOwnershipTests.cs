using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Reflection;
using System.Text;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Pins <see cref="ProxyMiddleware"/>'s disposal contract: it disposes the collaborators it built for
/// itself and leaves supplied ones to their owner.
/// <para>
/// The class already applied this rule to its Bedrock client factory (via <c>_ownsBedrockClientFactory</c>)
/// but not to its <see cref="HttpClient"/>, which it also constructs when none is supplied - so a
/// self-built client and its handler were never released. The asymmetry was the real hazard: the next
/// reader to copy the Bedrock ownership pattern would reasonably assume the client was already covered.
/// </para>
/// </summary>
public sealed class ProxyMiddlewareOwnershipTests
{
    private static ProxyMiddleware Build(HttpClient? httpClient)
    {
        return new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
                modelRouteResolver: ModelRouteResolverTestFactory.CreateWithModels(
                    ("primary", "prov-a", "primary-upstream", "https://primary.test"))),
            httpClient: httpClient);
    }

    [Fact]
    public void Dispose_SuppliedHttpClient_IsLeftToItsOwner()
    {
        // The dangerous direction. Tests pass a stub-wrapped client they still use afterward.
        // Disposing it here would break them. Production uses IHttpClientFactory instead.
        var handler = new TrackingHandler();
        var suppliedClient = new HttpClient(handler);

        Build(suppliedClient).Dispose();

        Assert.False(handler.Disposed);
    }

    [Fact]
    public void Dispose_SelfBuiltHttpClient_IsReleased()
    {
        // Constructed with no client, so the middleware builds its own and owns it. Observed through the
        // client's own disposal behavior - a disposed HttpClient rejects configuration - since the field
        // is private and there is no public surface that reveals it.
        var middleware = Build(httpClient: null);
        var selfBuilt = (HttpClient)typeof(ProxyMiddleware)
            .GetField(name: "_httpClient", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(middleware)!;

        middleware.Dispose();

        Assert.Throws<ObjectDisposedException>(() => selfBuilt.BaseAddress = new Uri("https://example.test"));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // The DI container disposes singletons once, but a test or a shutdown race may not be so tidy.
        var middleware = Build(httpClient: null);

        middleware.Dispose();

        var second = Record.Exception(middleware.Dispose);
        Assert.Null(second);
    }

    [Fact]
    public async Task InvokeAsync_WithFactory_ForwardsThroughTheNamedClient()
    {
        // Production supplies only the factory, so this is the path real traffic takes. Pins both the
        // client name the DI registration must match and that the upstream call actually goes through
        // the factory-created client rather than a fallback.
        var upstreamCalls = 0;
        var handler = new StubHandler(() =>
        {
            upstreamCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[]}""", encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
        });
        var factory = new RecordingHttpClientFactory(handler);
        using var middleware = new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
                modelRouteResolver: ModelRouteResolverTestFactory.CreateWithModels(
                    ("primary", "prov-a", "primary-upstream", "https://primary.test"))),
            httpClientFactory: factory);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var body = """{"model":"primary","messages":[{"role":"user","content":"hi"}]}"""u8.ToArray();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(1, upstreamCalls);
        Assert.Equal([ProxyMiddleware.HttpClientName], factory.RequestedNames);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>Records every client name requested and hands out clients over one shared stub handler.</summary>
    private sealed class RecordingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        internal List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return new HttpClient(handler: handler, disposeHandler: false);
        }
    }

    /// <summary>Answers every upstream request with the response the delegate builds.</summary>
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond());
        }
    }

    /// <summary>Records whether it was disposed. Never actually sends: these tests construct and dispose only.</summary>
    private sealed class TrackingHandler : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("These tests never send a request.");
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}