using System.Text;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Sandbox;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers PLAN.md Phase I / <c>docs/router/utility-model-routing.md</c> §B4: wiring an
/// <see cref="IRoutingPolicy"/> into <see cref="RequestInterceptor.ResolveModelRouteAsync"/> for the
/// router alias and the unresolved-model fallback.
/// </summary>
public class RequestInterceptorRoutingPolicyTests
{
    private static readonly string DefaultLiveDimension =
        RouterDimension.ToLiveKey(new SandboxOptions().LiveMemoryPrefix, RouterDimension.CodeGeneration);

    [Fact]
    public async Task ResolveModelRouteAsync_UnresolvedModel_WithRoutingPolicy_UsesPolicySelection()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var policy = new FakeRoutingPolicy("kimi-k2.5");
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("kimi-k2.5", result.Route!.ModelName);
        Assert.NotNull(policy.LastContext);
        Assert.Equal(2, policy.LastContext!.Candidates.Count);
        Assert.Contains(policy.LastContext.Candidates, c => c.ModelName == "gpt-5.4");
        Assert.Contains(policy.LastContext.Candidates, c => c.ModelName == "kimi-k2.5");
    }

    [Fact]
    public async Task ResolveModelRouteAsync_AutoModel_WithRoutingPolicy_UsesPolicySelection()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var policy = new FakeRoutingPolicy("gpt-5.4");
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"auto"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("gpt-5.4", result.Route!.ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_PolicySelectsUnresolvableModel_FallsBackToMemoryRanking()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var memory = new RouterMemory();
        await memory.AddScoreAsync(DefaultLiveDimension, "gpt-5.4", 0.1);
        await memory.AddScoreAsync(DefaultLiveDimension, "kimi-k2.5", 0.9);
        var policy = new FakeRoutingPolicy("not-a-configured-model");
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            routerMemory: memory,
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("kimi-k2.5", result.Route!.ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_ForcedSingleModel_NeverConsultsRoutingPolicy()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var policy = new FakeRoutingPolicy("gpt-5.4");
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            singleModelServingOptions: new SingleModelServingOptions { ForcedModelName = "gpt-5.4" },
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"whatever-the-client-sent"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(policy.LastContext);
    }

    private static DefaultHttpContext CreateContextWithBody(string body)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private sealed class FakeRoutingPolicy(string selection) : IRoutingPolicy
    {
        public RoutingContext? LastContext { get; private set; }

        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(selection);
        }
    }
}
