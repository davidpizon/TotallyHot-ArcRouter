using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProxyMiddleware"/>'s enforcement of Governance &gt; Providers' Stop/Play toggle
/// (<see cref="ProviderOptions.Enabled"/>): a stopped provider is skipped so an enabled backup serves the
/// request, and when every candidate is stopped the request is rejected with 503 without ever calling
/// upstream. Mirrors <see cref="ProxyMiddlewareBudgetTests"/>'s shape for the analogous budget-cap gate.
/// </summary>
public class ProxyMiddlewareProviderEnabledTests
{
    private const string PrimaryHost = "primary.test";
    private const string BackupHost = "backup.test";

    [Fact]
    public async Task InvokeAsync_PrimaryProviderStopped_SkipsToEnabledBackup()
    {
        var resolver = CreateResolver(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}", false),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}", true));

        var primaryCalled = false;
        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == PrimaryHost) primaryCalled = true;

            return Ok("served-by-backup");
        });

        var context = await RunAsync(resolver: resolver, handler: handler);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.False(primaryCalled); // the stopped provider is never attempted
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_OnlyProviderStopped_Returns503_AndNeverCallsUpstream()
    {
        var resolver = CreateResolver(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}", false));

        var upstreamCalled = false;
        var handler = new RoutingHandlerStub(_ =>
        {
            upstreamCalled = true;
            return Ok("should-not-be-served");
        });

        var context = await RunAsync(resolver: resolver, handler: handler);

        Assert.Equal(expected: StatusCodes.Status503ServiceUnavailable, actual: context.Response.StatusCode);
        Assert.False(upstreamCalled);
        Assert.Contains(expectedSubstring: "stopped", actualString: await ReadBodyAsync(context),
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // -- helpers -------------------------------------------------------------

    private static IModelRouteResolver CreateResolver(
        params (string ModelName, string Provider, string ProviderModelId, string BaseUrl, bool Enabled)[] models)
    {
        var providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
            if (!providers.ContainsKey(model.Provider))
                providers[model.Provider] = new ProviderOptions
                {
                    BaseUrl = model.BaseUrl,
                    Enabled = model.Enabled
                };

        var options = new ModelRoutingOptions
        {
            Providers = providers,
            ModelList = [.. models
                .Select(m => new ModelRouteEntry
                { ModelName = m.ModelName, Provider = m.Provider, ProviderModelId = m.ProviderModelId })]
        };

        return new ModelRouteResolver(store: new InMemoryProviderConfigStore(options),
            environment: Mock.Of<IEnvironmentVariableProvider>());
    }

    private static HttpResponseMessage Ok(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(content: body, encoding: Encoding.UTF8, mediaType: "text/plain") };
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpContext> RunAsync(
        IModelRouteResolver resolver,
        RoutingHandlerStub handler,
        string requestedModel = "primary")
    {
        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler));

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

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);
        return context;
    }

    private sealed class RoutingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}