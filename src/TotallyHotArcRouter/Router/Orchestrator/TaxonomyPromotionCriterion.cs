namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// One reporting window's comparison between the two taxonomies
/// (docs/router/self-organizing-classification-plan.md Phase T4), and the input
/// <see cref="TaxonomyPromotionCriterion"/> evaluates.
/// </summary>
/// <param name="DimensionMeanAbsoluteError">
/// The frozen nine-dimension taxonomy's mean absolute error over this window's held-out predictions, or
/// <see langword="null"/> when no row in the window produced one.
/// </param>
/// <param name="ClusterMeanAbsoluteError">
/// The learned cluster taxonomy's mean absolute error over the same window, or <see langword="null"/> when
/// no row produced one.
/// </param>
/// <param name="ClusterCoverage">
/// The fraction of this window's rows that received a non-abstaining cluster assignment, in <c>[0, 1]</c>.
/// </param>
public sealed record TaxonomyComparisonWindow(
    double? DimensionMeanAbsoluteError,
    double? ClusterMeanAbsoluteError,
    double ClusterCoverage);

/// <summary>
/// Evaluates the promotion criterion docs/router/self-organizing-classification-plan.md Phase T4 defines:
/// whether the learned cluster taxonomy has earned the right to a promotion <em>plan</em>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This authorizes writing a plan; it promotes nothing.</b> The plan is explicit that promotion -
/// replacing the keyword classifier with the learned taxonomy - is separate future work, and that meeting
/// this predicate is its precondition rather than its trigger. Nothing in this codebase reads the result of
/// this type to change routing behavior, and nothing should: the "additive, not a replacement" decision
/// holds until a future plan deliberately revisits it.
/// </para>
/// <para>
/// A pure function over already-computed windows, deliberately holding no clock, database, or options
/// dependency - the criterion is a statement about numbers, and keeping it free of ambient state is what
/// makes it directly testable, which Phase T4's exit bar requires by name.
/// </para>
/// </remarks>
public static class TaxonomyPromotionCriterion
{
    /// <summary>The default number of consecutive qualifying windows the criterion requires (the plan's <c>K</c>).</summary>
    public const int DefaultConsecutiveWindows = 4;

    /// <summary>The default minimum cluster coverage each qualifying window must reach.</summary>
    public const double DefaultMinimumCoverage = 0.8;

    /// <summary>
    /// Returns whether the most recent <paramref name="consecutiveWindows"/> windows all qualify: the
    /// cluster taxonomy's mean absolute error strictly below the dimension taxonomy's, and cluster coverage
    /// at or above <paramref name="minimumCoverage"/>.
    /// </summary>
    /// <param name="windows">
    /// The reporting windows in chronological order (oldest first). Only the newest
    /// <paramref name="consecutiveWindows"/> are examined; earlier history cannot rescue or spoil the
    /// result, matching the plan's "over K consecutive reporting windows" wording.
    /// </param>
    /// <param name="consecutiveWindows">How many consecutive windows must qualify.</param>
    /// <param name="minimumCoverage">The coverage floor each window must meet.</param>
    /// <returns><see langword="true"/> when the criterion is met; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// A window missing either error figure never qualifies. That is the deliberately conservative
    /// reading: "the cluster ledger's MAE is strictly lower" is a claim that cannot be evaluated when one
    /// side produced no measurement, and this plan's ground rules treat an unmeasurable comparison as a
    /// failure to demonstrate rather than as a pass by default.
    /// </remarks>
    public static bool IsMet(
        IReadOnlyList<TaxonomyComparisonWindow> windows,
        int consecutiveWindows = DefaultConsecutiveWindows,
        double minimumCoverage = DefaultMinimumCoverage)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveWindows);

        if (windows.Count < consecutiveWindows) return false;

        for (var i = windows.Count - consecutiveWindows; i < windows.Count; i++)
            if (!Qualifies(window: windows[i], minimumCoverage: minimumCoverage))
                return false;

        return true;
    }

    /// <summary>
    /// Returns whether one window qualifies on its own - both errors present, the cluster error strictly
    /// lower, and coverage at or above the floor.
    /// </summary>
    /// <param name="window">The window to evaluate.</param>
    /// <param name="minimumCoverage">The coverage floor.</param>
    /// <returns><see langword="true"/> when this window qualifies; otherwise <see langword="false"/>.</returns>
    public static bool Qualifies(TaxonomyComparisonWindow window, double minimumCoverage = DefaultMinimumCoverage)
    {
        ArgumentNullException.ThrowIfNull(window);

        return window is { DimensionMeanAbsoluteError: { } dimensionError, ClusterMeanAbsoluteError: { } clusterError }
               && clusterError < dimensionError
               && window.ClusterCoverage >= minimumCoverage;
    }
}