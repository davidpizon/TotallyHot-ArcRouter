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
    /// Gets the name of the configured routing policy.
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
    /// <see cref="DimBestVoterWeight"/>'s remarks for the worked-example derivation. Currently moot in
    /// practice - <see cref="Router.Orchestrator.LlmRouterVoter"/> always abstains until a future phase
    /// fills in its model artifact - but kept configurable so that phase needs no options change.
    /// </summary>
    [Range(0d, 100d)]
    public double LlmRouterVoterWeight { get; init; } = 0.64;

    /// <summary>Gets whether the <c>dim_best</c> voter participates in the Orchestrator's vote.</summary>
    public bool EnableDimBestVoter { get; init; } = true;

    /// <summary>Gets whether the <c>memory_kNN</c> voter participates in the Orchestrator's vote.</summary>
    public bool EnableMemoryKnnVoter { get; init; } = true;

    /// <summary>Gets whether the <c>logreg</c> voter participates in the Orchestrator's vote.</summary>
    public bool EnableLogRegVoter { get; init; } = true;

    /// <summary>
    /// Gets whether the <c>llm_router</c> voter participates in the Orchestrator's vote. Enabled by default
    /// even though the voter currently always abstains (no model artifact yet) - disabling it changes
    /// nothing observable today, but flipping it off is how a future phase's real implementation gets
    /// excluded without a code change if ever needed.
    /// </summary>
    public bool EnableLlmRouterVoter { get; init; } = true;

    /// <summary>
    /// Performs domain-level validation that is not fully expressible through data annotations.
    /// </summary>
    /// <exception cref="OptionsValidationException">Thrown when the routing option values are inconsistent.</exception>
    public void EnsureValid()
    {
        if (!EnableDimBestVoter && !EnableMemoryKnnVoter && !EnableLogRegVoter && !EnableLlmRouterVoter)
        {
            throw new OptionsValidationException(
                nameof(RoutingOptions),
                typeof(RoutingOptions),
                ["At least one Orchestrator voter must be enabled."]);
        }

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
    }
}

