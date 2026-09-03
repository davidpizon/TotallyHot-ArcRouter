using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers the router's own token cost reaching <see cref="RoutingTelemetryEvent"/>. Research-doc §5.1
/// defines <c>TotTok</c> as router + model tokens, so a savings figure that ignores what routing itself
/// cost reports gross rather than net; these tests pin the request-path half of that accounting - the
/// embedding client's token count surviving through <see cref="ModelRouteResolutionResult"/> to a
/// published event, priced at <see cref="RoutingOptions.SelfHostedRouterPricePerMillionTokens"/>.
/// </summary>
public class ProxyMiddlewareRouterCostTests
{
    private const string RequestBodyJson =
        """{"model":"gpt-5.4","messages":[{"role":"user","content":"hello there"}]}""";

    [Fact]
    public async Task InvokeAsync_EmbeddingComputed_PublishesRouterTokensPricedAtTheSelfHostedRate()
    {
        var publisher = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware(publisher: publisher,
            embeddingClient: new FakeEmbeddingClient(embedding: [1f, 2f, 3f], 250));

        await middleware.InvokeAsync(context: BuildContext(), next: _ => Task.CompletedTask);

        var published = await publisher.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(250, actual: published.RouterTokens);

        // 250 tokens at the manuscript's $0.054/M self-hosted rate.
        Assert.Equal(expected: 250m / 1_000_000m * 0.054m, actual: published.RouterCostUsd);
    }

    [Fact]
    public async Task InvokeAsync_NoEmbeddingClient_PublishesZeroRouterCostAsAKnownValue()
    {
        // Zero here is a measurement, not a gap: with no embedding client the router genuinely spent
        // nothing deciding, so net savings equal gross rather than being unknown.
        var publisher = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware(publisher: publisher, null);

        await middleware.InvokeAsync(context: BuildContext(), next: _ => Task.CompletedTask);

        var published = await publisher.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, actual: published.RouterTokens);
        Assert.Equal(0m, actual: published.RouterCostUsd);
    }

    [Fact]
    public async Task InvokeAsync_OperatorOverridesTheSelfHostedRate_PricesRouterTokensAtThatRate()
    {
        // The rate is an amortization the operator owns (their hardware, their throughput), so an override
        // has to actually reach the published figure rather than the compiled-in manuscript default.
        var publisher = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware(
            publisher: publisher,
            embeddingClient: new FakeEmbeddingClient(embedding: [1f], 1_000_000),
            routingOptions: new RoutingOptions { SelfHostedRouterPricePerMillionTokens = 2.5m });

        await middleware.InvokeAsync(context: BuildContext(), next: _ => Task.CompletedTask);

        var published = await publisher.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1_000_000, actual: published.RouterTokens);
        Assert.Equal(2.5m, actual: published.RouterCostUsd);
    }

    private static ProxyMiddleware BuildMiddleware(
        ITelemetryPublisher publisher,
        IEmbeddingClient? embeddingClient,
        RoutingOptions? routingOptions = null)
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://example.com");

        EmbeddingWarmupState? warmupState = null;
        if (embeddingClient is not null)
        {
            warmupState = new EmbeddingWarmupState();
            warmupState.MarkWarm();
        }

        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            embeddingClient: embeddingClient,
            embeddingWarmupState: warmupState);

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"hi"}}]}""",
                encoding: Encoding.UTF8, mediaType: "application/json")
        }));

        return new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = publisher,
                RoutingOptions = Options.Create(routingOptions ?? new RoutingOptions())
            }
        );
    }

    private static DefaultHttpContext BuildContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes(RequestBodyJson);
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>Returns a fixed vector and a caller-chosen token count, standing in for ONNX inference.</summary>
    private sealed class FakeEmbeddingClient(float[] embedding, int tokenCount) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EmbeddingResult(Vector: embedding, TokenCount: tokenCount));
        }
    }

    /// <summary>Captures the single event this test's one request publishes.</summary>
    private sealed class CapturingTelemetryPublisher : ITelemetryPublisher
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

        public async Task<RoutingTelemetryEvent> WaitForEventAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task1: _tcs.Task, task2: Task.Delay(timeout));
            Assert.True(condition: ReferenceEquals(objA: completed, objB: _tcs.Task),
                userMessage: "Timed out waiting for a routing telemetry event to be published.");
            return await _tcs.Task;
        }
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}