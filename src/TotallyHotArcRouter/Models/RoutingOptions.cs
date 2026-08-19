using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Represents configurable routing settings bound from the <c>Routing</c> section.
/// </summary>
public sealed class RoutingOptions
{
    /// <summary>
    /// Gets the configuration section name used for routing settings.
    /// </summary>
    public const string SectionName = "Routing";

    /// <summary>
    /// Gets the default model used when no better choice is available.
    /// </summary>
    [Required]
    public string DefaultModel { get; init; } = RouterConstants.DefaultModel;

    /// <summary>
    /// Gets the maximum number of candidate models considered in one decision.
    /// </summary>
    [Range(1, 100)]
    public int MaxCandidates { get; init; } = 8;

    /// <summary>
    /// Gets the maximum number of memory neighbors used during retrieval.
    /// </summary>
    [Range(1, 100)]
    public int MaxNeighborCount { get; init; } = 10;

    /// <summary>
    /// Gets a value indicating whether exploration is enabled.
    /// </summary>
    public bool EnableExploration { get; init; } = true;

    /// <summary>
    /// Gets the exploration rate used by exploration-capable policies.
    /// </summary>
    [Range(0d, 1d)]
    public double ExplorationRate { get; init; } = 0.05;

    /// <summary>
    /// Dead configuration: bound from <c>appsettings.json</c> (default <c>"hierarchical"</c>) but read by
    /// nothing except this property's own unit tests - no policy selection in the codebase branches on it.
    /// <see cref="EnableOrchestratorPolicy"/> is the actual live-path switch
    /// (docs/router/orchestrator-live-path-plan.md M1.3, which considered and rejected repurposing this
    /// property for that role: its shipped value is operator-authored and has never had live meaning, so
    /// attaching one now would change behavior based on configuration nobody was asked to review). Kept
    /// rather than removed because dropping a bound config key is a schema break unrelated to routing;
    /// removal is tracked as a separate cleanup.
    /// </summary>
    public string PolicyName { get; init; } = RouterConstants.DefaultPolicy;

    /// <summary>
    /// Gets the path to the JSON file used for router memory persistence.
    /// Relative paths are resolved from the application base directory.
    /// </summary>
    [Required]
    public string MemoryPath { get; init; } = "router_memory.json";

    /// <summary>
    /// Gets the path to the SQLite database used for task-embedding-keyed memory persistence
    /// (PLAN.md Phase J, research-doc §3.3), separate from the JSON dimension-averages file at
    /// <see cref="MemoryPath"/>. Relative paths are resolved from the application base directory.
    /// </summary>
    [Required]
    public string EmbeddingMemoryDatabasePath { get; init; } = "router_embedding_memory.db";

    /// <summary>
    /// Gets the cosine similarity a neighbor must meet to be returned from embedding-keyed kNN
    /// retrieval. Defaults to research-doc §3.3's canonical value.
    /// </summary>
    [Range(-1d, 1d)]
    public double EmbeddingSimilarityThreshold { get; init; } = 0.5;

    /// <summary>
    /// Gets the maximum number of entries embedding-keyed memory retains before evicting the oldest
    /// (FIFO), per research-doc §3.3's canonical bound.
    /// </summary>
    [Range(1, 1_000_000)]
    public int EmbeddingMemoryCapacity { get; init; } = 20_000;

    /// <summary>
    /// Gets the quality weight ε₁ in the cost-aware reward <c>r = ε₁·s + ε₂·κ</c>
    /// (research doc §"Notation"; <c>docs/router/utility-model-routing.md</c> §B3.5). Defaults to the
    /// manuscript's canonical value.
    /// </summary>
    [Range(-100d, 100d)]
    public double Epsilon1 { get; init; } = 1.0;

    /// <summary>
    /// Gets the cost weight ε₂ in the cost-aware reward <c>r = ε₁·s + ε₂·κ</c>. Negative by convention -
    /// a higher κ (more expensive) lowers the reward. Defaults to the manuscript's canonical value.
    /// </summary>
    [Range(-100d, 100d)]
    public double Epsilon2 { get; init; } = -0.1;

    /// <summary>
    /// Gets the client-facing model name (a <c>ModelRouting:ModelList[].ModelName</c> entry) that stands in
    /// as the <b>Always-<i>m</i></b> baseline when reporting what routing cost versus not routing at all -
    /// research-doc Table 4's "Single-Model (Always-<i>m</i>): Fixed model regardless of context. Reference
    /// performance floor." <see langword="null"/> (the default) means no baseline is declared and no
    /// savings figure is reported.
    /// </summary>
    /// <remarks>
    /// Deliberately operator-declared rather than auto-derived. Picking the most expensive configured model
    /// automatically would manufacture a flattering number nobody agreed to, and would silently inflate
    /// every time a pricier model joined the list; the paper's Always-<i>m</i> is a reference point chosen
    /// up front, not an artifact of the model pool.
    /// <para>
    /// Only consulted for requests where the client named no real model of its own (the
    /// <c>auto</c>/<c>agentic-router</c>/unresolved-name paths). When the client did name a model and the
    /// router substituted a different one, that named model is the honest counterfactual and is used
    /// instead - no configuration needed.
    /// </para>
    /// </remarks>
    public string? AlwaysBaselineModel { get; init; }

    /// <summary>
    /// Gets the USD price per 1,000,000 tokens charged for the router's <em>own</em> token consumption,
    /// which today is local ONNX embedding inference
    /// (<see cref="Router.Embeddings.OnnxEmbeddingClient"/>). Defaults to research-doc §5.1's canonical
    /// self-hosted rate of <c>$0.054/M</c>, derived there from H100 amortization and measured throughput
    /// (§B.3.2).
    /// </summary>
    /// <remarks>
    /// This exists because the paper's <c>TotTok</c> is explicitly "input + output token consumption
    /// (<b>router</b> + model)" - routing overhead is charged against the router, not hidden. Without it a
    /// savings figure would be gross rather than net, flattering the router by exactly the amount it spends
    /// deciding. Not sourced from the price catalog: that catalog holds published <em>provider</em> prices,
    /// and locally-served inference has no provider to publish one - the rate is an amortization the
    /// operator owns.
    /// </remarks>
    [Range(0d, 1000d)]
    public decimal SelfHostedRouterPricePerMillionTokens { get; init; } = 0.054m;

    /// <summary>
    /// Gets the minimum observed quality score <see cref="TotallyHot.ArcRouter.Router.UtilityRoutingPolicy"/> requires a
    /// candidate to hold before it is eligible for cost-aware selection
    /// (<c>docs/router/utility-model-routing.md</c> §B3.4). A candidate with no observed score yet
    /// (<see langword="null"/>) is never dropped by this gate - unobserved is not the same as bad; only
    /// a candidate that has been observed and scored below this floor is excluded.
    /// </summary>
    [Range(0d, 1d)]
    public double UtilityMinQualityScore { get; init; } = 0.3;

    /// <summary>
    /// Gets the <c>dim_best</c> voter's fixed weight in <see cref="Router.Orchestrator.OrchestratorRoutingPolicy"/>'s
    /// weighted vote (PLAN.md Phase L). Defaults to the value that, combined with
    /// <see cref="MemoryKnnVoterWeight"/>, reproduces research-doc §3.3's worked example (0.9 + 0.57 = 1.47) -
    /// see <see cref="Router.Orchestrator.OrchestratorRoutingPolicy"/>'s remarks for the full derivation.
    /// </summary>
    [Range(0d, 100d)]
    public double DimBestVoterWeight { get; init; } = 0.9;

    /// <summary>
    /// Gets the <c>memory_kNN</c> voter's fixed weight in the Orchestrator's weighted vote. See
    /// <see cref="DimBestVoterWeight"/>'s remarks for the worked-example derivation.
    /// </summary>
    [Range(0d, 100d)]
    public double MemoryKnnVoterWeight { get; init; } = 0.57;

    /// <summary>
    /// Gets the <c>logreg</c> voter's fixed weight in the Orchestrator's weighted vote. See
    /// <see cref="DimBestVoterWeight"/>'s remarks for the worked-example derivation.
    /// </summary>
    [Range(0d, 100d)]
    public double LogRegVoterWeight { get; init; } = 0.43;

    /// <summary>
    /// Gets the <c>llm_router</c> voter's fixed weight in the Orchestrator's weighted vote. See
    /// <see cref="DimBestVoterWeight"/>'s remarks for the worked-example derivation.
    /// <see cref="Router.Orchestrator.LlmRouterVoter"/> is a prompted off-the-shelf small instruct
    /// model, not the paper's fine-tuned checkpoint (see its own remarks for that settled deferral) -
    /// it still abstains whenever <see cref="LlmRouterOptions"/>'s model artifacts are missing or
    /// the task has no prompt text to route on.
    /// </summary>
    [Range(0d, 100d)]
    public double LlmRouterVoterWeight { get; init; } = 0.64;

    /// <summary>
    /// Gets the maximum time, in milliseconds, <see cref="Proxy.RequestInterceptor"/> waits for
    /// <see cref="Router.Embeddings.IEmbeddingClient.EmbedAsync"/> before giving up and routing without an
    /// embedding (docs/router/live-feedback-learning-plan.md Phase 2b). The routing hot path must never
    /// block on learning: a warm ONNX forward pass is single-digit milliseconds, so this default leaves
    /// generous headroom for transient contention on <c>OnnxEmbeddingClient</c>'s inference semaphore
    /// without letting a stuck embedding call stall the request it was computed for.
    /// </summary>
    [Range(1, 60_000)]
    public int EmbeddingBudgetMs { get; init; } = 250;

    /// <summary>
    /// Gets the maximum number of entries <see cref="Router.Embeddings.PendingTaskEmbeddingCache"/> holds
    /// before evicting the oldest (FIFO) - docs/router/live-feedback-learning-plan.md Phase 2c. Bridges a
    /// request's task embedding (computed on the request path) to its later-arriving verifier score
    /// (correlated only by <see cref="TotallyHot.ArcRouter.Sandbox.SandboxResult.RequestCorrelationId"/>);
    /// bounded so an operator who disables the sandbox (scores never arrive) cannot grow this unboundedly.
    /// </summary>
    [Range(1, 1_000_000)]
    public int PendingEmbeddingCacheCapacity { get; init; } = 2_000;

    /// <summary>
    /// Gets how long, in seconds, <see cref="Router.Embeddings.PendingTaskEmbeddingCache"/> retains an
    /// entry before it expires unclaimed - a score that never arrives (sandbox disabled, evaluation
    /// dropped, request aborted) must not hold its slot forever. Default comfortably exceeds a sandboxed
    /// evaluation's expected turnaround.
    /// </summary>
    [Range(1, 86_400)]
    public int PendingEmbeddingCacheTtlSeconds { get; init; } = 300;

    /// <summary>Gets whether the <c>dim_best</c> voter participates in the Orchestrator's vote.</summary>
    public bool EnableDimBestVoter { get; init; } = true;

    /// <summary>Gets whether the <c>memory_kNN</c> voter participates in the Orchestrator's vote.</summary>
    public bool EnableMemoryKnnVoter { get; init; } = true;

    /// <summary>Gets whether the <c>logreg</c> voter participates in the Orchestrator's vote.</summary>
    public bool EnableLogRegVoter { get; init; } = true;

    /// <summary>
    /// Gets whether the <c>llm_router</c> voter participates in the Orchestrator's vote. Enabled by
    /// default; the voter still abstains cleanly on its own whenever its model artifacts are unavailable,
    /// so this flag is only needed to exclude it entirely (e.g. to save the generation latency/cost) even
    /// when the model is otherwise usable.
    /// </summary>
    public bool EnableLlmRouterVoter { get; init; } = true;

    /// <summary>
    /// Gets the relative training weight applied to each live <c>memory_entries</c> row when training the
    /// <c>logreg</c> voter's artifact, relative to an OOD bootstrap row's fixed weight of <c>1.0</c>
    /// (docs/router/live-feedback-learning-plan.md Phase 4b). Greater than 1 by default because live
    /// traffic is the distribution actually being served, while the OOD bootstrap set is only a prior -
    /// the plan's own reasoning for weighting live observations above bootstrap rows.
    /// </summary>
    [Range(0d, 1000d)]
    public double LogRegLiveSampleWeight { get; init; } = 3.0;

    /// <summary>
    /// Gets the minimum total number of training samples (bootstrap rows plus live memory entries)
    /// <see cref="Router.Orchestrator.EmbeddingLogRegTrainingService"/> requires before writing a new
    /// <c>logreg</c> artifact. Below this, the retrain is declined and the prior artifact (if any) is
    /// left in place - the plan's "reject rather than install something worse" guard against a
    /// meaningless fit from too few rows.
    /// </summary>
    [Range(1, 1_000_000)]
    public int LogRegMinTrainingRows { get; init; } = 20;

    /// <summary>
    /// Gets the minimum number of distinct models that must have at least one training sample before
    /// <see cref="Router.Orchestrator.EmbeddingLogRegTrainingService"/> writes a new <c>logreg</c> artifact. A fit
    /// covering only one model is meaningless for an argmax-over-candidates voter - see
    /// <see cref="LogRegMinTrainingRows"/>'s remarks for the same guard's reasoning.
    /// </summary>
    [Range(1, 1_000)]
    public int LogRegMinModelsRepresented { get; init; } = 2;

    /// <summary>
    /// Gets the number of new <c>memory_entries</c> rows that must accumulate since the <c>logreg</c>
    /// artifact's last recorded <see cref="Router.Orchestrator.EmbeddingLogRegModelArtifact.MemoryEntryCount"/>
    /// before <c>Hosting.LogRegRetrainHostedService</c> automatically retrains
    /// (docs/router/live-feedback-learning-plan.md Phase 4c).
    /// </summary>
    [Range(1, 1_000_000)]
    public int LogRegRetrainThreshold { get; init; } = 500;

    /// <summary>
    /// Gets whether <c>Hosting.LogRegRetrainHostedService</c> automatically retrains the
    /// <c>logreg</c> voter's artifact once <see cref="LogRegRetrainThreshold"/> new memory entries have
    /// accumulated. The CLI flag (<c>--retrain-logreg</c>) and, later, the Governance retrain button both
    /// bypass this flag - it only gates the unattended background trigger.
    /// </summary>
    public bool EnableAutomaticLogRegRetrain { get; init; } = true;

    /// <summary>
    /// Gets whether <see cref="Router.CompositeRoutingPolicy"/> dispatches non-utility traffic to
    /// <see cref="Router.Orchestrator.OrchestratorRoutingPolicy"/> (the default) rather than
    /// <see cref="Router.AgentRouterPolicy"/>'s memory-only ranking
    /// (docs/router/orchestrator-live-path-plan.md M1.3). The kill switch for the Orchestrator's live
    /// path, independent of whether an explicitly-named model is honored - that question is settled
    /// (a named, servable model is always served) and has no configuration knob. Setting this to
    /// <see langword="false"/> restores pre-Phase-M routing exactly, and is also how PLAN.md Phase N
    /// exercises <see cref="Router.AgentRouterPolicy"/> as a comparison baseline without deregistering it.
    /// </summary>
    public bool EnableOrchestratorPolicy { get; init; } = true;

    /// <summary>
    /// Performs domain-level validation that is not fully expressible through data annotations.
    /// </summary>
    /// <exception cref="OptionsValidationException">Thrown when the routing option values are inconsistent.</exception>
    public void EnsureValid()
    {
        if (!RouterConstants.SupportedModels.Contains(DefaultModel, StringComparer.OrdinalIgnoreCase))
        {
            throw new OptionsValidationException(
                nameof(RoutingOptions),
                typeof(RoutingOptions),
                [$"DefaultModel '{DefaultModel}' is not in the supported model list."]);
        }

        if (!EnableExploration && ExplorationRate != 0)
        {
            throw new OptionsValidationException(
                nameof(RoutingOptions),
                typeof(RoutingOptions),
                ["ExplorationRate must be 0 when exploration is disabled."]);
        }

        if (string.IsNullOrWhiteSpace(MemoryPath))
        {
            throw new OptionsValidationException(
                nameof(RoutingOptions),
                typeof(RoutingOptions),
                ["MemoryPath is required."]);
        }

        if (string.IsNullOrWhiteSpace(EmbeddingMemoryDatabasePath))
        {
            throw new OptionsValidationException(
                nameof(RoutingOptions),
                typeof(RoutingOptions),
                ["EmbeddingMemoryDatabasePath is required."]);
        }

        // Null means "no Always-m baseline declared" and is valid; a present-but-blank value is not - it
        // reads as a configured baseline while naming no model, which would silently report no savings
        // rather than surfacing the typo.
        if (AlwaysBaselineModel is not null && string.IsNullOrWhiteSpace(AlwaysBaselineModel))
        {
            throw new OptionsValidationException(
                nameof(RoutingOptions),
                typeof(RoutingOptions),
                ["AlwaysBaselineModel must name a model when set; omit it entirely to declare no baseline."]);
        }
    }
}

