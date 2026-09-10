namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// One request's routing return-on-investment, as served to the GUI's Cost Analytics "Routing ROI"
/// screen - a projection of
/// <see cref="TotallyHot.ArcRouter.Transcripts.TaxonomyComparisonRecord"/> carrying only its cost half.
/// </summary>
/// <remarks>
/// <para>
/// <b>The baseline is an untrained router's pick</b> - one that has never read live memory, choosing only
/// from the frozen CodeRouterBench probing-split prior - not a worst-case model and not the live,
/// memory-preferring <c>dim_best</c> voter. The figure answers "what did the router's learning buy against
/// the same classifier before it had learned anything", which is the comparison
/// docs/router/routing-roi-regret-plan.md's frozen-baseline correction defines and the only baseline this
/// codebase computes as a savings yardstick. A baseline that itself learned from live traffic would hold
/// the gap between it and the router constant and report an improvement of zero regardless of how much the
/// router has actually learned.
/// </para>
/// <para>
/// <b>The predictive-adequacy half is deliberately not projected here.</b> Phase T4's mean-absolute-error
/// series exists to gate a promotion decision, not to be read as an operator metric, so it stays in the
/// store and the structured logs rather than reaching a dashboard.
/// </para>
/// </remarks>
/// <param name="ComparedAtUtc">
/// When the comparison ran. Not the time the request was served - comparisons are deliberately
/// not real-time.
/// </param>
/// <param name="SessionId">The conversation this request belonged to, for the screen's per-session filter.</param>
/// <param name="RoutedModel">The model that actually served the request.</param>
/// <param name="BaselineModel">
/// The model an untrained router would have chosen from the frozen CodeRouterBench prior alone, or
/// <see langword="null"/> when it abstained.
/// </param>
/// <param name="ActualCostUsd">What serving the request actually cost, or <see langword="null"/> when unknown.</param>
/// <param name="BaselineEstimatedCostUsd">
/// <b>An estimate</b> of what <see cref="BaselineModel"/> would have cost, priced from that model's own
/// observed-average token counts - the counterfactual's true token count is never observed. Any surface
/// rendering this must label it as an estimate.
/// </param>
/// <param name="EstimatedNetSavingsUsd">
/// Baseline minus actual: positive is a saving, negative a loss. Inherits the
/// estimate qualification.
/// </param>
/// <param name="IsExploratory">Whether this was an epsilon-greedy probe rather than the ensemble's own pick.</param>
public sealed record RoutingRoiPoint(
    DateTimeOffset ComparedAtUtc,
    string SessionId,
    string RoutedModel,
    string? BaselineModel,
    decimal? ActualCostUsd,
    decimal? BaselineEstimatedCostUsd,
    decimal? EstimatedNetSavingsUsd,
    bool IsExploratory);