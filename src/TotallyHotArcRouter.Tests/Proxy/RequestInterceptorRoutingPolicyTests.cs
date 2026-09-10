using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

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

    // Regression coverage for the request-path wiring of UntrainedBaselineModel (docs/router/routing-roi-
    // regret-plan.md's frozen-baseline correction): a wiring bug here (e.g. the live-prefixed dimension key
    // reaching the selector instead of the bare one) would leave every persisted transcript's baseline null
    // while UntrainedBaselineSelectorTests and TaxonomyComparisonServiceTests - which construct the
    // selector/service directly rather than going through ResolveModelRouteAsync - stay green.
    [Fact]
    public async Task ResolveModelRouteAsync_WithRoutingPolicy_PropagatesUntrainedBaselineModel()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertProbingRow(database: temp.Database, taskId: "task-1", dimension: RouterDimension.CodeGeneration,
            model: "gpt-5.4", 0.2);
        InsertProbingRow(database: temp.Database, taskId: "task-2", dimension: RouterDimension.CodeGeneration,
            model: "kimi-k2.5", 0.8);
        var selector = new UntrainedBaselineSelector(database: temp.Database,
            logger: NullLogger<UntrainedBaselineSelector>.Instance);
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var policy = new FakeRoutingPolicy("kimi-k2.5");
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            routingPolicy: policy,
            untrainedBaselineSelector: selector);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        var result = await interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        // The policy's own pick ("kimi-k2.5") happens to coincide with the untrained baseline's pick here;
        // the two fields are independently sourced (see ModelRouteResolutionResult's remarks), asserted
        // separately so a regression that collapsed one into the other would still be caught.
        Assert.Equal(expected: "kimi-k2.5", actual: result.Route!.ModelName);
        Assert.Equal(expected: "kimi-k2.5", actual: result.UntrainedBaselineModel);
        // The predicted score travels with the model, read from the same prior snapshot it was picked
        // from - docs/router/routing-roi-regret-plan.md's frozen-baseline correction, second pass.
        Assert.Equal(0.8, actual: result.UntrainedBaselinePredictedScore);
    }

    // Mirrors ResolveModelRouteAsync_WithRoutingPolicy_PropagatesUntrainedBaselineModel for the
    // memory-ranking fallback branch (RequestInterceptor.ResolveAgenticRouteAsync's tail, reached when no
    // policy is configured), which threads UntrainedBaselineModel through a separate call site.
    [Fact]
    public async Task ResolveModelRouteAsync_MemoryFallback_PropagatesUntrainedBaselineModel()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertProbingRow(database: temp.Database, taskId: "task-1", dimension: RouterDimension.CodeGeneration,
            model: "gpt-5.4", 0.9);
        InsertProbingRow(database: temp.Database, taskId: "task-2", dimension: RouterDimension.CodeGeneration,
            model: "kimi-k2.5", 0.1);
        var selector = new UntrainedBaselineSelector(database: temp.Database,
            logger: NullLogger<UntrainedBaselineSelector>.Instance);
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            untrainedBaselineSelector: selector);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        var result = await interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "gpt-5.4", actual: result.UntrainedBaselineModel);
        Assert.Equal(0.9, actual: result.UntrainedBaselinePredictedScore);
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

    private static void InsertProbingRow(BenchmarkDatabase database, string taskId, string dimension, string model,
        double score)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_id_results (task_id, split, source_split, dimension, model, score)
                              VALUES ($taskId, 'probing', 'probing', $dimension, $model, $score);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$score", value: score);
        command.ExecuteNonQuery();
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