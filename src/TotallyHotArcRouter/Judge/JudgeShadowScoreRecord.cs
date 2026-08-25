namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One persisted row of <c>judge_shadow_scores</c>: the judge's opinion recorded alongside the static
/// verifier's score for the same request, for agreement analysis.
/// </summary>
/// <remarks>
/// <b>These rows are the audit trail, not the routing path.</b> The judge's grade now also reaches router
/// memory - blended with the static score by <see cref="Quality.Grading.IQualityScoreAggregator"/> into the
/// single observation the router learns from - but it arrives there through the aggregator, never by a
/// reader of this table. Keeping the two scores side by side here is what makes the blend auditable after
/// the fact: the row shows what each grader independently thought before they were combined.
/// </remarks>
/// <param name="Id">The store-assigned identity. Zero for a row not yet persisted.</param>
/// <param name="CorrelationId">Correlation id shared with the scored request's routing telemetry.</param>
/// <param name="CreatedAtUtc">When this row was written, in UTC.</param>
/// <param name="Dimension">The inferred task dimension.</param>
/// <param name="Model">The model that produced the evaluated response.</param>
/// <param name="StaticScore">The static verifier's unified score for the same request, before the judge's grade was blended in.</param>
/// <param name="JudgeScore">The G-Eval judge's score, normalized to <c>[0, 1]</c>.</param>
/// <param name="JudgeModel">
/// The client-facing name of the model that actually judged this response, as
/// <see cref="JudgeModelSelector"/> resolved it at scoring time. Recording what ran - rather than what was
/// configured - matters because the selector substitutes a fallback for an ineligible pick, and G2's
/// agreement analysis has to segment on the backbone that produced each score.
/// </param>
/// <param name="JudgePromptVersion">The prompt-version tag (<see cref="JudgeOptions.PromptVersion"/> at scoring time), the auto-CoT cache guard equivalent.</param>
/// <param name="JudgeLatencyMs">Wall-clock duration of the judge HTTP call, in milliseconds.</param>
/// <param name="UsedLogprobs">Whether <see cref="JudgeScore"/> was computed via probability weighting rather than the single-sample fallback.</param>
public sealed record JudgeShadowScoreRecord(
    long Id,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    string Dimension,
    string Model,
    double StaticScore,
    double JudgeScore,
    string JudgeModel,
    string JudgePromptVersion,
    long JudgeLatencyMs,
    bool UsedLogprobs);
