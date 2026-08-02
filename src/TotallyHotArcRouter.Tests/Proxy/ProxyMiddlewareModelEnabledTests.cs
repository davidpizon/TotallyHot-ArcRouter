using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Text;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers enforcement of Governance &gt; Providers' per-model Start/Stop toggle and "Refresh from endpoint"
/// presence flag (<see cref="ModelRouteEntry.Enabled"/> / <see cref="ModelRouteEntry.PresentUpstream"/>): a
/// stopped or not-currently-upstream model is skipped so an eligible backup serves the request instead, and
/// upstream is never called for a model that's gated. The model-level twin of
/// <see cref="ProxyMiddlewareProviderEnabledTests"/>, same shape, but with one deliberate difference: a sole
/// gated candidate fails at <c>RequestInterceptor.ResolveModelRouteAsync</c>'s resolution step itself (400)
/// rather than reaching <see cref="ProxyMiddleware"/>'s later all-candidates-unavailable check (503) - see
/// <see cref="InvokeAsync_OnlyModelStopped_Returns400_AndNeverCallsUpstream"/>'s own comment for why.
/// </summary>
public class ProxyMiddlewareModelEnabledTests
{
    private const string PrimaryHost = "primary.test";
    private const string BackupHost = "backup.test";

    [Fact]
    public async Task InvokeAsync_PrimaryModelStopped_SkipsToEnabledBackup()
    {
        var resolver = CreateResolver(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}", enabled: false, presentUpstream: true),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}", enabled: true, presentUpstream: true));

        var primaryCalled = false;
        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == PrimaryHost)
            {
                primaryCalled = true;
            }

            return Ok("served-by-backup");
        });

        var context = await RunAsync(resolver, handler);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(primaryCalled); // the stopped model is never attempted
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_PrimaryModelNotPresentUpstream_SkipsToEnabledBackup()
    {
        // Same as above, via PresentUpstream (a "Refresh from endpoint" scan no longer reporting the model)
        // rather than Enabled (an operator's own Start/Stop toggle) - both gate the same way.
        var resolver = CreateResolver(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}", enabled: true, presentUpstream: false),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}", enabled: true, presentUpstream: true));

        var primaryCalled = false;
        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == PrimaryHost)
            {
                primaryCalled = true;
            }

            return Ok("served-by-backup");
        });

        var context = await RunAsync(resolver, handler);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(primaryCalled);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_OnlyModelStopped_Returns400_AndNeverCallsUpstream()
    {
        // Unlike the provider-disabled case (which still resolves to a candidate ProxyMiddleware later
        // finds all-unavailable and rejects with 503), a disabled model is caught earlier, at
        // RequestInterceptor.ResolveModelRouteAsync's own explicit-resolve gate - the same site TryResolve
        // itself is checked, so a sole disabled candidate never becomes a "resolved but unavailable"
        // candidate at all. It fails resolution outright, which ProxyMiddleware surfaces as 400.
        var resolver = CreateResolver(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}", enabled: false, presentUpstream: true));

        var upstreamCalled = false;
        var handler = new RoutingHandlerStub(_ =>
        {
            upstreamCalled = true;
            return Ok("should-not-be-served");
        });

        var context = await RunAsync(resolver, handler);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(upstreamCalled);
    }

    // -- helpers -------------------------------------------------------------

    private static IModelRouteResolver CreateResolver(
        params (string ModelName, string Provider, string ProviderModelId, string BaseUrl, bool enabled, bool presentUpstream)[] models)
    {
        var providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            if (!providers.ContainsKey(model.Provider))
            {
                providers[model.Provider] = new ProviderOptions
                {
                    BaseUrl = model.BaseUrl,
                    ApiKey = $"test-key-{model.Provider}"
                };
            }
        }

        var options = new ModelRoutingOptions
        {
            Providers = providers,
            ModelList = models
                .Select(m => new ModelRouteEntry
                {
                    ModelName = m.ModelName,
                    Provider = m.Provider,
                    ProviderModelId = m.ProviderModelId,
                    Enabled = m.enabled,
                    PresentUpstream = m.presentUpstream
                })
                .ToList()
        };

        return new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpContext> RunAsync(
        IModelRouteResolver resolver,
        RoutingHandlerStub handler,
        string requestedModel = "primary")
    {
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var body = Encoding.UTF8.GetBytes($$"""{"model":"{{requestedModel}}"}""");
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
}

