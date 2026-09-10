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
/// <param name="ClusterPredictedScore">
/// The learned taxonomy's held-out predicted score, or <see langword="null"/> on the
/// same terms.
/// </param>
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
/// The model an <em>untrained</em> router - one that has never read live memory - would have chosen from
/// this request's actual candidate menu, using only the frozen CodeRouterBench probing-split prior
/// (<c>UntrainedBaselineSelector</c>), or <see langword="null"/> when it abstained - in which case no
/// savings figure is computed for this row. This is the ROI cost-savings yardstick's frozen counterfactual
/// (docs/router/routing-roi-regret-plan.md's frozen-baseline correction); it does <em>not</em> use the
/// live, memory-preferring <c>dim_best</c> voter, because a baseline that learns alongside the router it is
/// measured against holds the gap between them constant and reports an improvement of zero regardless of
/// how much the router has actually learned.
/// </param>
/// <param name="ActualCostUsd">What serving this request actually cost, or <see langword="null"/> when unknown.</param>
/// <param name="BaselineEstimatedCostUsd">
/// What <see cref="BaselineModel"/> would have cost, computed from <see cref="BaselineInputTokens"/>/
/// <see cref="BaselineOutputTokens"/> and <see cref="BaselineInputPricePerMillion"/>/
/// <see cref="BaselineOutputPricePerMillion"/>. <b>An estimate</b>: the counterfactual's true token count
/// is never observed, so this prices that model's own observed-average token counts at the ingredients'
/// captured prices. Never presented without that qualification. Immutable once written - see
/// <see cref="BaselineInputTokens"/>'s remarks.
/// </param>
/// <param name="EstimatedNetSavingsUsd">
/// <see cref="BaselineEstimatedCostUsd"/> minus <see cref="ActualCostUsd"/> - positive when routing saved
/// money against the frozen baseline, negative when it cost more. Inherits the estimate qualification.
/// </param>
/// <param name="BaselinePredictedScore">
/// The score <see cref="BaselineModel"/> would likely have achieved on this request - never live memory,
/// unlike the taxonomy-accuracy comparison above. No leave-one-out correction applies: a table-only
/// prediction never absorbed this observation, because it never reads live memory at all. <b>An
/// estimate</b>: the counterfactual response was never produced.
/// <para>
/// Sourced from <c>TranscriptRecord.UntrainedBaselinePredictedScore</c> - the score read from the exact
/// frozen CodeRouterBench probing-split prior snapshot the baseline was selected from, at request time -
/// when the transcript carries one (every row written since docs/router/routing-roi-regret-plan.md's
/// frozen-baseline correction, second pass). Falls back to a fresh lookup against whatever prior is
/// currently loaded only for a legacy row that predates that column - see
/// <c>TaxonomyComparisonService.PredictBaselineScore</c>. <see langword="null"/> when the baseline
/// abstained, or (legacy rows only) the corpus is unsynced or the current prior has no average for it; a
/// modern row's persisted score is never nulled out by a later prior no longer containing the cell.
/// </para>
/// </param>
/// <param name="EstimatedRegret">
/// The routing decision's estimated regret against the untrained baseline under the canonical reward
/// <c>r = ε₁·s + ε₂·κ</c> (docs/router/routing-roi-regret-plan.md, weights from
/// <c>RoutingOptions.Epsilon1</c>/<c>Epsilon2</c>): the baseline's estimated reward
/// (<see cref="BaselinePredictedScore"/>, <see cref="BaselineEstimatedCostUsd"/>) minus the routed pick's
/// observed reward (<see cref="ObservedScore"/>, <see cref="ActualCostUsd"/>). Positive means the untrained
/// baseline would likely have earned more reward; negative means routing beat it.
/// <see langword="null"/> whenever any input is missing - never fabricated. Inherits the estimate
/// qualification: the baseline half is predicted, not observed.
/// </param>
/// <param name="BaselineInputTokens">
/// <see cref="BaselineModel"/>'s observed-average prompt token count at the moment this row was first
/// compared, the first ingredient behind <see cref="BaselineEstimatedCostUsd"/>.
/// </param>
/// <param name="BaselineOutputTokens">
/// <see cref="BaselineModel"/>'s observed-average completion token count at the moment this row was first
/// compared.
/// </param>
/// <param name="BaselineInputPricePerMillion">
/// <see cref="BaselineModel"/>'s USD-per-million-input-token catalog rate at the moment this row was first
/// compared.
/// </param>
/// <param name="BaselineOutputPricePerMillion">
/// <see cref="BaselineModel"/>'s USD-per-million-output-token catalog rate at the moment this row was
/// first compared.
/// </param>
/// <param name="BaselineTokenizerRatio">
/// The baseline-to-routed tokenizer multiplier applied to this turn's observed input tokens, or
/// <see langword="null"/> when no baseline cost was estimated.
/// </param>
/// <param name="BaselineTokenizerRatioMeasured">
/// Whether <paramref name="BaselineTokenizerRatio"/> was measured against both models' real tokenizers, or
/// assumed to be 1 because at least one was counted with a stand-in encoding. Recorded separately because a
/// ratio of 1 reads identically in both cases, and only one of them is an observation.
/// </param>
/// <remarks>
/// The four <c>Baseline*</c> cost ingredients (tokens and prices) are captured once, the first time a row
/// is compared, and the row is then never rewritten: <c>SqliteTaxonomyComparisonStore.UpsertAsync</c> is
/// first-write-wins for a row that already has a baseline recorded. Both the token averages and the price
/// catalog drift as live traffic accumulates and prices refresh, so a re-run (a rescan, a backfill) would
/// otherwise silently rewrite an already-published savings figure with today's inputs instead of the ones
/// that were actually in force - the same contamination the frozen-baseline correction exists to eliminate.
/// The ingredients are stored, not only the computed cost, so a savings figure is auditable and
/// reproducible from its own row.
/// </remarks>
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
    double? EstimatedRegret,
    double? BaselineInputTokens = null,
    double? BaselineOutputTokens = null,
    decimal? BaselineInputPricePerMillion = null,
    decimal? BaselineOutputPricePerMillion = null,
    double? BaselineTokenizerRatio = null,
    bool? BaselineTokenizerRatioMeasured = null);