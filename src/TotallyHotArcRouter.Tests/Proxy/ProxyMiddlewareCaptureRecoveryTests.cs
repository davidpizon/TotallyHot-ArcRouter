using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProxyMiddleware"/>'s capture-recovery fallback
/// (<c>docs/router/token-tracking-implementation-plan.md</c> §5.11): a streamed response larger than
/// <c>MaxCapturedResponseBytes</c> whose trailing usage event falls outside the head-capped capture is
/// still recorded, because <see cref="IncrementalUsageScanner"/> retained a trailing window of the raw
/// stream independent of that cap.
/// </summary>
public class ProxyMiddlewareCaptureRecoveryTests
{
    private const string PrimaryHost = "primary.test";

    // Comfortably larger than ProxyMiddleware's 4 MiB head-capture cap, so the trailing usage event is
    // guaranteed to fall entirely outside it.
    private const int FillerBytes = 5 * 1024 * 1024;

    [Fact]
    public async Task InvokeAsync_StreamedResponseLargerThanCaptureCap_RecoversUsageFromTail()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "primary-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(_ => OversizedStreamingUsageResponse());

        await RunAsync(resolver: resolver, handler: handler, usageLedger: ledger);

        Assert.Equal(1, actual: CountRows(temp));
    }

    // -- helpers -------------------------------------------------------------

    private static HttpResponseMessage OversizedStreamingUsageResponse()
    {
        // One giant leading content-delta chunk pushes the trailing usage chunk well past the 4 MiB
        // head cap; ProxyMiddleware's primary (head-capped) parse must fail against it, so a successful
        // ledger row here can only have come from IncrementalUsageScanner's tail-window fallback.
        var filler = new string('a', count: FillerBytes);
        var sse =
            $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{filler}\"}}}}]}}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n" +
            "data: [DONE]\n\n";

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: sse, encoding: Encoding.UTF8, mediaType: "text/event-stream")
        };
        return response;
    }

    private static int CountRows(TempDatabase temp)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_ledger;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static async Task<HttpContext> RunAsync(
        IModelRouteResolver resolver,
        RoutingHandlerStub handler,
        IUsageLedger usageLedger,
        string requestedModel = "primary")
    {
        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                UsageLedger = usageLedger
            }
        );

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