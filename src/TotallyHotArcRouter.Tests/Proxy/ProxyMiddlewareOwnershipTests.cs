using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Pins <see cref="ProxyMiddleware"/>'s disposal contract: it disposes the collaborators it built for
/// itself and leaves supplied ones to their owner.
///
/// <para>
/// The class already applied this rule to its Bedrock client factory (via <c>_ownsBedrockClientFactory</c>)
/// but not to its <see cref="HttpClient"/>, which it also constructs when none is supplied - so a
/// self-built client and its handler were never released. The asymmetry was the real hazard: the next
/// reader to copy the Bedrock ownership pattern would reasonably assume the client was already covered.
/// </para>
/// </summary>
public sealed class ProxyMiddlewareOwnershipTests
{
    /// <summary>Records whether it was disposed. Never actually sends: these tests construct and dispose only.</summary>
    private sealed class TrackingHandler : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("These tests never send a request.");

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static ProxyMiddleware Build(HttpClient? httpClient) =>
        new(NullLogger<ProxyMiddleware>.Instance,
            new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, ModelRouteResolverTestFactory.CreateWithModels(
                ("primary", "prov-a", "primary-upstream", "https://primary.test"))),
            httpClient);

    [Fact]
    public void Dispose_SuppliedHttpClient_IsLeftToItsOwner()
    {
        // The dangerous direction. In production the client is DI-owned and shared; in tests it usually
        // wraps a stub handler the test still uses afterward. Disposing it here would break both.
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
            .GetField("_httpClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
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
}
