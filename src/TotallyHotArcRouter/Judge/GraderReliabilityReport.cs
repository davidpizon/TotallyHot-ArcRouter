using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The full output of one <see cref="IGraderReliabilityAnalyzer.AnalyzeAsync"/> run
/// (docs/router/grader-reliability-plan.md, Phase Q4): per-dimension inter-grader agreement, verbosity
/// skew, and self-preference skew, computed purely from already-collected <c>grader_scores</c> rows. Never
/// mutates a weight; Phase Q5 decides what (if anything) to do with these numbers.
/// </summary>
/// <param name="Dimensions">One report per dimension present in the underlying data.</param>
/// <param name="GeneratedAtUtc">When this report was computed.</param>
/// <param name="TotalRowsAnalyzed">The total <c>grader_scores</c> row count this report was built from.</param>
public sealed record GraderReliabilityReport(
    IReadOnlyList<GraderReliabilityDimensionReport> Dimensions,
    DateTimeOffset GeneratedAtUtc,
    int TotalRowsAnalyzed);

/// <summary>One dimension's slice of a <see cref="GraderReliabilityReport"/>.</summary>
/// <param name="Dimension">The task dimension these statistics were computed over.</param>
/// <param name="PairAgreements">Inter-grader agreement for every grader-key pair that co-scored at least one request.</param>
/// <param name="VerbositySkews">Response-length correlation for every grader key present in this dimension.</param>
/// <param name="SelfPreferenceSkews">Self-preference skew for every grader key with a recorded backbone.</param>
public sealed record GraderReliabilityDimensionReport(
    string Dimension,
    IReadOnlyList<GraderPairAgreement> PairAgreements,
    IReadOnlyList<GraderVerbositySkew> VerbositySkews,
    IReadOnlyList<GraderSelfPreferenceSkew> SelfPreferenceSkews);

/// <summary>
/// Spearman rank correlation between two graders' scores on the same requests, within one dimension.
/// Spearman rather than Pearson because these are bounded <c>[0,1]</c> opinions that need not vary
/// linearly with each other - ICE-Score's discrete 0-4 rubric in particular shapes its scores into five
/// fixed rungs, which a rank correlation tolerates and a linear one would not.
/// </summary>
/// <param name="GraderA">The first grader key, in <see cref="GraderKeys"/> declaration order relative to <paramref name="GraderB"/>.</param>
/// <param name="GraderB">The second grader key.</param>
/// <param name="Correlation">
/// The Spearman correlation in <c>[-1,1]</c>, or <see langword="null"/> when <see cref="SampleSize"/> is
/// below <see cref="IGraderReliabilityAnalyzer.MinimumSampleSize"/> - suppressed, not zero-filled, because a
/// correlation from too few paired observations is noise, not signal.
/// </param>
/// <param name="SampleSize">The number of requests both graders scored.</param>
public sealed record GraderPairAgreement(string GraderA, string GraderB, double? Correlation, int SampleSize);

/// <summary>
/// Spearman rank correlation between one grader's score and the graded response's character length, within
/// one dimension. A grader whose score tracks length regardless of quality is verbosity-biased
/// (docs/research/code-quality-metrics-assessment.md §4: the code-specific literature's largest measured
/// bias, ahead of self-preference).
/// </summary>
/// <param name="GraderKey">The grader key.</param>
/// <param name="Correlation">
/// The Spearman correlation in <c>[-1,1]</c>, or <see langword="null"/> when <see cref="SampleSize"/> is
/// below <see cref="IGraderReliabilityAnalyzer.MinimumSampleSize"/>.
/// </param>
/// <param name="SampleSize">The number of scored rows with a recorded response length.</param>
public sealed record GraderVerbositySkew(string GraderKey, double? Correlation, int SampleSize);

/// <summary>
/// Whether a grader scores the candidate model matching its own backbone differently from every other
/// candidate, within one dimension.
/// </summary>
/// <param name="GraderKey">The grader key.</param>
/// <param name="MeanScoreDelta">
/// The mean score when the graded candidate matches the grader's own backbone, minus the mean score
/// otherwise. Positive means the grader favors its own output. <see langword="null"/> - not zero - when
/// either group is empty: a grader whose backbone never appears as a graded candidate has no self-preference
/// question to answer at all, which is a fact about the operator's model pool, not evidence of zero bias.
/// </param>
/// <param name="OwnModelSampleSize">Rows where the candidate model matched this grader's own backbone.</param>
/// <param name="OtherModelSampleSize">Rows where it did not.</param>
public sealed record GraderSelfPreferenceSkew(
    string GraderKey,
    double? MeanScoreDelta,
    int OwnModelSampleSize,
    int OtherModelSampleSize);
