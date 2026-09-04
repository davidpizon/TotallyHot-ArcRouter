using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers PLAN.md Phase I / <c>docs/router/utility-model-routing.md</c> §B4: wiring an
/// <see cref="IRoutingPolicy"/> into <see cref="RequestInterceptor.ResolveModelRouteAsync"/> for the
/// router alias and the unresolved-model fallback.
/// </summary>
public class RequestInterceptorRoutingPolicyTests
{
    private static readonly string DefaultLiveDimension =
        RouterDimension.ToLiveKey(liveMemoryPrefix: new QualityOptions().LiveMemoryPrefix,
            dimension: RouterDimension.CodeGeneration);

    [Fact]
    public async Task ResolveModelRouteAsync_UnresolvedModel_WithRoutingPolicy_UsesPolicySelection()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var policy = new FakeRoutingPolicy("kimi-k2.5");
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        var result = await interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "kimi-k2.5", actual: result.Route!.ModelName);
        Assert.NotNull(policy.LastContext);
        Assert.Equal(2, actual: policy.LastContext!.Candidates.Count);
        Assert.Contains(collection: policy.LastContext.Candidates, filter: c => c.ModelName == "gpt-5.4");
        Assert.Contains(collection: policy.LastContext.Candidates, filter: c => c.ModelName == "kimi-k2.5");
    }

    [Fact]
    public async Task ResolveModelRouteAsync_AutoModel_WithRoutingPolicy_UsesPolicySelection()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var policy = new FakeRoutingPolicy("gpt-5.4");
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"auto"}""");

        var result = await interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "gpt-5.4", actual: result.Route!.ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_PolicySelectsUnresolvableModel_FallsBackToMemoryRanking()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: DefaultLiveDimension, model: "gpt-5.4", 0.1);
        await memory.AddScoreAsync(dimension: DefaultLiveDimension, model: "kimi-k2.5", 0.9);
        var policy = new FakeRoutingPolicy("not-a-configured-model");
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            routerMemory: memory,
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        var result = await interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "kimi-k2.5", actual: result.Route!.ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_CandidatesRankedForPolicy_ColdStartOutranksLowScore()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("low-scored", "openai", "low-scored"),
            ("unscored", "moonshot", "unscored"));
        var memory = new RouterMemory();
        // Below the 0.5 cold-start ranking prior (see RequestInterceptor.ColdStartRankingScore).
        await memory.AddScoreAsync(dimension: DefaultLiveDimension, model: "low-scored", 0.1);
        var policy = new FakeRoutingPolicy("low-scored");
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            routerMemory: memory,
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        await interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(policy.LastContext);
        Assert.Equal(expected: "unscored", actual: policy.LastContext!.Candidates[0].ModelName);
        Assert.Equal(expected: "low-scored", actual: policy.LastContext.Candidates[1].ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_ForcedSingleModel_NeverConsultsRoutingPolicy()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://api.openai.com");
        var policy = new FakeRoutingPolicy("gpt-5.4");
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            singleModelServingOptions: new SingleModelServingOptions { ForcedModelName = "gpt-5.4" },
            routingPolicy: policy);
        var context = CreateContextWithBody("""{"model":"whatever-the-client-sent"}""");

        var result = await interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(policy.LastContext);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_PolicyOverridesSignalsOverload_ReceivesExtractedTaskText()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var policy = new SignalsCapturingRoutingPolicy("kimi-k2.5");
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            routingPolicy: policy);
        var context = CreateContextWithBody(
            """{"model":"agentic-router","messages":[{"role":"user","content":"please refactor this function"}]}""");

        var result = await interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(policy.LastSignals);
        Assert.Equal(expected: "please refactor this function", actual: policy.LastSignals!.TaskText);
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

    /// <summary>
    /// Unlike <see cref="FakeRoutingPolicy"/>, overrides the <see cref="RoutingSignals"/> overload
    /// directly (mirroring <see cref="TotallyHot.ArcRouter.Router.Orchestrator.OrchestratorRoutingPolicy"/>)
    /// to prove <see cref="RequestInterceptor"/> actually calls it, rather than only ever hitting
    /// <see cref="IRoutingPolicy"/>'s default-interface-method fallback.
    /// </summary>
    private sealed class SignalsCapturingRoutingPolicy(string selection) : IRoutingPolicy
    {
        public RoutingSignals? LastSignals { get; private set; }

        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            return SelectModelAsync(context: context, null, cancellationToken: cancellationToken);
        }

        public Task<string> SelectModelAsync(RoutingContext context, RoutingSignals? signals,
            CancellationToken cancellationToken = default)
        {
            LastSignals = signals;
            return Task.FromResult(selection);
        }
    }
}