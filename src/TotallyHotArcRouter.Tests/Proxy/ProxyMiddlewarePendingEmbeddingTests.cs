using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router.Embeddings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers docs/router/live-feedback-learning-plan.md Phase 2c's request-path half: once
/// <see cref="RequestInterceptor"/> has computed a task embedding, a completed request must populate
/// <see cref="PendingTaskEmbeddingCache"/> so <see cref="Router.EmbeddingMemoryScoreObserver"/> can later
/// claim it by correlation id.
/// </summary>
public class ProxyMiddlewarePendingEmbeddingTests
{
    [Fact]
    public async Task InvokeAsync_EmbeddingComputed_PopulatesPendingCacheForTheCompletedRequest()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://example.com");
        var warmupState = new EmbeddingWarmupState();
        warmupState.MarkWarm();
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            embeddingClient: new FakeEmbeddingClient([1f, 2f, 3f]),
            embeddingWarmupState: warmupState);
        var pendingCache = new PendingTaskEmbeddingCache(Options.Create(new RoutingOptions()));

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"hi"}}]}""", Encoding.UTF8, "application/json"),
        }));

        var middleware = new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor,
            new HttpClient(handler),
            pendingTaskEmbeddingCache: pendingCache);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4","messages":[{"role":"user","content":"hello there"}]}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(1, pendingCache.Count);
    }

    [Fact]
    public async Task InvokeAsync_NoEmbeddingComputed_LeavesPendingCacheEmpty()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://example.com");
        // No embedding client configured at all - the pre-Phase-2 default.
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var pendingCache = new PendingTaskEmbeddingCache(Options.Create(new RoutingOptions()));

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"hi"}}]}""", Encoding.UTF8, "application/json"),
        }));

        var middleware = new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor,
            new HttpClient(handler),
            pendingTaskEmbeddingCache: pendingCache);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4","messages":[{"role":"user","content":"hello there"}]}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(0, pendingCache.Count);
    }

    private sealed class FakeEmbeddingClient(float[] embedding, int tokenCount = 0) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult(embedding, tokenCount));
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
