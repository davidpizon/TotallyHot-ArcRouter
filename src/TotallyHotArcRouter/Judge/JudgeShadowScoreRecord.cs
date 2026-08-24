namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One persisted row of <c>judge_shadow_scores</c> (docs/router/geval-shadow-scoring-plan.md §1d): the
/// judge's opinion recorded alongside the Verifier's own score for the same request, for later agreement
/// analysis (Phase G2). Never influences routing or <c>memory_entries</c> - shadow rows are read-only
/// telemetry until Phase G3.
/// </summary>
/// <param name="Id">The store-assigned identity. Zero for a row not yet persisted.</param>
/// <param name="CorrelationId">Correlation id shared with the scored request's routing telemetry.</param>
/// <param name="CreatedAtUtc">When this row was written, in UTC.</param>
/// <param name="Dimension">The inferred task dimension.</param>
/// <param name="Model">The model that produced the evaluated response.</param>
/// <param name="VerifierScore">The Verifier's own unified score for the same request.</param>
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
/// <param name="Executed">Whether <see cref="VerifierScore"/> was execution-grounded (Tier &gt; 0) rather than Tier-0-only - the split G2's analysis needs.</param>
public sealed record JudgeShadowScoreRecord(
    long Id,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    string Dimension,
    string Model,
    double VerifierScore,
    double JudgeScore,
    string JudgeModel,
    string JudgePromptVersion,
    long JudgeLatencyMs,
    bool UsedLogprobs,
    bool Executed);
