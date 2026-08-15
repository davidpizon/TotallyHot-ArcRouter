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
/// Covers docs/router/live-feedback-learning-plan.md Phase 2b: <see cref="RequestInterceptor"/> computing
/// a request's task embedding under a budget, skipping until warm, and degrading to <see langword="null"/>
/// on any failure rather than ever blocking or failing the request.
/// </summary>
public class RequestInterceptorEmbeddingTests
{
    [Fact]
    public async Task ResolveModelRouteAsync_NoEmbeddingClientConfigured_TaskEmbeddingIsNullAndRoutingSucceeds()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"gpt-5.4","messages":[{"role":"user","content":"hello"}]}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.TaskEmbedding);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_ClientConfiguredButNotWarm_SkipsEmbeddingWithoutCallingClient()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var client = new FakeEmbeddingClient(_ => [1f, 0f]);
        var warmupState = new EmbeddingWarmupState(); // never marked warm
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            embeddingClient: client,
            embeddingWarmupState: warmupState);
        var context = CreateContextWithBody("""{"model":"gpt-5.4","messages":[{"role":"user","content":"hello"}]}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.TaskEmbedding);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_WarmClientWithText_ComputesEmbedding()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var expected = new float[] { 0.1f, 0.2f, 0.3f };
        var client = new FakeEmbeddingClient(_ => expected);
        var warmupState = new EmbeddingWarmupState();
        warmupState.MarkWarm();
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            embeddingClient: client,
            embeddingWarmupState: warmupState);
        var context = CreateContextWithBody("""{"model":"gpt-5.4","messages":[{"role":"user","content":"refactor this"}]}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.TaskEmbedding);
        Assert.Equal(1, client.CallCount);
        Assert.Equal("refactor this", client.LastText);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_NoExtractableTaskText_SkipsEmbeddingWithoutCallingClient()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var client = new FakeEmbeddingClient(_ => [1f]);
        var warmupState = new EmbeddingWarmupState();
        warmupState.MarkWarm();
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            embeddingClient: client,
            embeddingWarmupState: warmupState);
        // No "messages" array at all, so RequestTextExtractor.ExtractNewestUserMessage returns null.
        var context = CreateContextWithBody("""{"model":"gpt-5.4"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.TaskEmbedding);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_ClientThrows_DegradesToNullEmbeddingAndRoutingStillSucceeds()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var client = new FakeEmbeddingClient(_ => throw new InvalidOperationException("embedding backend unavailable"));
        var warmupState = new EmbeddingWarmupState();
        warmupState.MarkWarm();
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            embeddingClient: client,
            embeddingWarmupState: warmupState);
        var context = CreateContextWithBody("""{"model":"gpt-5.4","messages":[{"role":"user","content":"hello"}]}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.TaskEmbedding);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_ClientExceedsBudget_DegradesToNullEmbeddingAndRoutingStillSucceeds()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var client = new FakeEmbeddingClient(async (text, ct) =>
        {
            // Never completes on its own; only the budget's linked cancellation can end this call - the
            // very behavior under test.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new EmbeddingResult([1f], TokenCount: 1);
        });
        var warmupState = new EmbeddingWarmupState();
        warmupState.MarkWarm();
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            embeddingClient: client,
            embeddingWarmupState: warmupState,
            routingOptions: Options.Create(new RoutingOptions { EmbeddingBudgetMs = 20 }));
        var context = CreateContextWithBody("""{"model":"gpt-5.4","messages":[{"role":"user","content":"hello"}]}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.TaskEmbedding);
    }

    private static DefaultHttpContext CreateContextWithBody(string body)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        private readonly Func<string, CancellationToken, Task<EmbeddingResult>> _embed;

        public FakeEmbeddingClient(Func<string, float[]> embed) : this((text, _) => Task.FromResult(new EmbeddingResult(embed(text), TokenCount: 0)))
        {
        }

        public FakeEmbeddingClient(Func<string, CancellationToken, Task<EmbeddingResult>> embed) => _embed = embed;

        public int CallCount { get; private set; }

        public string? LastText { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastText = text;
            return _embed(text, cancellationToken);
        }
    }
}
