using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Quality;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers request and response interception behavior.
/// </summary>
public class RequestInterceptorTests
{
    // These tests seed RouterMemory directly and never send a "messages" array, so
    // InferLiveDimension's KeywordDimensionInferrer default (empty prompt) always resolves here -
    // matching the dimension RequestInterceptor's fallback ranking will read for these requests.
    private static readonly string DefaultLiveDimension =
        RouterDimension.ToLiveKey(new QualityOptions().LiveMemoryPrefix, RouterDimension.CodeGeneration);

    [Fact]
    public async Task InterceptRequestAsync_IncrementsCount_AndLogsStructuredMessage()
    {
        var loggerMock = new Mock<ILogger<RequestInterceptor>>();
        var interceptor = new RequestInterceptor(loggerMock.Object, ModelRouteResolverTestFactory.Empty());
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.com");
        context.Request.Path = "/test";

        await interceptor.InterceptRequestAsync(context);
        await interceptor.InterceptRequestAsync(context);

        Assert.Equal(2, interceptor.InterceptedRequestCount);
        VerifyLogContains(loggerMock, LogLevel.Information, "[INTERCEPTOR] Intercepting request for");
    }

    [Fact]
    public async Task InterceptResponseAsync_LogsStructuredMessage()
    {
        var loggerMock = new Mock<ILogger<RequestInterceptor>>();
        var interceptor = new RequestInterceptor(loggerMock.Object, ModelRouteResolverTestFactory.Empty());
        var context = new DefaultHttpContext();
        context.Request.Path = "/chat";
        context.Response.StatusCode = StatusCodes.Status200OK;

        await interceptor.InterceptResponseAsync(context);

        VerifyLogContains(loggerMock, LogLevel.Information, "[INTERCEPTOR] Intercepting response for");
    }

    // docs/router/tool-call-normalization.md §3.4 performance rule 1: whether the request offered any
    // tools is the gate on installing normalization at all, so it has to survive onto every candidate the
    // forwarding path might actually attempt - not just the primary. A fallback candidate reaching the
    // upstream with this cleared would silently reproduce the original incident on exactly the requests
    // that already failed over once.
    [Theory]
    [InlineData("""{"model":"gpt-5.4","tools":[{"type":"function","function":{"name":"get_time"}}]}""", true)]
    [InlineData("""{"model":"gpt-5.4"}""", false)]
    [InlineData("""{"model":"gpt-5.4","tools":[]}""", false)]
    public async Task ResolveModelRouteAsync_CarriesToolsIsSetOnEveryCandidate(string body, bool expected)
    {
        // Several models so the candidate list has fallbacks, not just the primary.
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);

        var result = await interceptor.ResolveModelRouteAsync(CreateContextWithBody(body), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Candidates.Count > 1, "expected a fallback candidate, otherwise this only tests the primary");
        Assert.All(result.Candidates, candidate => Assert.Equal(expected, candidate.CarriesTools));
    }

    [Fact]
    public async Task ResolveModelRouteAsync_KnownModel_RewritesBodyToProviderModelId()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"gpt-5.4","temperature":0.7}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("gpt-5.4-2026-01", result.Route!.ProviderModelId);

        using var document = JsonDocument.Parse(result.RewrittenBody!);
        Assert.Equal("gpt-5.4-2026-01", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.7, document.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task ResolveModelRouteAsync_UnknownModel_NoModelsConfigured_ReturnsFailure()
    {
        // The agentic fallback (below) has nothing to fall back to when the allowlist itself is empty, so
        // this is the one case where an unresolved model still fails outside single-model serving.
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), ModelRouteResolverTestFactory.Empty());
        var context = CreateContextWithBody("""{"model":"not-a-known-model"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Route);
        Assert.Contains("not-a-known-model", result.ErrorMessage);
    }

    // docs/router/utility-model-routing.md's generalized fallback: outside single-model serving, an
    // unresolved model is accepted and agentically routed to a real, allowlisted candidate rather than
    // rejected - this is the out-of-the-box path for the spark-vscode-extension's default
    // totallyhot.spark.modelId ("agentic-router"), which is never itself a configured ModelList entry.
    [Fact]
    public async Task ResolveModelRouteAsync_UnknownModel_ModelsConfigured_AgenticallyRoutesToConfiguredModel()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("gpt-5.4", result.Route!.ModelName);
        Assert.Equal("gpt-5.4-2026-01", result.Route!.ProviderModelId);

        using var document = JsonDocument.Parse(result.RewrittenBody!);
        Assert.Equal("gpt-5.4-2026-01", document.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveModelRouteAsync_UnknownModel_RanksByRouterMemoryScore_PicksHighestScoring()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var memory = new RouterMemory();
        await memory.AddScoreAsync(DefaultLiveDimension, "gpt-5.4", 0.2);
        await memory.AddScoreAsync(DefaultLiveDimension, "kimi-k2.5", 0.9);
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver, routerMemory: memory);
        var context = CreateContextWithBody("""{"model":"agentic-router"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("kimi-k2.5", result.Route!.ModelName);
    }

    // PLAN.md Phase G's regression test: a verifier score written for one request must change which
    // model a *later* same-dimension request auto-selects - the exact loop that was severed by the
    // live: vs unresolved-model-fallback key mismatch (RouterMemoryScoreObserver wrote one key,
    // RequestInterceptor read another, so they could never agree). Exercises the real observer and a
    // real prompt-derived dimension end to end, rather than seeding RouterMemory directly.
    [Fact]
    public async Task ResolveModelRouteAsync_ScoreObservedForPriorRequest_ChangesNextSameDimensionAutoSelect()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var memory = new RouterMemory();
        var observer = new RouterMemoryScoreObserver(
            memory,
            Options.Create(new QualityOptions()),
            Mock.Of<ILogger<RouterMemoryScoreObserver>>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver, routerMemory: memory);

        // Request N: the verifier scores a bug-fixing response from kimi-k2.5 highly, off the hot path.
        await observer.ObserveAsync(
            new QualityResult
            {
                Model = "kimi-k2.5",
                Dimension = RouterDimension.BugFixing,
                UnifiedScore = 0.95,
            },
            TestContext.Current.CancellationToken);

        // Request N+1: a new, same-dimension prompt ("fix this bug...") auto-selects.
        var context = CreateContextWithBody(
            """{"model":"auto","messages":[{"role":"user","content":"Please fix this bug in my code."}]}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("kimi-k2.5", result.Route!.ModelName);
    }

    // "model": "auto" is the explicit, documented way to ask the router to choose - it must run the same
    // ranked auto-select the unresolved-model fallback uses, and the name is matched case-insensitively so
    // a client that sends "Auto" or "AUTO" gets the same behavior.
    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public async Task ResolveModelRouteAsync_AutoModel_AnyCasing_AutoSelectsHighestRankedModel(string requestedModel)
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5-upstream"));
        var memory = new RouterMemory();
        await memory.AddScoreAsync(DefaultLiveDimension, "gpt-5.4", 0.2);
        await memory.AddScoreAsync(DefaultLiveDimension, "kimi-k2.5", 0.9);
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver, routerMemory: memory);
        var context = CreateContextWithBody($$"""{"model":"{{requestedModel}}"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("kimi-k2.5", result.Route!.ModelName);

        // The forwarded body must carry the selected model's upstream id, never the literal "auto".
        using var document = JsonDocument.Parse(result.RewrittenBody!);
        Assert.Equal("kimi-k2.5-upstream", document.RootElement.GetProperty("model").GetString());
    }

    // The advertised alias must behave identically to "auto" - it is the name a picker actually sends back,
    // so if it did not auto-select, selecting the router entry would do nothing. Matched case-insensitively
    // because clients are free to normalize the casing of what they advertise back to us.
    [Theory]
    [InlineData("totallyhot-arcrouter")]
    [InlineData("TotallyHot-ArcRouter")]
    [InlineData("TOTALLYHOT-ARCROUTER")]
    public async Task ResolveModelRouteAsync_RouterModel_AnyCasing_AutoSelectsHighestRankedModel(string requestedModel)
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5-upstream"));
        var memory = new RouterMemory();
        await memory.AddScoreAsync(DefaultLiveDimension, "gpt-5.4", 0.2);
        await memory.AddScoreAsync(DefaultLiveDimension, "kimi-k2.5", 0.9);
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver, routerMemory: memory);
        var context = CreateContextWithBody($$"""{"model":"{{requestedModel}}"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("kimi-k2.5", result.Route!.ModelName);

        // The forwarded body must carry the selected model's upstream id - the router name is never a real
        // upstream model, so leaking it through would be rejected by whatever provider received it.
        using var document = JsonDocument.Parse(result.RewrittenBody!);
        Assert.Equal("kimi-k2.5-upstream", document.RootElement.GetProperty("model").GetString());
    }

    // Same allowlist-shadowing guarantee "auto" has: the advertised name is reserved, so an operator who
    // configures a model under it cannot change what selecting the router entry means for every client.
    [Fact]
    public async Task ResolveModelRouteAsync_RouterModel_ConfiguredModelNamedTheSame_StillAutoSelects()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("totallyhot-arcrouter", "openai", "some-literal-model"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var memory = new RouterMemory();
        await memory.AddScoreAsync(DefaultLiveDimension, "totallyhot-arcrouter", 0.2);
        await memory.AddScoreAsync(DefaultLiveDimension, "kimi-k2.5", 0.9);
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver, routerMemory: memory);
        var context = CreateContextWithBody("""{"model":"totallyhot-arcrouter"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("kimi-k2.5", result.Route!.ModelName);
    }

    // The reserved name wins over the allowlist: an operator who happens to configure a model called "auto"
    // must not silently change what "model": "auto" means for every connecting client.
    [Fact]
    public async Task ResolveModelRouteAsync_AutoModel_ConfiguredModelNamedAuto_StillAutoSelects()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("auto", "openai", "some-literal-model"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var memory = new RouterMemory();
        await memory.AddScoreAsync(DefaultLiveDimension, "auto", 0.2);
        await memory.AddScoreAsync(DefaultLiveDimension, "kimi-k2.5", 0.9);
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver, routerMemory: memory);
        var context = CreateContextWithBody("""{"model":"auto"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("kimi-k2.5", result.Route!.ModelName);
    }

    // Auto-select can only pick from currently-eligible models. With none left, there is nothing to select,
    // so the request is rejected with a message naming auto-select rather than an "unknown model" one.
    [Fact]
    public async Task ResolveModelRouteAsync_AutoModel_NoEligibleModel_ReturnsFailure()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["prov-a"] = new ProviderOptions { BaseUrl = "https://api.openai.com", Enabled = false }
            },
            ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "prov-a", ProviderModelId = "gpt-5.4" }]
        };
        var resolver = new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"auto"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("auto-selected", result.ErrorMessage!, StringComparison.Ordinal);
    }

    // Single-model serving still overrides everything the client asks for, "auto" included - a proxy started
    // with --model serves exactly that one model and never auto-selects.
    [Fact]
    public async Task ResolveModelRouteAsync_SingleModelServingForced_AutoModel_ServesForcedModel()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var memory = new RouterMemory();
        await memory.AddScoreAsync(DefaultLiveDimension, "kimi-k2.5", 0.9);
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            new SingleModelServingOptions { ForcedModelName = "gpt-5.4" },
            routerMemory: memory);
        var context = CreateContextWithBody("""{"model":"auto"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Candidates);
        Assert.Equal("gpt-5.4", result.Route!.ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_MissingModelField_ReturnsFailure()
    {
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), ModelRouteResolverTestFactory.Empty());
        var context = CreateContextWithBody("""{"messages":[]}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_MalformedJson_ReturnsFailure()
    {
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), ModelRouteResolverTestFactory.Empty());
        var context = CreateContextWithBody("not-json");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_EmptyBody_ReturnsFailure()
    {
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), ModelRouteResolverTestFactory.Empty());
        var context = CreateContextWithBody(string.Empty);

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"gpt-5.4\"")]
    [InlineData("42")]
    public async Task ResolveModelRouteAsync_NonObjectJsonRoot_ReturnsFailure_WithoutThrowing(string body)
    {
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), ModelRouteResolverTestFactory.Empty());
        var context = CreateContextWithBody(body);

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    // Verifies that ListAvailableModels reports every configured model, in the resolver's own order and
    // otherwise unmodified, so the interceptor stays a thin pass-through for the model-discovery data
    // ProxyMiddleware needs to answer /v1/models. The synthetic router entry is covered separately below.
    [Fact]
    public void ListAvailableModels_ReportsEveryConfiguredModelFromTheResolver()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);

        var models = interceptor.ListAvailableModels();

        Assert.Equal(resolver.ListModels(), models.Where(m => m.ModelName != "totallyhot-arcrouter"));
    }

    // Editors that attach to this proxy as a provider force the user to pick one name from the discovery
    // response, so "let the router choose" is only reachable if it is itself listed. It leads so it reads
    // as the default in the picker.
    [Fact]
    public void ListAvailableModels_ModelsConfigured_ListsTheRouterEntryFirst()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);

        var models = interceptor.ListAvailableModels();

        Assert.Equal("totallyhot-arcrouter", models[0].ModelName);
        Assert.Equal(3, models.Count);
    }

    // With nothing configured to route to, selecting the router entry could only ever fail - so it must not
    // be offered. Advertising it here would put an entry in the picker that errors on first use.
    [Fact]
    public void ListAvailableModels_NoModelsConfigured_OmitsTheRouterEntry()
    {
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            ModelRouteResolverTestFactory.Empty());

        var models = interceptor.ListAvailableModels();

        Assert.Empty(models);
    }

    // Single-model serving bypasses auto-select entirely (--model always wins), so the router entry would
    // be a lie: picking it would silently serve the forced model instead of choosing.
    [Fact]
    public void ListAvailableModels_SingleModelServingForced_OmitsTheRouterEntry()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            new SingleModelServingOptions { ForcedModelName = "gpt-5.4" });

        var models = interceptor.ListAvailableModels();

        Assert.DoesNotContain(models, m => m.ModelName == "totallyhot-arcrouter");
    }

    // Local Proxy CLI: an invalid --model CLI value must fail at startup
    // (constructor time), not silently on the first request.
    [Fact]
    public void Constructor_ForcedModelNotConfigured_ThrowsInvalidOperationException()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new RequestInterceptor(
                Mock.Of<ILogger<RequestInterceptor>>(),
                resolver,
                new SingleModelServingOptions { ForcedModelName = "not-a-configured-model" }));

        Assert.Contains("not-a-configured-model", ex.Message, StringComparison.Ordinal);
        Assert.Contains("gpt-5.4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ForcedModelIsConfigured_DoesNotThrow()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"));

        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            new SingleModelServingOptions { ForcedModelName = "gpt-5.4" });

        Assert.NotNull(interceptor);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_SingleModelServingForced_OverridesClientRequestedModel()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4-2026-01", "https://api.openai.com");
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            new SingleModelServingOptions { ForcedModelName = "gpt-5.4" });
        var context = CreateContextWithBody("""{"model":"whatever-the-client-thinks-it-wants"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("gpt-5.4-2026-01", result.Route!.ProviderModelId);

        using var document = JsonDocument.Parse(result.RewrittenBody!);
        Assert.Equal("gpt-5.4-2026-01", document.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ListAvailableModels_SingleModelServingForced_ReturnsOnlyTheForcedModel()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            new SingleModelServingOptions { ForcedModelName = "gpt-5.4" });

        var models = interceptor.ListAvailableModels();

        var model = Assert.Single(models);
        Assert.Equal("gpt-5.4", model.ModelName);
    }

    // docs/router/agent-resilience-strategies.md's Circuit Breaker replaced the old static per-model
    // Fallbacks list: every other configured model is now automatically a dynamic next-best candidate, so
    // no explicit backup declaration is needed for this to work.
    [Fact]
    public async Task ResolveModelRouteAsync_OtherModelsConfigured_BuildsOrderedCandidates_EachRewrittenToItsOwnUpstreamId()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", "https://a.example.com"),
            ("backup", "prov-b", "backup-upstream", "https://b.example.com"));
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"primary"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Candidates.Count);

        Assert.Equal("prov-a", result.Candidates[0].Route.Provider);
        Assert.Equal("prov-b", result.Candidates[1].Route.Provider);

        using var primaryBody = JsonDocument.Parse(result.Candidates[0].RewrittenBody);
        Assert.Equal("primary-upstream", primaryBody.RootElement.GetProperty("model").GetString());
        using var backupBody = JsonDocument.Parse(result.Candidates[1].RewrittenBody);
        Assert.Equal("backup-upstream", backupBody.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveModelRouteAsync_OtherModelOnDifferentProviderSharingModelId_IsKept()
    {
        // Two distinct providers legitimately share the same ProviderModelId string; the candidate dedup
        // keys on the full upstream target (provider + base URL + model id), so the backup is NOT dropped.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "shared-id", "https://a.example.com"),
            ("backup", "prov-b", "shared-id", "https://b.example.com"));
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"primary"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("prov-b", result.Candidates[1].Route.Provider);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_OnlyConfiguredModel_YieldsSingleCandidate()
    {
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4", "https://api.openai.com");
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"gpt-5.4"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_SingleModelServingForced_IgnoresOtherConfiguredModels()
    {
        // --model force-routes to exactly one model, overriding even the client's choice; substituting or
        // cascading to a different model would contradict that contract, so forced serving yields exactly
        // one candidate regardless of what else is configured, and ignores circuit-breaker state entirely.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", "https://a.example.com"),
            ("backup", "prov-b", "backup-upstream", "https://b.example.com"));
        var interceptor = new RequestInterceptor(
            Mock.Of<ILogger<RequestInterceptor>>(),
            resolver,
            new SingleModelServingOptions { ForcedModelName = "primary" });
        var context = CreateContextWithBody("""{"model":"primary"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Candidates);
        Assert.Equal("prov-a", result.Candidates[0].Route.Provider);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_PrimaryCircuitOpen_SubstitutesNextBestModel()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", "https://a.example.com"),
            ("backup", "prov-b", "backup-upstream", "https://b.example.com"));
        var circuitBreaker = new CircuitBreaker();
        var primaryTarget = new CircuitBreakerTargetKey("prov-a", "https://a.example.com/", "primary-upstream");
        for (var i = 0; i < 3; i++)
        {
            circuitBreaker.RecordFailure(primaryTarget); // default FailureThreshold is 3
        }
        Assert.True(circuitBreaker.IsOpen(primaryTarget));

        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver, circuitBreaker: circuitBreaker);
        var context = CreateContextWithBody("""{"model":"primary"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        // The open primary is swapped out entirely - it must not appear anywhere in the candidate list.
        Assert.Equal("backup", result.Candidates[0].Route.ModelName);
        Assert.DoesNotContain(result.Candidates, c => c.Route.ModelName == "primary");
    }

    [Fact]
    public async Task ResolveModelRouteAsync_EveryCandidateCircuitOpen_KeepsOriginalRouteAnyway()
    {
        // "Fail open" when there is truly no eligible substitute: ProxyMiddleware's own real-time bypass
        // check will then fail fast with a 502 without ever making the network call.
        var resolver = ModelRouteResolverTestFactory.Create("gpt-5.4", "gpt-5.4", "https://api.openai.com");
        var circuitBreaker = new CircuitBreaker();
        var target = new CircuitBreakerTargetKey("test-provider", "https://api.openai.com/", "gpt-5.4");
        for (var i = 0; i < 3; i++)
        {
            circuitBreaker.RecordFailure(target);
        }

        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver, circuitBreaker: circuitBreaker);
        var context = CreateContextWithBody("""{"model":"gpt-5.4"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Candidates);
        Assert.Equal("gpt-5.4", result.Candidates[0].Route.ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_PrimaryProviderDisabled_SubstitutesNextBestModel()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["prov-a"] = new ProviderOptions { BaseUrl = "https://a.example.com", Enabled = false },
                ["prov-b"] = new ProviderOptions { BaseUrl = "https://b.example.com" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "primary", Provider = "prov-a", ProviderModelId = "primary-upstream" },
                new ModelRouteEntry { ModelName = "backup", Provider = "prov-b", ProviderModelId = "backup-upstream" }
            ]
        };
        var resolver = new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"primary"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        // The stopped primary is swapped out entirely - it must not appear anywhere in the candidate list.
        Assert.Equal("backup", result.Candidates[0].Route.ModelName);
        Assert.DoesNotContain(result.Candidates, c => c.Route.ModelName == "primary");
    }

    [Fact]
    public async Task ResolveModelRouteAsync_EveryCandidateProviderDisabled_KeepsOriginalRouteAnyway()
    {
        // "Fail open" when there is truly no eligible substitute, exactly like the all-circuit-open case:
        // ProxyMiddleware's own real-time gate will then reject it (503, "provider is stopped") without
        // ever making the network call.
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["prov-a"] = new ProviderOptions { BaseUrl = "https://api.openai.com", Enabled = false }
            },
            ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "prov-a", ProviderModelId = "gpt-5.4" }]
        };
        var resolver = new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"gpt-5.4"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Candidates);
        Assert.Equal("gpt-5.4", result.Candidates[0].Route.ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_UnresolvedModel_EveryConfiguredProviderDisabled_RejectsRequest()
    {
        // An unresolved model name falls back to TryAgenticallyRouteUnresolvedModel, which delegates to
        // RankEligibleModels - if every configured candidate's provider is disabled, none is eligible, so
        // there is nothing to agentically route to and the request is rejected outright.
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["prov-a"] = new ProviderOptions { BaseUrl = "https://api.openai.com", Enabled = false }
            },
            ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "prov-a", ProviderModelId = "gpt-5.4" }]
        };
        var resolver = new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"unknown-model"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
    }

    // The model-level twin of the three provider-disabled tests above, via the new gate at the explicit-model
    // resolve site (ResolveModelRouteAsync's "else if (!TryResolve(...) || !IsModelEnabled(...))"): a model
    // that resolves but is stopped/not-currently-upstream is treated exactly like an unresolved name.
    //
    // Note this is NOT a literal 1:1 mirror of ResolveModelRouteAsync_EveryCandidateProviderDisabled_
    // KeepsOriginalRouteAnyway: that "fail open, let ProxyMiddleware reject later" outcome only exists for
    // providers because IsProviderEnabled is checked *only* in the later substitution block, after TryResolve
    // has already succeeded. IsModelEnabled is checked earlier, at the same site as TryResolve itself, so a
    // disabled/absent model is redirected (or rejected, if nothing else is eligible) immediately - there is
    // no "keeps the original disabled route anyway" case to test for models.
    [Fact]
    public async Task ResolveModelRouteAsync_RequestedModelDisabled_AgenticallyRoutesToNextBestModel()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["prov-a"] = new ProviderOptions { BaseUrl = "https://a.example.com" },
                ["prov-b"] = new ProviderOptions { BaseUrl = "https://b.example.com" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "primary", Provider = "prov-a", ProviderModelId = "primary-upstream", Enabled = false },
                new ModelRouteEntry { ModelName = "backup", Provider = "prov-b", ProviderModelId = "backup-upstream" }
            ]
        };
        var resolver = new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"primary"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("backup", result.Candidates[0].Route.ModelName);
        Assert.DoesNotContain(result.Candidates, c => c.Route.ModelName == "primary");
    }

    [Fact]
    public async Task ResolveModelRouteAsync_RequestedModelNotPresentUpstream_AgenticallyRoutesToNextBestModel()
    {
        // Same as above, via PresentUpstream (a "Refresh from endpoint" scan no longer reporting the model)
        // rather than Enabled (an operator's own Start/Stop toggle) - both gate IsModelEnabled identically.
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["prov-a"] = new ProviderOptions { BaseUrl = "https://a.example.com" },
                ["prov-b"] = new ProviderOptions { BaseUrl = "https://b.example.com" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "primary", Provider = "prov-a", ProviderModelId = "primary-upstream", PresentUpstream = false },
                new ModelRouteEntry { ModelName = "backup", Provider = "prov-b", ProviderModelId = "backup-upstream" }
            ]
        };
        var resolver = new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"primary"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("backup", result.Candidates[0].Route.ModelName);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_OnlyConfiguredModelDisabled_RejectsRequest()
    {
        // Nothing to agentically reroute to - the sole candidate is itself disabled - so this fails
        // immediately, the same "allowlist has nothing eligible" outcome as an empty configuration.
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["prov-a"] = new ProviderOptions { BaseUrl = "https://api.openai.com" }
            },
            ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "prov-a", ProviderModelId = "gpt-5.4", Enabled = false }]
        };
        var resolver = new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"gpt-5.4"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ResolveModelRouteAsync_UnresolvedModel_DisabledCandidateNeverOffered_RoutesToEnabledOneInstead()
    {
        // Exercises RankEligibleModels' own model-level filter directly (distinct from the explicit-resolve
        // gate above): an unresolved name's agentic fallback must skip a disabled candidate on its own,
        // not merely happen to avoid it.
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["prov-a"] = new ProviderOptions { BaseUrl = "https://a.example.com" },
                ["prov-b"] = new ProviderOptions { BaseUrl = "https://b.example.com" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "stopped", Provider = "prov-a", ProviderModelId = "stopped-upstream", Enabled = false },
                new ModelRouteEntry { ModelName = "running", Provider = "prov-b", ProviderModelId = "running-upstream" }
            ]
        };
        var resolver = new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var context = CreateContextWithBody("""{"model":"totally-unknown-name"}""");

        var result = await interceptor.ResolveModelRouteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.All(result.Candidates, c => Assert.NotEqual("stopped", c.Route.ModelName));
        Assert.Contains(result.Candidates, c => c.Route.ModelName == "running");
    }

    private static DefaultHttpContext CreateContextWithBody(string body)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private static void VerifyLogContains(Mock<ILogger<RequestInterceptor>> loggerMock, LogLevel level, string expectedText)
    {
        loggerMock.Verify(
            logger => logger.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(expectedText, StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}

