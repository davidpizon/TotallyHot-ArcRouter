using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The <c>Always-m</c> baseline (research-doc Table 4): always picks the same configured model,
/// ignoring dimension and every other context signal. One instance per candidate model is the reference
/// floor, and the natural sanity check — <c>CumReg</c> for <c>Always-Opus</c> should roughly match
/// Opus's own row-average gap to the per-task oracle.
/// </summary>
public sealed class AlwaysModelBaseline : IRegretBaselineRouter
{
    private readonly string _canonicalModelId;

    /// <summary>Initializes a new instance of the <see cref="AlwaysModelBaseline"/> class for one fixed model.</summary>
    /// <param name="modelId">The model id this baseline always routes to, any spelling.</param>
    public AlwaysModelBaseline(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _canonicalModelId = ModelNameCanonicalizer.Canonicalize(modelId);
        Name = $"always_{_canonicalModelId}";
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns <see langword="null"/> — not a fallback candidate — when this task's outcome row never
    /// scored the fixed model at all, so that task is excluded from this baseline's metrics rather than
    /// silently substituting a different model's cell.
    /// </remarks>
    public string? Route(RegretReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.CandidateModelIds.FirstOrDefault(id =>
            string.Equals(a: id, b: _canonicalModelId, comparisonType: StringComparison.Ordinal));
    }
}