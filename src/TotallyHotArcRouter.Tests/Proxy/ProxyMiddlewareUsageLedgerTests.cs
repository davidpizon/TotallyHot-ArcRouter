using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProxyMiddleware"/>'s wiring into the durable usage ledger
/// (<c>docs/router/token-tracking-implementation-plan.md</c> Phase 2): a served request with extractable
/// usage is recorded, and an upstream <c>request-id</c> response header is what the recorded row dedupes
/// on, so a retried publish of the same upstream response collides with its own earlier write.
/// </summary>
public class ProxyMiddlewareUsageLedgerTests
{
    private const string PrimaryHost = "primary.test";

    [Fact]
    public async Task InvokeAsync_UsageExtracted_RecordsOneLedgerRow()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "primary-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(_ => OpenAiUsageResponse());

        await RunAsync(resolver: resolver, handler: handler, usageLedger: ledger);

        Assert.Equal(1, actual: CountRows(temp));
    }

    [Fact]
    public async Task InvokeAsync_SameUpstreamRequestIdTwice_DedupesToOneRow()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "primary-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(_ => OpenAiUsageResponse("req-fixed-123"));

        await RunAsync(resolver: resolver, handler: handler, usageLedger: ledger);
        await RunAsync(resolver: resolver, handler: handler, usageLedger: ledger);

        Assert.Equal(1, actual: CountRows(temp));
    }

    [Fact]
    public async Task InvokeAsync_DifferentUpstreamRequestIds_RecordsTwoRows()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "primary-upstream", $"https://{PrimaryHost}"));

        var callIndex = 0;
        var handler = new RoutingHandlerStub(_ => OpenAiUsageResponse($"req-{callIndex++}"));

        await RunAsync(resolver: resolver, handler: handler, usageLedger: ledger);
        await RunAsync(resolver: resolver, handler: handler, usageLedger: ledger);

        Assert.Equal(2, actual: CountRows(temp));
    }

    [Fact]
    public async Task InvokeAsync_FailoverServed_LedgerRowAttributesRequestedModelToTheModelThatServed()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        const string failoverPrimaryHost = "primary-failover.test";
        const string backupHost = "backup-failover.test";
        // Distinct provider keys so ModelRouteResolverTestFactory.CreateWithModels gives each its own
        // BaseUrl - "ollama" is OpenAI-shaped for usage-parsing purposes (UsageExtractor), so both
        // candidates' responses are still extractable.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "primary-upstream", $"https://{failoverPrimaryHost}"),
            ("backup", "ollama", "backup-upstream", $"https://{backupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == failoverPrimaryHost
            ? throw new HttpRequestException("connection refused")
            : OpenAiUsageResponse());

        await RunAsync(resolver: resolver, handler: handler, usageLedger: ledger);

        Assert.Equal(1, actual: CountRows(temp));
        // M2.3: the ledger's requested_model column holds the model that served ("backup"), not the one
        // lined up first ("primary") - a value fix, not a schema change (docs/router/agent-cost-tracking.md).
        Assert.Equal(expected: "backup", actual: ReadRequestedModel(temp));
    }

    [Fact]
    public async Task InvokeAsync_NoUsageBlock_RecordsNothing()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "primary-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[]}""", encoding: Encoding.UTF8, mediaType: "application/json")
        });

        await RunAsync(resolver: resolver, handler: handler, usageLedger: ledger);

        Assert.Equal(0, actual: CountRows(temp));
    }

    // -- helpers -------------------------------------------------------------

    private static HttpResponseMessage OpenAiUsageResponse(string? requestId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        };

        if (requestId is not null) response.Headers.Add(name: "request-id", value: requestId);

        return response;
    }

    private static int CountRows(TempDatabase temp)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_ledger;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ReadRequestedModel(TempDatabase temp)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT requested_model FROM usage_ledger LIMIT 1;";
        return (string)command.ExecuteScalar()!;
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

        // PublishTelemetryAsync (and the ledger write inside it) runs after the response is fully written,
        // inside its own try/catch so a failure there never surfaces to InvokeAsync's caller - awaited by
        // InvokeAsync itself, so by the time it returns the write has already happened or been dropped.
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