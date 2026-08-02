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
    }
}

