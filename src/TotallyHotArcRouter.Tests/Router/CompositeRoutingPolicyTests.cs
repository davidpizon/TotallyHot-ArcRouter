using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        await memory.AddScoreAsync("live:code_generation", "model-a", 0.4);
        await memory.AddScoreAsync("live:code_generation", "model-b", 0.9);
        var router = new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            memory);
        var policy = new AgentRouterPolicy(router);
        var context = new RoutingContext(
            "live:code_generation",
            IsUtility: false,
            Candidates: [new RoutingCandidate("model-a", "openai", IsFree: false), new RoutingCandidate("model-b", "openai", IsFree: false)]);

        var selected = await policy.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("model-b", selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_UtilityContext_DispatchesToUtilityPolicy()
    {
        var catalog = new PassthroughPriceCatalog();
        catalog.SetPrice("cheap", "openai", 1m, 1m);
        catalog.SetPrice("pricey", "openai", 50m, 50m);
        var utilityPolicy = new UtilityRoutingPolicy(catalog, new RouterMemory(), Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            new RouterMemory()));
        // Utility traffic never reaches the Orchestrator regardless of EnableOrchestratorPolicy, but the
        // composite still requires one to construct - EnableOrchestratorPolicy = false keeps this test's
        // fixture minimal (an Orchestrator with no voters would work identically for this assertion).
        var composite = new CompositeRoutingPolicy(
            utilityPolicy,
            generalPolicy,
            CreateOrchestrator([]),
            Options.Create(new RoutingOptions { EnableOrchestratorPolicy = false }));
        var context = new RoutingContext(
            "live:utility",
            IsUtility: true,
            Candidates: [new RoutingCandidate("cheap", "openai", IsFree: false), new RoutingCandidate("pricey", "openai", IsFree: false)]);

        var selected = await composite.SelectModelAsync(context, TestContext.Current.CancellationToken);

        // Cold-start utility selection ranks by price alone - cheapest wins.
        Assert.Equal("cheap", selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_NonUtilityContext_EnableOrchestratorPolicyFalse_DispatchesToGeneralPolicy()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync("live:code_generation", "model-a", 0.9);
        var utilityPolicy = new UtilityRoutingPolicy(new PassthroughPriceCatalog(), new RouterMemory(), Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0, DefaultModel = "model-a" }),
            memory));
        // The kill switch (docs/router/orchestrator-live-path-plan.md M1.3) restores pre-Phase-M routing
        // exactly - this test is the "EnableOrchestratorPolicy = false" half of Phase M's exit criterion.
        var composite = new CompositeRoutingPolicy(
            utilityPolicy,
            generalPolicy,
            CreateOrchestrator([]),
            Options.Create(new RoutingOptions { EnableOrchestratorPolicy = false }));
        var context = new RoutingContext(
            "live:code_generation",
            IsUtility: false,
            Candidates: [new RoutingCandidate("model-a", "openai", IsFree: false)]);

        var selected = await composite.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("model-a", selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_NonUtilityContext_DefaultsToOrchestrator()
    {
        var utilityPolicy = new UtilityRoutingPolicy(new PassthroughPriceCatalog(), new RouterMemory(), Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            new RouterMemory()));
        var orchestratorVoter = new FakeVoter(VoterNames.DimBest, "model-b", 0.9);
        // EnableOrchestratorPolicy defaults to true - not set here - so this exercises the actual Phase M
        // default rather than an explicitly-configured one.
        var composite = new CompositeRoutingPolicy(
            utilityPolicy,
            generalPolicy,
            CreateOrchestrator([orchestratorVoter]),
            Options.Create(new RoutingOptions()));
        var context = new RoutingContext(
            "live:code_generation",
            IsUtility: false,
            Candidates: [new RoutingCandidate("model-a", "openai", IsFree: false), new RoutingCandidate("model-b", "openai", IsFree: false)]);

        var selected = await composite.SelectModelAsync(context, TestContext.Current.CancellationToken);

        // AgentRouterPolicy has no memory for this dimension and would fall back to a different model
        // (alphabetically-first candidate); the Orchestrator's single voter picks model-b, so this proves
        // the default dispatch actually reaches the Orchestrator rather than the general policy.
        Assert.Equal("model-b", selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_NonUtilityContext_ForwardsRoutingSignalsToOrchestrator()
    {
        var utilityPolicy = new UtilityRoutingPolicy(new PassthroughPriceCatalog(), new RouterMemory(), Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            new RouterMemory()));
        var recordingVoter = new RecordingVoter(VoterNames.DimBest, "model-a");
        var composite = new CompositeRoutingPolicy(
            utilityPolicy,
            generalPolicy,
            CreateOrchestrator([recordingVoter]),
            Options.Create(new RoutingOptions()));
        var context = new RoutingContext(
            "live:code_generation",
            IsUtility: false,
            Candidates: [new RoutingCandidate("model-a", "openai", IsFree: false)]);
        var embedding = new float[] { 1f, 2f, 3f };
        var signals = new RoutingSignals("refactor this function", embedding);

        var selected = await composite.SelectModelAsync(context, signals, TestContext.Current.CancellationToken);

        Assert.Equal("model-a", selected);
        Assert.NotNull(recordingVoter.LastContext);
        Assert.Equal("refactor this function", recordingVoter.LastContext!.TaskText);
        Assert.Same(embedding, recordingVoter.LastContext.TaskEmbedding);
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
        catalog.SetPrice("cheap", "openai", 1m, 1m);
        var utilityPolicy = new UtilityRoutingPolicy(catalog, new RouterMemory(), Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            new RouterMemory()));
        var composite = new CompositeRoutingPolicy(
            utilityPolicy,
            generalPolicy,
            CreateOrchestrator([]),
            Options.Create(new RoutingOptions { EnableOrchestratorPolicy = false }));
        var context = new RoutingContext(
            "live:utility",
            IsUtility: true,
            Candidates: [new RoutingCandidate("cheap", "openai", IsFree: false)]);

        var decision = await composite.DecideOutcomeAsync(context, signals: null, TestContext.Current.CancellationToken);

        Assert.Equal("cheap", decision.SelectedModel);
        Assert.False(decision.IsExploratory);
        Assert.Equal(1.0, decision.Propensity, precision: 6);
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
        var utilityPolicy = new UtilityRoutingPolicy(new PassthroughPriceCatalog(), new RouterMemory(), Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            new RouterMemory()));
        var orchestratorVoter = new FakeVoter(VoterNames.DimBest, "model-a", 1.0);
        var orchestrator = new OrchestratorRoutingPolicy(
            [orchestratorVoter],
            Options.Create(new RoutingOptions { EnableExploration = true, ExplorationRate = 1.0 }),
            NullLogger<OrchestratorRoutingPolicy>.Instance);
        var composite = new CompositeRoutingPolicy(
            utilityPolicy,
            generalPolicy,
            orchestrator,
            Options.Create(new RoutingOptions()));
        var context = new RoutingContext(
            "live:code_generation",
            IsUtility: false,
            Candidates: [new RoutingCandidate("model-a", "openai", IsFree: false)]);

        var decision = await composite.DecideOutcomeAsync(context, signals: null, TestContext.Current.CancellationToken);

        Assert.Equal("model-a", decision.SelectedModel);
        Assert.True(decision.IsExploratory);
        Assert.Equal(1.0, decision.Propensity, precision: 6);
    }

    /// <summary>Builds a bare <see cref="OrchestratorRoutingPolicy"/> for dispatch-only test fixtures - exploration disabled so dispatch assertions stay deterministic.</summary>
    private static OrchestratorRoutingPolicy CreateOrchestrator(IEnumerable<IRoutingVoter> voters) =>
        new(
            voters,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            NullLogger<OrchestratorRoutingPolicy>.Instance);

    private sealed class FakeVoter(string name, string modelName, double confidence) : IRoutingVoter
    {
        public string Name { get; } = name;

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VoterVote(Name, modelName, confidence));
    }

    private sealed class RecordingVoter(string name, string modelName) : IRoutingVoter
    {
        public string Name { get; } = name;

        public VotingContext? LastContext { get; private set; }

        public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(new VoterVote(Name, modelName, 0.9));
        }
    }

    private sealed class PassthroughPriceCatalog : IModelPriceCatalog
    {
        private readonly Dictionary<ModelKey, ModelPrice> _prices = [];

        public void SetPrice(string modelName, string provider, decimal input, decimal output) =>
            _prices[new ModelKey(modelName, provider)] = new ModelPrice(input, output);

        public ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context) =>
            _prices.TryGetValue(key, out var price) ? price : null;

        public ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge) =>
            _prices.TryGetValue(key, out var price) ? price : null;

        public void Invalidate()
        {
        }
    }
}
