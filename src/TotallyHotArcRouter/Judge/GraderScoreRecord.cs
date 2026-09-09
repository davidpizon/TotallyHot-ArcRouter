using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One grader's raw score against one request, as persisted to <c>grader_scores</c>
/// (docs/router/grader-reliability-plan.md, Phase Q4). Generalizes <see cref="JudgeShadowScoreRecord"/> from
/// "the judge vs. the static blend" to every grader that can contribute to
/// <see cref="Quality.QualityResult"/>: a request graded by four graders produces up to four rows, one per
/// grader, joined back together by <see cref="CorrelationId"/> - never one row per request.
/// </summary>
/// <param name="Id">The row's database identity; <c>0</c> for a record not yet inserted.</param>
/// <param name="CorrelationId">Correlation id shared by every grader's row for the same request.</param>
/// <param name="CreatedAtUtc">When this row was written.</param>
/// <param name="Dimension">The task dimension the request was routed under.</param>
/// <param name="Model">The candidate model that produced the graded response.</param>
/// <param name="GraderKey">
/// Which grader produced <see cref="Score"/>: one of the <see cref="GraderKeys"/> constants. <see cref="GraderKeys.Syntax"/>
/// is never used here - it is a boolean structural verdict, not a <c>[0,1]</c> opinion, and correlating a
/// bool against continuous scores would answer a different question than this table exists to answer.
/// </param>
/// <param name="Score">The grader's score in <c>[0,1]</c>.</param>
/// <param name="GraderBackboneModel">
/// The client-facing name of the backbone model that produced this score, for the judge and Phase Q3
/// portfolio graders; <see langword="null"/> for <see cref="GraderKeys.Analysis"/>, which has no LLM
/// backbone at all. Needed to compute self-preference skew: whether a grader scores its own backbone's
/// output differently from every other candidate's.
/// </param>
/// <param name="ResponseLengthChars">
/// The graded response's character length, or <see langword="null"/> when it was never captured (no LLM
/// grader was live for this request, so nothing populated the length cache). Duplicated across every row
/// sharing <see cref="CorrelationId"/> so a verbosity-skew query never needs a join back to a separate
/// per-request table.
/// </param>
public sealed record GraderScoreRecord(
    long Id,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    string Dimension,
    string Model,
    string GraderKey,
    double Score,
    string? GraderBackboneModel,
    int? ResponseLengthChars);
