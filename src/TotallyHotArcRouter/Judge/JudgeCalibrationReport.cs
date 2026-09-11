namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The full output of one <see cref="IJudgeCalibrationAnalyzer.AnalyzeAsync"/> run
/// (docs/router/geval-shadow-scoring-plan.md Phase G2): how the G-Eval judge's opinion compares with the
/// static verifier's grade on the same requests, computed purely from already-collected
/// <c>judge_shadow_scores</c> rows. Read-only - nothing here reads or writes a weight, and the judge it
/// describes is already live (G3 shipped), so this is a standing regression check rather than the gate G2
/// was originally designed as.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every statistic is segmented, never pooled.</b> Phase G2's revision note requires grouping by
/// <c>judge_model</c> - <see cref="JudgeModelSelector"/> resolves a backbone per call from the operator's
/// own provider list, and toggling a provider mid-accumulation silently changes it, so a pooled
/// correlation would blend two models' calibration into one meaningless number. <c>used_logprobs</c> gets
/// the same treatment for the same reason: rows scored through the single-sample fallback carry exactly
/// the quantization noise probability weighting exists to remove (docs/research/2303.16634v3.md), so they
/// are not comparable with logprob-weighted rows.
/// </para>
/// <para>
/// <b>The G3 gate's first condition is absent by necessity, not oversight.</b> It required the judge to
/// rank-correlate with ground truth on execution-grounded rows; code execution was removed from the
/// project entirely, so no such rows exist or can exist. <see cref="Verdicts"/> records that as
/// permanently unevaluable rather than silently omitting it - the judge was promoted without the evidence
/// this phase was designed to demand, and the report says so every time it runs.
/// </para>
/// </remarks>
/// <param name="Cohorts">One slice per (dimension, judge model, logprobs, authoritativeness) cohort present in the data.</param>
/// <param name="SelfPreference">Per-candidate-model judge-minus-static skew, reported without a verdict - see <see cref="JudgeSelfPreferenceRow"/>.</param>
/// <param name="Verdicts">The pass/fail outcome of every G3 gate condition that remains evaluable.</param>
/// <param name="GeneratedAtUtc">When this report was computed.</param>
/// <param name="TotalRowsAnalyzed">The total <c>judge_shadow_scores</c> row count this report was built from.</param>
public sealed record JudgeCalibrationReport(
    IReadOnlyList<JudgeCalibrationCohort> Cohorts,
    IReadOnlyList<JudgeSelfPreferenceRow> SelfPreference,
    IReadOnlyList<JudgeCalibrationVerdict> Verdicts,
    DateTimeOffset GeneratedAtUtc,
    int TotalRowsAnalyzed);

/// <summary>
/// Whether a real parser produced the static grade a judge score is being compared against, which decides
/// how much the comparison is worth. Replaces the <c>executed</c> flag G2 was originally designed around:
/// execution was removed, and this is the nearest remaining proxy for "how much do we trust the non-judge
/// number".
/// </summary>
public enum StaticGradeAuthority
{
    /// <summary>
    /// The row predates the <c>syntax_authoritative</c> column and carries no value. Reported as its own
    /// cohort rather than folded into either side: guessing would put heuristic-graded rows into the
    /// trusted bucket half the time, which is the exact error the split exists to prevent.
    /// </summary>
    Unknown = 0,

    /// <summary>A heuristic produced the static grade (Python, shell) - an opinion, weighted at half by <c>QualityScorer</c>.</summary>
    Heuristic = 1,

    /// <summary>A real parser produced the static grade (Roslyn for C#, Acornima for JS/TS) - a fact.</summary>
    Authoritative = 2
}

/// <summary>
/// One comparable slice of the shadow table: all rows sharing a dimension, a judge backbone, a
/// probability-weighting mode, and a static-grade authority. Statistics are only ever computed within a
/// cohort, never across them - see <see cref="JudgeCalibrationReport"/>'s remarks.
/// </summary>
/// <param name="Dimension">The task dimension these rows were graded under.</param>
/// <param name="JudgeModel">The backbone that actually produced these judge scores, as resolved per call.</param>
/// <param name="UsedLogprobs">Whether these scores were probability-weighted rather than parsed from a single sample.</param>
/// <param name="StaticAuthority">Whether a real parser produced the static grades being compared against.</param>
/// <param name="Agreement">How closely the judge tracks the static grade on these rows.</param>
/// <param name="Distribution">The shape of the judge's own score distribution on these rows - G-Eval's score-collapse check.</param>
/// <param name="SampleSize">The number of rows in this cohort.</param>
public sealed record JudgeCalibrationCohort(
    string Dimension,
    string JudgeModel,
    bool UsedLogprobs,
    StaticGradeAuthority StaticAuthority,
    JudgeAgreement Agreement,
    JudgeScoreDistribution Distribution,
    int SampleSize);

/// <summary>
/// How closely the judge's score tracks the static verifier's on the same requests, within one cohort.
/// </summary>
/// <remarks>
/// Both numbers are reported because they answer different questions and can disagree informatively. A
/// judge that ranks responses correctly but sits a constant 0.2 above the static grade has a high
/// <paramref name="Correlation"/> and a large <paramref name="MeanAbsoluteDifference"/> - it is useful for
/// choosing between models and miscalibrated in absolute terms, which is exactly the state a blended
/// <c>u_i</c> cares about.
/// </remarks>
/// <param name="Correlation">
/// The Spearman rank correlation between judge and static score in <c>[-1,1]</c>, or
/// <see langword="null"/> when the cohort is below
/// <see cref="IJudgeCalibrationAnalyzer.MinimumSampleSize"/> or either series has zero variance - see
/// <see cref="RankCorrelation.Pearson"/> for why the latter is suppressed rather than reported as 0.0.
/// </param>
/// <param name="MeanAbsoluteDifference">
/// The mean of <c>|judge − static|</c> across the cohort, or <see langword="null"/> for an empty one.
/// Reported at every sample size, unlike <paramref name="Correlation"/>: a mean is meaningful from the
/// first row, where a rank correlation from three points is noise dressed as precision.
/// </param>
/// <param name="MeanJudgeScore">The cohort's mean judge score, or <see langword="null"/> for an empty cohort.</param>
/// <param name="MeanStaticScore">The cohort's mean static score, or <see langword="null"/> for an empty cohort.</param>
public sealed record JudgeAgreement(
    double? Correlation,
    double? MeanAbsoluteDifference,
    double? MeanJudgeScore,
    double? MeanStaticScore);

/// <summary>
/// The shape of the judge's own score distribution within one cohort - G-Eval's documented score-collapse
/// failure, where the backbone answers the same digit to nearly everything while its mean stays perfectly
/// plausible (docs/research/2303.16634v3.md §"Score collapse"). This is what makes the failure visible: a
/// mean cannot show it, and a correlation against a collapsed series is undefined rather than alarming.
/// </summary>
/// <param name="StandardDeviation">
/// The population standard deviation of the judge's scores, or <see langword="null"/> for an empty
/// cohort. Near zero means the judge is answering the same thing regardless of input.
/// </param>
/// <param name="ModalShare">
/// The proportion of the cohort taken by its single most common score (bucketed to three decimals), in
/// <c>(0,1]</c>, or <see langword="null"/> for an empty cohort. Catches what the standard deviation misses:
/// 3, 3, 3, 3, 5 has a respectable spread supplied entirely by one outlier.
/// </param>
/// <param name="DistinctScoreCount">The number of distinct scores (bucketed to three decimals) present in the cohort.</param>
public sealed record JudgeScoreDistribution(
    double? StandardDeviation,
    double? ModalShare,
    int DistinctScoreCount);

/// <summary>
/// One candidate model's judge-minus-static skew: how much more (or less) generously the judge grades
/// this model's output than the static verifier does, within one judge backbone.
/// </summary>
/// <remarks>
/// <b>Reported without a pass/fail verdict, deliberately.</b> G-Eval's self-preference finding is
/// directional - the paper's evaluator scored LLM-written text above human-written text <i>always</i>,
/// including where human raters disagreed - but it publishes no magnitude, so there is no anchor for a
/// ceiling. Nor is there a clean comparison group in this data: the only available yardstick is the static
/// score, which is itself heuristic on Python and shell rows. A threshold here would be invented, and an
/// invented threshold printed as a FAIL is worse than no verdict, because it looks like a measurement.
/// The re-open condition is real traffic: once several models have accumulated rows, the spread across
/// <see cref="MeanScoreDelta"/> values becomes an empirical baseline a ceiling can be set against.
/// </remarks>
/// <param name="Dimension">The task dimension these rows were graded under - one of the four cohort keys, carried here so this row is never read as pooled across dimensions.</param>
/// <param name="JudgeModel">The backbone whose grading is described.</param>
/// <param name="UsedLogprobs">Whether these scores were probability-weighted - the second cohort key.</param>
/// <param name="StaticAuthority">Whether a real parser produced the static grades being compared against - the third cohort key.</param>
/// <param name="CandidateModel">The model whose responses were graded.</param>
/// <param name="MeanScoreDelta">
/// The mean judge score minus the mean static score for this candidate, within this cohort. Positive means
/// the judge is more generous to this model than the static verifier is.
/// </param>
/// <param name="IsOwnBackbone">
/// Whether <paramref name="CandidateModel"/> is the judge's own backbone - the self-preference case
/// G-Eval names directly. A row where this is true and <paramref name="MeanScoreDelta"/> stands well above
/// the other rows' is the paper's finding reproduced locally.
/// </param>
/// <param name="SampleSize">The number of rows behind this candidate's means.</param>
public sealed record JudgeSelfPreferenceRow(
    string Dimension,
    string JudgeModel,
    bool UsedLogprobs,
    StaticGradeAuthority StaticAuthority,
    string CandidateModel,
    double MeanScoreDelta,
    bool IsOwnBackbone,
    int SampleSize);

/// <summary>Whether one G3 gate condition passed, failed, or cannot be evaluated at all.</summary>
public enum JudgeCalibrationVerdictKind
{
    /// <summary>The condition was evaluated and met.</summary>
    Pass = 0,

    /// <summary>The condition was evaluated and not met.</summary>
    Fail = 1,

    /// <summary>Not enough data to evaluate the condition yet; it will be evaluated once volume accumulates.</summary>
    Insufficient = 2,

    /// <summary>
    /// The condition can never be evaluated, because the evidence it requires no longer exists. Distinct
    /// from <see cref="Insufficient"/>: waiting will not change it.
    /// </summary>
    Unevaluable = 3
}

/// <summary>
/// One G3 gate condition's outcome. The gate itself is historical - G3 shipped, so the judge is already
/// influencing routing - but conditions that remain measurable are worth running as a standing regression
/// check against an already-promoted grader.
/// </summary>
/// <param name="Condition">A short identifier for the condition (e.g. <c>score-collapse</c>).</param>
/// <param name="Kind">Whether the condition passed, failed, lacks data, or is permanently unevaluable.</param>
/// <param name="Detail">
/// A human-readable explanation naming the cohorts and numbers behind the outcome, so the verdict can be
/// argued with rather than merely believed.
/// </param>
public sealed record JudgeCalibrationVerdict(string Condition, JudgeCalibrationVerdictKind Kind, string Detail);
