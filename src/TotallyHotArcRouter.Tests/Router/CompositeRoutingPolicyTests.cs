using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="AgentRouterPolicy"/> and <see cref="CompositeRoutingPolicy"/>'s dispatch between the
/// utility policy, the Orchestrator (docs/router/orchestrator-live-path-plan.md M1's default), and
/// <see cref="AgentRouterPolicy"/> behind <see cref="RoutingOptions.EnableOrchestratorPolicy"/>'s kill
/// switch.
/// </summary>
public class CompositeRoutingPolicyTests
{
    [Fact]
    public async Task AgentRouterPolicy_DelegatesToAgentAsARouterSelection()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: "live:code_generation", model: "model-a", 0.4);
        await memory.AddScoreAsync(dimension: "live:code_generation", model: "model-b", 0.9);
        var router = new AgentAsARouter(
            logger: NullLogger<AgentAsARouter>.Instance,
            options: Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            memory: memory);
        var policy = new AgentRouterPolicy(router);
        var context = new RoutingContext(
            Dimension: "live:code_generation",
            false,
            Candidates:
            [
                new RoutingCandidate(ModelName: "model-a", Provider: "openai", false),
                new RoutingCandidate(ModelName: "model-b", Provider: "openai", false)
            ]);

        var selected =
            await policy.SelectModelAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "model-b", actual: selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_UtilityContext_DispatchesToUtilityPolicy()
    {
        var catalog = new PassthroughPriceCatalog();
        catalog.SetPrice(modelName: "cheap", provider: "openai", 1m, 1m);
        catalog.SetPrice(modelName: "pricey", provider: "openai", 50m, 50m);
        var utilityPolicy = new UtilityRoutingPolicy(priceCatalog: catalog, memory: new RouterMemory(),
            options: Options.Create(new RoutingOptions()), logger: NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            logger: NullLogger<AgentAsARouter>.Instance,
            options: Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            memory: new RouterMemory()));
        // Utility traffic never reaches the Orchestrator regardless of EnableOrchestratorPolicy, but the
        // composite still requires one to construct - EnableOrchestratorPolicy = false keeps this test's
        // fixture minimal (an Orchestrator with no voters would work identically for this assertion).
        var composite = new CompositeRoutingPolicy(
            utilityPolicy: utilityPolicy,
            generalPolicy: generalPolicy,
            orchestratorPolicy: CreateOrchestrator([]),
            options: Options.Create(new RoutingOptions { EnableOrchestratorPolicy = false }));
        var context = new RoutingContext(
            Dimension: "live:utility",
            true,
            Candidates:
            [
                new RoutingCandidate(ModelName: "cheap", Provider: "openai", false),
                new RoutingCandidate(ModelName: "pricey", Provider: "openai", false)
            ]);

        var selected = await composite.SelectModelAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        // Cold-start utility selection ranks by price alone - cheapest wins.
        Assert.Equal(expected: "cheap", actual: selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_NonUtilityContext_EnableOrchestratorPolicyFalse_DispatchesToGeneralPolicy()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: "live:code_generation", model: "model-a", 0.9);
        var utilityPolicy = new UtilityRoutingPolicy(priceCatalog: new PassthroughPriceCatalog(),
            memory: new RouterMemory(), options: Options.Create(new RoutingOptions()),
            logger: NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            logger: NullLogger<AgentAsARouter>.Instance,
            options: Options.Create(new RoutingOptions
            { EnableExploration = false, ExplorationRate = 0, DefaultModel = "model-a" }),
            memory: memory));
        // The kill switch (docs/router/orchestrator-live-path-plan.md M1.3) restores pre-Phase-M routing
        // exactly - this test is the "EnableOrchestratorPolicy = false" half of Phase M's exit criterion.
        var composite = new CompositeRoutingPolicy(
            utilityPolicy: utilityPolicy,
            generalPolicy: generalPolicy,
            orchestratorPolicy: CreateOrchestrator([]),
            options: Options.Create(new RoutingOptions { EnableOrchestratorPolicy = false }));
        var context = new RoutingContext(
            Dimension: "live:code_generation",
            false,
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)]);

        var selected = await composite.SelectModelAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "model-a", actual: selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_NonUtilityContext_DefaultsToOrchestrator()
    {
        var utilityPolicy = new UtilityRoutingPolicy(priceCatalog: new PassthroughPriceCatalog(),
            memory: new RouterMemory(), options: Options.Create(new RoutingOptions()),
            logger: NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            logger: NullLogger<AgentAsARouter>.Instance,
            options: Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            memory: new RouterMemory()));
        var orchestratorVoter = new FakeVoter(name: VoterNames.DimBest, modelName: "model-b", 0.9);
        // EnableOrchestratorPolicy defaults to true - not set here - so this exercises the actual Phase M
        // default rather than an explicitly-configured one.
        var composite = new CompositeRoutingPolicy(
            utilityPolicy: utilityPolicy,
            generalPolicy: generalPolicy,
            orchestratorPolicy: CreateOrchestrator([orchestratorVoter]),
            options: Options.Create(new RoutingOptions()));
        var context = new RoutingContext(
            Dimension: "live:code_generation",
            false,
            Candidates:
            [
                new RoutingCandidate(ModelName: "model-a", Provider: "openai", false),
                new RoutingCandidate(ModelName: "model-b", Provider: "openai", false)
            ]);

        var selected = await composite.SelectModelAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        // AgentRouterPolicy has no memory for this dimension and would fall back to a different model
        // (alphabetically-first candidate); the Orchestrator's single voter picks model-b, so this proves
        // the default dispatch actually reaches the Orchestrator rather than the general policy.
        Assert.Equal(expected: "model-b", actual: selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_NonUtilityContext_ForwardsRoutingSignalsToOrchestrator()
    {
        var utilityPolicy = new UtilityRoutingPolicy(priceCatalog: new PassthroughPriceCatalog(),
            memory: new RouterMemory(), options: Options.Create(new RoutingOptions()),
            logger: NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            logger: NullLogger<AgentAsARouter>.Instance,
            options: Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            memory: new RouterMemory()));
        var recordingVoter = new RecordingVoter(name: VoterNames.DimBest, modelName: "model-a");
        var composite = new CompositeRoutingPolicy(
            utilityPolicy: utilityPolicy,
            generalPolicy: generalPolicy,
            orchestratorPolicy: CreateOrchestrator([recordingVoter]),
            options: Options.Create(new RoutingOptions()));
        var context = new RoutingContext(
            Dimension: "live:code_generation",
            false,
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)]);
        var embedding = new[] { 1f, 2f, 3f };
        var signals = new RoutingSignals(TaskText: "refactor this function", TaskEmbedding: embedding);

        var selected = await composite.SelectModelAsync(context: context, signals: signals,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "model-a", actual: selected);
        Assert.NotNull(recordingVoter.LastContext);
        Assert.Equal(expected: "refactor this function", actual: recordingVoter.LastContext!.TaskText);
        Assert.Same(expected: embedding, actual: recordingVoter.LastContext.TaskEmbedding);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: the utility leg has no exploration
    /// mechanism, so it falls through to <see cref="IRoutingPolicy"/>'s default
    /// <see cref="IRoutingPolicy.DecideOutcomeAsync"/> implementation - non-exploratory, propensity 1.0 -
    /// even though <see cref="CompositeRoutingPolicy"/> itself overrides the method.
    /// </summary>
    [Fact]
    public async Task DecideOutcomeAsync_UtilityContext_ReportsNonExploratoryCertainPropensity()
    {
        var catalog = new PassthroughPriceCatalog();
        catalog.SetPrice(modelName: "cheap", provider: "openai", 1m, 1m);
        var utilityPolicy = new UtilityRoutingPolicy(priceCatalog: catalog, memory: new RouterMemory(),
            options: Options.Create(new RoutingOptions()), logger: NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            logger: NullLogger<AgentAsARouter>.Instance,
            options: Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            memory: new RouterMemory()));
        var composite = new CompositeRoutingPolicy(
            utilityPolicy: utilityPolicy,
            generalPolicy: generalPolicy,
            orchestratorPolicy: CreateOrchestrator([]),
            options: Options.Create(new RoutingOptions { EnableOrchestratorPolicy = false }));
        var context = new RoutingContext(
            Dimension: "live:utility",
            true,
            Candidates: [new RoutingCandidate(ModelName: "cheap", Provider: "openai", false)]);

        var decision = await composite.DecideOutcomeAsync(context: context, null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "cheap", actual: decision.SelectedModel);
        Assert.False(decision.IsExploratory);
        Assert.Equal(1.0, actual: decision.Propensity, 6);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: when the Orchestrator leg is chosen,
    /// <see cref="CompositeRoutingPolicy.DecideOutcomeAsync"/> forwards to
    /// <see cref="OrchestratorRoutingPolicy.DecideOutcomeAsync"/>, which reports real epsilon-greedy
    /// provenance instead of the interface default's always-certain wrap.
    /// </summary>
    [Fact]
    public async Task DecideOutcomeAsync_OrchestratorLeg_ReportsRealExplorationProvenance()
    {
        var utilityPolicy = new UtilityRoutingPolicy(priceCatalog: new PassthroughPriceCatalog(),
            memory: new RouterMemory(), options: Options.Create(new RoutingOptions()),
            logger: NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            logger: NullLogger<AgentAsARouter>.Instance,
            options: Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            memory: new RouterMemory()));
        var orchestratorVoter = new FakeVoter(name: VoterNames.DimBest, modelName: "model-a", 1.0);
        var orchestrator = new OrchestratorRoutingPolicy(
            voters: [orchestratorVoter],
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            { EnableExploration = true, ExplorationRate = 1.0 }),
            logger: NullLogger<OrchestratorRoutingPolicy>.Instance);
        var composite = new CompositeRoutingPolicy(
            utilityPolicy: utilityPolicy,
            generalPolicy: generalPolicy,
            orchestratorPolicy: orchestrator,
            options: Options.Create(new RoutingOptions()));
        var context = new RoutingContext(
            Dimension: "live:code_generation",
            false,
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)]);

        var decision = await composite.DecideOutcomeAsync(context: context, null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "model-a", actual: decision.SelectedModel);
        Assert.True(decision.IsExploratory);
        Assert.Equal(1.0, actual: decision.Propensity, 6);
    }

    /// <summary>
    /// Builds a bare <see cref="OrchestratorRoutingPolicy"/> for dispatch-only test fixtures - exploration disabled
    /// so dispatch assertions stay deterministic.
    /// </summary>
    private static OrchestratorRoutingPolicy CreateOrchestrator(IEnumerable<IRoutingVoter> voters)
    {
        return new OrchestratorRoutingPolicy(
            voters: voters,
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            { EnableExploration = false, ExplorationRate = 0 }),
            logger: NullLogger<OrchestratorRoutingPolicy>.Instance);
    }

    private sealed class FakeVoter(string name, string modelName, double confidence) : IRoutingVoter
    {
        public string Name { get; } = name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new VoterVote(VoterName: Name, ModelName: modelName, Confidence: confidence));
        }
    }

    private sealed class RecordingVoter(string name, string modelName) : IRoutingVoter
    {
        public VotingContext? LastContext { get; private set; }
        public string Name { get; } = name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(new VoterVote(VoterName: Name, ModelName: modelName, 0.9));
        }
    }

    private sealed class PassthroughPriceCatalog : IModelPriceCatalog
    {
        private readonly Dictionary<ModelKey, ModelPrice> _prices = [];

        public ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context)
        {
            return _prices.TryGetValue(key: key, value: out var price) ? price : null;
        }

        public ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge)
        {
            return _prices.TryGetValue(key: key, value: out var price) ? price : null;
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
}