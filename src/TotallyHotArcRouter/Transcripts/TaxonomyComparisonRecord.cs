namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// One scored request's comparison between the frozen nine-dimension taxonomy and the learned cluster
/// taxonomy, per docs/router/self-organizing-classification-plan.md Phase T4 - the two axes that phase
/// measures (predictive adequacy and estimated token-cost savings) recorded together for one row, because
/// they answer questions about the same decision and separating them would make joining them a query
/// problem for every reader.
/// </summary>
/// <param name="TranscriptId">The <c>request_transcripts</c> row this compares. One comparison per transcript row.</param>
/// <param name="ComparedAtUtc">When the comparison ran - not when the request was served.</param>
/// <param name="SessionId">
/// The conversation this request belonged to, denormalized from the transcript's correlation id so the
/// Cost Analytics "Routing ROI" screen can filter by session without re-parsing correlation ids.
/// </param>
/// <param name="ObservedScore">The verifier's score for the model that actually served the request.</param>
/// <param name="DimensionPredictedScore">
/// The frozen taxonomy's held-out predicted score for the served model, or <see langword="null"/> when no
/// honest leave-one-out prediction existed.
/// </param>
/// <param name="ClusterPredictedScore">The learned taxonomy's held-out predicted score, or <see langword="null"/> on the same terms.</param>
/// <param name="DimensionAbsoluteError">
/// <c>|observed - dimension predicted|</c>, or <see langword="null"/> when the prediction was unavailable.
/// Stored rather than derived so a reader aggregating a window never has to re-decide how a missing
/// prediction is handled.
/// </param>
/// <param name="ClusterAbsoluteError">The learned taxonomy's absolute error, on the same terms.</param>
/// <param name="IsClustered">
/// Whether this request received a non-abstaining cluster assignment - the numerator of the cluster
/// coverage rate the promotion criterion reads.
/// </param>
/// <param name="IsExploratory">
/// Whether the routing decision was an epsilon-greedy probe. Every row carries this label, per the plan's
/// "every row is labeled exploration versus exploitation" ground rule.
/// </param>
/// <param name="RoutedModel">The model that actually served the request.</param>
/// <param name="BaselineModel">
/// The model <c>dim_best</c> alone would have chosen, or <see langword="null"/> when it abstained - in
/// which case no savings figure is computed for this row.
/// </param>
/// <param name="ActualCostUsd">What serving this request actually cost, or <see langword="null"/> when unknown.</param>
/// <param name="BaselineEstimatedCostUsd">
/// What <see cref="BaselineModel"/> would have cost. <b>An estimate</b>: the counterfactual's true token
/// count is never observed, so this prices that model's own observed-average token counts. Never presented
/// without that qualification.
/// </param>
/// <param name="EstimatedNetSavingsUsd">
/// <see cref="BaselineEstimatedCostUsd"/> minus <see cref="ActualCostUsd"/> - positive when routing saved
/// money against the frozen baseline, negative when it cost more. Inherits the estimate qualification.
/// </param>
/// <param name="BaselinePredictedScore">
/// The score <see cref="BaselineModel"/> would likely have achieved on this request, predicted from the
/// dimension ledger's blend (live average, else probing prior) - the same number <c>DimBestVoter</c> votes
/// on, held out when the routed model <em>is</em> the baseline so a cell never predicts from its own
/// observation. <b>An estimate</b>: the counterfactual response was never produced.
/// <see langword="null"/> when the baseline abstained or neither ledger source has the cell.
/// </param>
/// <param name="EstimatedRegret">
/// The routing decision's estimated regret against the <c>dim_best</c> baseline under the canonical
/// reward <c>r = ε₁·s + ε₂·κ</c> (docs/router/routing-roi-regret-plan.md, weights from
/// <c>RoutingOptions.Epsilon1</c>/<c>Epsilon2</c>): the baseline's estimated reward
/// (<see cref="BaselinePredictedScore"/>, <see cref="BaselineEstimatedCostUsd"/>) minus the routed pick's
/// observed reward (<see cref="ObservedScore"/>, <see cref="ActualCostUsd"/>). Positive means the
/// <c>dim_best</c> pick would likely have earned more reward; negative means routing beat it.
/// <see langword="null"/> whenever any input is missing - never fabricated. Inherits the estimate
/// qualification: the baseline half is predicted, not observed.
/// </param>
public sealed record TaxonomyComparisonRecord(
    long TranscriptId,
    DateTimeOffset ComparedAtUtc,
    string SessionId,
    double ObservedScore,
    double? DimensionPredictedScore,
    double? ClusterPredictedScore,
    double? DimensionAbsoluteError,
    double? ClusterAbsoluteError,
    bool IsClustered,
    bool IsExploratory,
    string RoutedModel,
    string? BaselineModel,
    decimal? ActualCostUsd,
    decimal? BaselineEstimatedCostUsd,
    decimal? EstimatedNetSavingsUsd,
    double? BaselinePredictedScore,
    double? EstimatedRegret);
