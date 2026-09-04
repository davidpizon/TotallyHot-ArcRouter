using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProxyMiddleware"/>'s per-provider monthly budget enforcement (Governance &gt;
/// Providers): a provider whose cap is exhausted is skipped so an under-budget fallback serves the request;
/// when every candidate is breached the request is rejected with 402; and a served request's usage is
/// recorded against the serving provider. This is separate from the transport-outage cascade in
/// <see cref="ProxyMiddlewareFallbackTests"/> - a breached provider is never even attempted.
/// </summary>
public class ProxyMiddlewareBudgetTests
{
    private const string PrimaryHost = "primary.test";
    private const string BackupHost = "backup.test";

    [Fact]
    public async Task InvokeAsync_PrimaryProviderBreached_SkipsToUnderBudgetBackup()
    {
        using var temp = new TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        budgetStore.SetBudget(providerKey: "prov-a", 0m, null); // cap of $0 is breached immediately

        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var primaryCalled = false;
        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == PrimaryHost) primaryCalled = true;

            return Ok("served-by-backup");
        });

        var capturing = new CapturingPublisher();
        var context = await RunAsync(resolver: resolver, handler: handler, budgetStore: budgetStore,
            telemetryPublisher: capturing);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.False(primaryCalled); // the breached provider is never attempted
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));

        var telemetry = await capturing.WaitAsync();
        Assert.True(telemetry.IsFallback);
        Assert.Equal(expected: "primary", actual: telemetry.RequestedModel); // the client still asked for the primary
        Assert.Equal(expected: "prov-b", actual: telemetry.Provider);
    }

    [Fact]
    public async Task InvokeAsync_AllProvidersBreached_Returns402_AndNeverCallsUpstream()
    {
        using var temp = new TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        budgetStore.SetBudget(providerKey: "prov-a", 0m, null);
        budgetStore.SetBudget(providerKey: "prov-b", 0m, null);

        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var upstreamCalled = false;
        var handler = new RoutingHandlerStub(_ =>
        {
            upstreamCalled = true;
            return Ok("should-not-be-served");
        });

        var context = await RunAsync(resolver: resolver, handler: handler, budgetStore: budgetStore);

        Assert.Equal(expected: StatusCodes.Status402PaymentRequired, actual: context.Response.StatusCode);
        Assert.False(upstreamCalled);
        Assert.Contains(expectedSubstring: "budget", actualString: await ReadBodyAsync(context),
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_LastCandidateBreachedMidRequest_Returns402()
    {
        // Models the concurrent-edit race the pre-loop all-breached check can't catch: IsBreached reports
        // under-budget on the pre-loop check, then over-budget when the loop re-checks the same provider (a
        // budget edit via the management API landed in between). With a single candidate this drives the
        // request out of the loop with nothing written - the post-loop safeguard must turn that into a 402
        // rather than a default empty 200.
        var enforcer = new FlipOnSecondCheckEnforcer();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "primary-upstream", $"https://{PrimaryHost}"));

        var upstreamCalled = false;
        var handler = new RoutingHandlerStub(_ =>
        {
            upstreamCalled = true;
            return Ok("should-not-be-served");
        });

        var context = await RunAsync(resolver: resolver, handler: handler, budgetStore: enforcer);

        Assert.Equal(expected: StatusCodes.Status402PaymentRequired, actual: context.Response.StatusCode);
        Assert.False(upstreamCalled);
        Assert.Contains(expectedSubstring: "budget", actualString: await ReadBodyAsync(context),
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_UnderBudgetProvider_ServesAndRecordsUsage()
    {
        using var temp = new TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        budgetStore.SetBudget(providerKey: "openai", 100m, 1_000_000L);

        // Provider "openai" so the usage extractor recognizes the OpenAI response shape and records tokens.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "primary-upstream", $"https://{PrimaryHost}"));

        // An OpenAI-shaped usage block so the usage extractor records real token counts.
        var handler = new RoutingHandlerStub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        });

        var context = await RunAsync(resolver: resolver, handler: handler, budgetStore: budgetStore);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);

        var status = budgetStore.GetStatus("openai");
        Assert.Equal(15L, actual: status.TokensUsed);
    }

    // -- helpers -------------------------------------------------------------

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
        IBudgetEnforcer budgetStore,
        ITelemetryPublisher? telemetryPublisher = null,
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
                TelemetryPublisher = telemetryPublisher,
                BudgetStore = budgetStore
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

    // Reports "under budget" on the first IsBreached call (the pre-loop all-breached check) and "over budget"
    // on every call after (the in-loop skip), modeling a budget edit that lands mid-request.
    private sealed class FlipOnSecondCheckEnforcer : IBudgetEnforcer
    {
        private int _checks;

        public bool IsBreached(string providerKey)
        {
            return ++_checks > 1;
        }

        public Task RecordUsageAsync(string providerKey, decimal? costUsd, int? promptTokens, int? completionTokens,
            int? cacheCreationTokens, int? cacheReadTokens, DateTimeOffset usageAtUtc,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingPublisher : ITelemetryPublisher
    {
        private readonly TaskCompletionSource<RoutingTelemetryEvent> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            _tcs.TrySetResult(telemetryEvent);
            return Task.CompletedTask;
        }

        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async Task<RoutingTelemetryEvent> WaitAsync()
        {
            var completed = await Task.WhenAny(task1: _tcs.Task, task2: Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(condition: ReferenceEquals(objA: completed, objB: _tcs.Task),
                userMessage: "Timed out waiting for a routing telemetry event.");
            return await _tcs.Task;
        }
    }
}