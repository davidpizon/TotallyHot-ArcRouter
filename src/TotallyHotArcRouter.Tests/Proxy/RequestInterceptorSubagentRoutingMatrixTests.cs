using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Extraction;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Issue #163's Phase 4 test matrix, run end to end: a real <see cref="RequestInterceptor"/> over the real
/// <see cref="CompositeRoutingPolicy"/>, <see cref="UtilityRoutingPolicy"/> and <see cref="RouterMemory"/>, with a
/// price catalog and an Orchestrator that always votes for the expensive model. The neighbouring
/// <see cref="RequestInterceptorSubagentBiasTests"/> pins what context the interceptor hands a stub policy; these
/// tests pin which model a request actually resolves to, which is where the cost outcome matters. The fixture is
/// two models, <c>cheap</c> and <c>pricey</c>: with no bias the expensive one wins, so any request that ends on
/// <c>cheap</c> got there through the helper or light-subagent rule.
/// </summary>
public sealed class RequestInterceptorSubagentRoutingMatrixTests
{
    private const string Cheap = "cheap";
    private const string Pricey = "pricey";

    /// <summary>A long coding prompt, so the payload heuristic never reads the request as a helper task itself.</summary>
    private const string CodingPrompt =
        "Refactor the billing module and add tests for the retry path so that it handles every timeout case we discussed earlier today in the planning meeting.";

    private static readonly (string, string) AgentId = ("x-claude-code-agent-id", "agent-1");
    private static readonly (string, string) AuxiliaryClass = ("x-claude-code-request-class", "auxiliary");
    private static readonly (string, string) SubagentClass = ("x-claude-code-request-class", "subagent");
    private static readonly (string, string) ExploreType = ("x-claude-code-agent-type", "Explore");
    private static readonly (string, string) GeneralPurposeType = ("x-claude-code-agent-type", "general-purpose");

    private static readonly (string, string)[] ExploreHeaders = [SubagentClass, AgentId, ExploreType];
    private static readonly (string, string)[] GeneralPurposeHeaders = [SubagentClass, AgentId, GeneralPurposeType];
    private static readonly (string, string)[] HelperHeaders = [AuxiliaryClass];
    private static readonly (string, string)[] NoHeaders = [];

    private static string AutoBody => BodyFor("auto");

    private static string BodyFor(string model)
    {
        return $$"""{"model":"{{model}}","messages":[{"role":"user","content":"{{CodingPrompt}}"}]}""";
    }

    /// <summary>
    /// The memory dimension a <see cref="CodingPrompt"/> request is routed under, derived with the same classifier
    /// and prefix the interceptor uses, so seeded scores land where the policies read them.
    /// </summary>
    private static string LiveDimension()
    {
        var classification = new HeuristicRequestClassifier(new KeywordDimensionInferrer()).Classify(
            (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(AutoBody)!);
        return RouterDimension.ToLiveKey(liveMemoryPrefix: new QualityOptions().LiveMemoryPrefix,
            dimension: classification.Dimension);
    }

    private sealed record Fixture(RequestInterceptor Interceptor, IModelRouteResolver Resolver, CircuitBreaker Circuit);

    /// <summary>
    /// Builds the fixture. <paramref name="cheapScore"/> and <paramref name="priceyScore"/> seed the quality memory
    /// (a <see langword="null"/> leaves that model unscored); <paramref name="cheapPrice"/> is the cheap model's price
    /// per million tokens, the expensive one costing 10.
    /// </summary>
    private static async Task<Fixture> BuildAsync(double? cheapScore = null, double? priceyScore = null,
        decimal cheapPrice = 1m)
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            (Cheap, "openai", Cheap),
            (Pricey, "openai", Pricey));

        var memory = new RouterMemory();
        var dimension = LiveDimension();
        if (cheapScore is { } cs) await memory.AddScoreAsync(dimension: dimension, model: Cheap, cs);
        if (priceyScore is { } ps) await memory.AddScoreAsync(dimension: dimension, model: Pricey, ps);

        var catalog = new FixedPriceCatalog();
        catalog.SetPrice(modelName: Cheap, provider: "openai", cheapPrice, cheapPrice);
        catalog.SetPrice(modelName: Pricey, provider: "openai", 10m, 10m);

        var routingOptions = new RoutingOptions { EnableExploration = false, ExplorationRate = 0 };
        var utilityPolicy = new UtilityRoutingPolicy(priceCatalog: catalog, memory: memory,
            options: Options.Create(routingOptions), logger: NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            logger: NullLogger<AgentAsARouter>.Instance, options: Options.Create(routingOptions), memory: memory));
        var orchestrator = new OrchestratorRoutingPolicy(
            voters: [new AlwaysVotesFor(name: VoterNames.DimBest, modelName: Pricey)],
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(routingOptions),
            logger: NullLogger<OrchestratorRoutingPolicy>.Instance);
        var composite = new CompositeRoutingPolicy(utilityPolicy: utilityPolicy, generalPolicy: generalPolicy,
            orchestratorPolicy: orchestrator, options: Options.Create(routingOptions));

        var circuit = new CircuitBreaker();
        var interceptor = new RequestInterceptor(
            logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver,
            routerMemory: memory,
            circuitBreaker: circuit,
            routingPolicy: composite);

        return new Fixture(Interceptor: interceptor, Resolver: resolver, Circuit: circuit);
    }

    private static Task<ModelRouteResolutionResult> ResolveAsync(Fixture fixture, string body,
        params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        foreach (var (name, value) in headers) context.Request.Headers.Append(key: name, value: value);

        return fixture.Interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    // ---- Helper ----

    [Fact]
    public async Task Helper_WithAPricedCheapAndAnExpensiveCandidate_PicksTheCheapOne()
    {
        var fixture = await BuildAsync();

        var result = await ResolveAsync(fixture, AutoBody, HelperHeaders);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: Cheap, actual: result.Route!.ModelName);
    }

    [Fact]
    public async Task Helper_TheSameRequestWithoutTheMarker_RoutesAsBefore()
    {
        var fixture = await BuildAsync();

        var result = await ResolveAsync(fixture, AutoBody, NoHeaders);

        Assert.Equal(expected: Pricey, actual: result.Route!.ModelName);
    }

    // ---- Light subagent (Explore) ----

    [Fact]
    public async Task Explore_FreeCandidateWithinTheFloor_IsChosen()
    {
        // Best is 0.9, so the floor is 0.81 and the $0 model at 0.85 clears it.
        var fixture = await BuildAsync(cheapScore: 0.85, priceyScore: 0.9, cheapPrice: 0m);

        var result = await ResolveAsync(fixture, AutoBody, ExploreHeaders);

        Assert.Equal(expected: Cheap, actual: result.Route!.ModelName);
    }

    [Fact]
    public async Task Explore_FreeCandidateBelowTheFloor_IsExcludedAndTheNearBestOneChosen()
    {
        var fixture = await BuildAsync(cheapScore: 0.7, priceyScore: 0.9, cheapPrice: 0m);

        var result = await ResolveAsync(fixture, AutoBody, ExploreHeaders);

        Assert.Equal(expected: Pricey, actual: result.Route!.ModelName);
        // The Orchestrator also ends on pricey (its voter always picks it), and so does the memory fallback. Only
        // the Orchestrator leg records a voter pick, so a null here shows the near-best leg itself chose pricey.
        Assert.Null(result.DimBestModel);
    }

    [Fact]
    public async Task Explore_WithNoKnownScores_RoutesAsWithoutASignal()
    {
        var fixture = await BuildAsync(cheapPrice: 0m);

        var unbiased = await ResolveAsync(fixture, AutoBody, NoHeaders);
        var result = await ResolveAsync(fixture, AutoBody, ExploreHeaders);

        Assert.Equal(expected: unbiased.Route!.ModelName, actual: result.Route!.ModelName);
        Assert.Equal(expected: Pricey, actual: result.Route.ModelName);
    }

    // ---- Other subagents ----

    [Fact]
    public async Task GeneralPurposeSubagent_SelectsExactlyWhatTheSameRequestWithoutASignalSelects()
    {
        // A fixture where the bias would pick the cheap model if it applied: the cheap model is within the floor.
        var fixture = await BuildAsync(cheapScore: 0.85, priceyScore: 0.9, cheapPrice: 0m);

        var unbiased = await ResolveAsync(fixture, AutoBody, NoHeaders);
        var result = await ResolveAsync(fixture, AutoBody, GeneralPurposeHeaders);

        Assert.Equal(expected: unbiased.Route!.ModelName, actual: result.Route!.ModelName);
        Assert.Equal(expected: Pricey, actual: result.Route.ModelName);
        // Detected but not acted on: without this, a detector that stopped recognising the headers would pass too.
        Assert.Equal(expected: "claude-code/general-purpose", actual: result.Classification!.Subagent?.ToLabel());
    }

    // ---- Allowlist and circuit state ----

    [Theory]
    [InlineData("helper")]
    [InlineData("explore")]
    public async Task SignalledRequest_NeverResolvesToAModelWhoseCircuitIsOpen(string kind)
    {
        var fixture = await BuildAsync(cheapScore: 0.85, priceyScore: 0.9, cheapPrice: 0m);
        Assert.True(fixture.Resolver.TryResolve(modelName: Cheap, route: out var cheapRoute));
        var target = CircuitBreakerTargetKey.FromRoute(cheapRoute!);
        for (var i = 0; i < new CircuitBreakerOptions().FailureThreshold; i++) fixture.Circuit.RecordFailure(target);
        Assert.True(fixture.Circuit.IsOpen(target));

        var result = await ResolveAsync(fixture, AutoBody, kind == "helper" ? HelperHeaders : ExploreHeaders);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: Pricey, actual: result.Route!.ModelName);
        Assert.DoesNotContain(collection: result.Candidates, filter: c => c.Route.ModelName == Cheap);
        // AutoSelect, not CircuitOpen: RoutingCandidateBuilder swaps a circuit-open pick for the next model after
        // the policy ran, so CircuitOpen would mean the policy was offered the open model and chose it.
        Assert.Equal(expected: RoutingSubstitutionReason.AutoSelect, actual: result.SubstitutionReason);
    }

    [Theory]
    [InlineData("helper")]
    [InlineData("explore")]
    public async Task SignalledRequest_AlwaysResolvesWithinTheConfiguredModels(string kind)
    {
        var fixture = await BuildAsync(cheapScore: 0.85, priceyScore: 0.9, cheapPrice: 0m);

        var result = await ResolveAsync(fixture, AutoBody, kind == "helper" ? HelperHeaders : ExploreHeaders);

        var configured = fixture.Resolver.ListModels().Select(m => m.ModelName).ToList();
        Assert.True(result.IsSuccess);
        Assert.Contains(result.Route!.ModelName, configured);
        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, c => Assert.Contains(c.Route.ModelName, configured));
    }

    // ---- Explicit model pick (ADR-0005) ----

    [Theory]
    [InlineData("helper")]
    [InlineData("explore")]
    public async Task ExplicitModelPick_WithASignal_KeepsItsModel(string kind)
    {
        var fixture = await BuildAsync(cheapScore: 0.85, priceyScore: 0.9, cheapPrice: 0m);

        var result = await ResolveAsync(fixture, BodyFor(Pricey), kind == "helper" ? HelperHeaders : ExploreHeaders);

        Assert.Equal(expected: Pricey, actual: result.Route!.ModelName);
        Assert.Equal(expected: RoutingSubstitutionReason.None, actual: result.SubstitutionReason);
    }

    // ---- Copilot aliases ----

    [Theory]
    [InlineData("copilot-utility")]
    [InlineData("copilot-utility-small")]
    [InlineData("Copilot-Utility-Small")]
    [InlineData("copilot-utility-tiny")]
    public async Task CopilotUtilityAlias_GetsTheHelperBias(string alias)
    {
        var fixture = await BuildAsync();

        var result = await ResolveAsync(fixture, BodyFor(alias));

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: Cheap, actual: result.Route!.ModelName);
        Assert.Equal(expected: "copilot/utility-alias", actual: result.Classification!.Subagent!.ToLabel());
    }

    [Theory]
    [InlineData("copilot-utilit")]
    [InlineData("my-copilot-utility")]
    public async Task NearMissOfACopilotAlias_GetsNoBias(string name)
    {
        var fixture = await BuildAsync();

        var result = await ResolveAsync(fixture, BodyFor(name));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Classification!.Subagent);
        Assert.Equal(expected: Pricey, actual: result.Route!.ModelName);
    }

    /// <summary>A price catalog with fixed, never-stale prices.</summary>
    private sealed class FixedPriceCatalog : IModelPriceCatalog
    {
        private readonly Dictionary<ModelKey, ModelPrice> _prices = [];

        public ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context)
        {
            return _prices.GetValueOrDefault(key);
        }

        public ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge)
        {
            return _prices.GetValueOrDefault(key);
        }

        public void Invalidate()
        {
        }

        public void SetPrice(string modelName, string provider, decimal input, decimal output)
        {
            _prices[new ModelKey(ModelName: modelName, Provider: provider)] =
                new ModelPrice(InputPerMillionTokens: input, OutputPerMillionTokens: output);
        }
    }

    /// <summary>An Orchestrator voter that always picks the same model, so a request the bias skips ends there.</summary>
    private sealed class AlwaysVotesFor(string name, string modelName) : IRoutingVoter
    {
        public string Name { get; } = name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new VoterVote(VoterName: Name, ModelName: modelName, 0.9));
        }
    }
}
