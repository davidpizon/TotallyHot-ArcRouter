using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using static TotallyHot.ArcRouter.Tests.Proxy.ModelFeatureSupportFixtures;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers which candidates <see cref="RequestInterceptor.ResolveModelRouteAsync"/> strips
/// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1): every router-chosen
/// candidate on <c>/v1/messages</c>, never an explicit pick's own first attempt, never another path - and a failover
/// candidate always starts from the unstripped body (ADR-0017 Strip rule 2).
/// </summary>
public sealed class RequestInterceptorFeatureStripTests
{
    private const string Haiku = "claude-haiku";
    private const string Sonnet = "claude-sonnet";

    private static (RequestInterceptor Interceptor, FakeModelFeatureSupportStore Store) Build(string policyPick,
        bool withStore = true)
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            (Haiku, "anthropic", "claude-haiku-4-5-20251001"),
            (Sonnet, "anthropic", "claude-sonnet-5"));
        var store = new FakeModelFeatureSupportStore().With(Haiku45(), Capable("claude-sonnet-5"));
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver, routingPolicy: new FixedPolicy(policyPick),
            modelFeatureSupportStore: withStore ? store : null);
        return (interceptor, store);
    }

    private static Task<ModelRouteResolutionResult> Resolve(RequestInterceptor interceptor, string path, string body)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.Path = path;
        return interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string WithModel(string body, string model)
    {
        var node = (JsonObject)JsonNode.Parse(body)!;
        node["model"] = model;
        return node.ToJsonString();
    }

    private static JsonObject BodyOf(RouteCandidate candidate)
    {
        return (JsonObject)JsonNode.Parse(candidate.RewrittenBody)!;
    }

    [Fact]
    public async Task AutoPickOfHaiku_StripsThePrimary_AndTheCapableFallbackGetsTheFullBody()
    {
        var (interceptor, _) = Build(policyPick: Haiku);

        var result = await Resolve(interceptor, "/v1/messages", ExploreBody);

        Assert.True(result.IsSuccess);
        var primary = result.Candidates[0];
        Assert.Equal(expected: Haiku, actual: primary.Route.ModelName);
        Assert.Contains(expected: "thinking.adaptive", collection: primary.FeatureStrip.Features);
        Assert.Null(BodyOf(primary)["thinking"]);
        Assert.Equal(expected: "claude-haiku-4-5-20251001", actual: BodyOf(primary)["model"]!.GetValue<string>());

        var fallback = Assert.Single(result.Candidates.Skip(1));
        Assert.Equal(expected: Sonnet, actual: fallback.Route.ModelName);
        Assert.True(fallback.FeatureStrip.IsEmpty);
        Assert.Equal(expected: "adaptive", actual: BodyOf(fallback)["thinking"]!["type"]!.GetValue<string>());
        Assert.NotNull(BodyOf(fallback)["output_config"]);
        Assert.Equal(expected: "claude-sonnet-5", actual: BodyOf(fallback)["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task AutoPickOfACapableModel_StripsNothing_ButAHaikuFallbackIsStripped()
    {
        var (interceptor, _) = Build(policyPick: Sonnet);

        var result = await Resolve(interceptor, "/v1/messages", ExploreBody);

        Assert.True(result.Candidates[0].FeatureStrip.IsEmpty);
        Assert.NotNull(BodyOf(result.Candidates[0])["thinking"]);

        var fallback = result.Candidates[1];
        Assert.Equal(expected: Haiku, actual: fallback.Route.ModelName);
        Assert.Null(BodyOf(fallback)["thinking"]);
        Assert.Contains(expected: "output_config.effort", collection: fallback.FeatureStrip.Features);
    }

    [Fact]
    public async Task ExplicitPickOfHaiku_IsSentAsReceived()
    {
        var (interceptor, _) = Build(policyPick: Sonnet);

        var result = await Resolve(interceptor, "/v1/messages", WithModel(ExploreBody, Haiku));

        var primary = result.Candidates[0];
        Assert.Equal(expected: Haiku, actual: primary.Route.ModelName);
        Assert.True(primary.FeatureStrip.IsEmpty);
        Assert.Equal(expected: "adaptive", actual: BodyOf(primary)["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitPickOfACapableModel_StillStripsAHaikuFallback()
    {
        var (interceptor, _) = Build(policyPick: Sonnet);

        var result = await Resolve(interceptor, "/v1/messages", WithModel(ExploreBody, Sonnet));

        Assert.True(result.Candidates[0].FeatureStrip.IsEmpty);
        Assert.False(result.Candidates[1].FeatureStrip.IsEmpty);
    }

    [Fact]
    public async Task AnotherPath_IsNeverStripped_AndNeverConsultsTheStore()
    {
        var (interceptor, store) = Build(policyPick: Haiku);

        var result = await Resolve(interceptor, "/v1/chat/completions", ExploreBody);

        Assert.All(result.Candidates, candidate => Assert.True(candidate.FeatureStrip.IsEmpty));
        Assert.Equal(0, actual: store.Lookups);
        Assert.NotNull(BodyOf(result.Candidates[0])["thinking"]);
    }

    [Fact]
    public async Task NoStoreWired_StripsNothing()
    {
        var (interceptor, _) = Build(policyPick: Haiku, withStore: false);

        var result = await Resolve(interceptor, "/v1/messages", ExploreBody);

        Assert.All(result.Candidates, candidate => Assert.True(candidate.FeatureStrip.IsEmpty));
        Assert.NotNull(BodyOf(result.Candidates[0])["thinking"]);
    }

    [Fact]
    public async Task HelperShape_OnHaiku_DropsOnlyEffort()
    {
        var (interceptor, _) = Build(policyPick: Haiku);

        var result = await Resolve(interceptor, "/v1/messages", HelperBody);

        Assert.Equal(expected: ["output_config.effort"], actual: result.Candidates[0].FeatureStrip.Features);
        Assert.NotNull(result.Candidates[0].UnstrippedBody);
    }

    private sealed class FixedPolicy(string selection) : IRoutingPolicy
    {
        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(selection);
        }
    }
}
