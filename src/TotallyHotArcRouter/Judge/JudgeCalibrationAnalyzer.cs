using System.Globalization;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The <see cref="IJudgeCalibrationAnalyzer"/> implementation: reads every <c>judge_shadow_scores</c> row
/// once, groups it into comparable cohorts, and computes judge-vs-static agreement, the judge's own score
/// distribution, and per-candidate self-preference skew
/// (docs/router/geval-shadow-scoring-plan.md Phase G2).
/// </summary>
public sealed class JudgeCalibrationAnalyzer : IJudgeCalibrationAnalyzer
{
    private readonly IOptionsMonitor<JudgeCalibrationOptions> _options;
    private readonly IJudgeShadowScoreStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="JudgeCalibrationAnalyzer"/> class.</summary>
    /// <param name="store">Supplies every <c>judge_shadow_scores</c> row to analyze.</param>
    /// <param name="options">
    /// Supplies the score-collapse thresholds, read per run rather than captured so a retune takes effect
    /// on the next report without a restart.
    /// </param>
    /// <param name="timeProvider">
    /// Clock used to stamp <see cref="JudgeCalibrationReport.GeneratedAtUtc"/>; defaults to
    /// <see cref="TimeProvider.System"/>. Overridable for deterministic tests.
    /// </param>
    public JudgeCalibrationAnalyzer(
        IJudgeShadowScoreStore store,
        IOptionsMonitor<JudgeCalibrationOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public int MinimumSampleSize => 30;

    /// <inheritdoc/>
    public async Task<JudgeCalibrationReport> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var cohorts = rows
            .GroupBy(row => new CohortKey(
                Dimension: row.Dimension,
                JudgeModel: row.JudgeModel,
                UsedLogprobs: row.UsedLogprobs,
                StaticAuthority: ToAuthority(row.SyntaxAuthoritative)))
            .Select(AnalyzeCohort)
            .OrderBy(c => c.Dimension, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.JudgeModel, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(c => c.UsedLogprobs)
            .ThenBy(c => c.StaticAuthority)
            .ToList();

        return new JudgeCalibrationReport(
            Cohorts: cohorts,
            SelfPreference: ComputeSelfPreference(rows),
            Verdicts: BuildVerdicts(cohorts),
            GeneratedAtUtc: _timeProvider.GetUtcNow(),
            TotalRowsAnalyzed: rows.Count);
    }

    /// <summary>
    /// Maps a row's nullable <c>syntax_authoritative</c> column onto the reported cohort bucket. A row
    /// written before the column existed is <see cref="StaticGradeAuthority.Unknown"/> rather than assumed
    /// either way - see that member's remarks.
    /// </summary>
    private static StaticGradeAuthority ToAuthority(bool? syntaxAuthoritative)
    {
        return syntaxAuthoritative switch
        {
            true => StaticGradeAuthority.Authoritative,
            false => StaticGradeAuthority.Heuristic,
            null => StaticGradeAuthority.Unknown
        };
    }

    /// <summary>Computes one cohort's agreement and distribution statistics from its rows.</summary>
    private JudgeCalibrationCohort AnalyzeCohort(IGrouping<CohortKey, JudgeShadowScoreRecord> group)
    {
        var judgeScores = group.Select(row => row.JudgeScore).ToList();
        var staticScores = group.Select(row => row.StaticScore).ToList();

        var agreement = new JudgeAgreement(
            // Suppressed below the floor for the same reason GraderPairAgreement.Correlation is: a rank
            // correlation over a handful of points is noise dressed as precision. The mean absolute
            // difference beside it is not suppressed - a mean is meaningful from the first row.
            Correlation: judgeScores.Count >= MinimumSampleSize
                ? RankCorrelation.Spearman(judgeScores, staticScores)
                : null,
            MeanAbsoluteDifference: RankCorrelation.MeanAbsoluteDifference(judgeScores, staticScores),
            MeanJudgeScore: judgeScores.Count > 0 ? judgeScores.Average() : null,
            MeanStaticScore: staticScores.Count > 0 ? staticScores.Average() : null);

        var distribution = new JudgeScoreDistribution(
            StandardDeviation: RankCorrelation.StandardDeviation(judgeScores),
            ModalShare: RankCorrelation.ModalShare(judgeScores),
            DistinctScoreCount: judgeScores.Select(score => Math.Round(score, digits: 3)).Distinct().Count());

        return new JudgeCalibrationCohort(
            Dimension: group.Key.Dimension,
            JudgeModel: group.Key.JudgeModel,
            UsedLogprobs: group.Key.UsedLogprobs,
            StaticAuthority: group.Key.StaticAuthority,
            Agreement: agreement,
            Distribution: distribution,
            SampleSize: judgeScores.Count);
    }

    /// <summary>
    /// For each judge backbone, compares its mean score against the static verifier's mean score for every
    /// candidate model it graded - the judge-minus-static delta per candidate, computed within the same
    /// (dimension, judge model, logprobs, static authority) cohort as everything else in the report.
    /// </summary>
    /// <remarks>
    /// The delta is against the static grade rather than against the judge's other candidates, because the
    /// question is whether the judge departs from the non-judge grader <i>differently</i> for one model
    /// than for another. A judge that simply grades generously across the board shifts every row's delta
    /// equally and is not exhibiting self-preference; a judge whose delta is far larger for one model is.
    /// Reported flat, without a verdict - see <see cref="JudgeSelfPreferenceRow"/>'s remarks.
    /// </remarks>
    /// <remarks>
    /// Grouped on all four cohort keys plus the candidate model, not just (judge model, candidate model):
    /// pooling across dimensions or weighting modes would let a judge's own backbone appear preferred
    /// merely because one cohort has a different score distribution from another, which is exactly the
    /// confound <see cref="JudgeCalibrationReport"/>'s "segmented, never pooled" rule exists to rule out.
    /// </remarks>
    private static List<JudgeSelfPreferenceRow> ComputeSelfPreference(IReadOnlyList<JudgeShadowScoreRecord> rows)
    {
        return
        [
            .. rows
                .GroupBy(row => new CohortKey(
                    Dimension: row.Dimension,
                    JudgeModel: row.JudgeModel,
                    UsedLogprobs: row.UsedLogprobs,
                    StaticAuthority: ToAuthority(row.SyntaxAuthoritative)))
                .SelectMany(cohort => cohort
                    .GroupBy(row => row.Model)
                    .Select(group => new JudgeSelfPreferenceRow(
                        Dimension: cohort.Key.Dimension,
                        JudgeModel: cohort.Key.JudgeModel,
                        UsedLogprobs: cohort.Key.UsedLogprobs,
                        StaticAuthority: cohort.Key.StaticAuthority,
                        CandidateModel: group.Key,
                        MeanScoreDelta: group.Average(row => row.JudgeScore) - group.Average(row => row.StaticScore),
                        IsOwnBackbone: string.Equals(cohort.Key.JudgeModel, group.Key,
                            StringComparison.OrdinalIgnoreCase),
                        SampleSize: group.Count())))
                .OrderBy(row => row.Dimension, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.JudgeModel, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(row => row.UsedLogprobs)
                .ThenBy(row => row.StaticAuthority)
                .ThenByDescending(row => row.MeanScoreDelta)
        ];
    }

    /// <summary>
    /// Builds the outcome of every G3 gate condition: the permanently unevaluable ground-truth condition,
    /// the evaluable score-collapse condition, and self-preference's deliberate absence of a verdict.
    /// </summary>
    private List<JudgeCalibrationVerdict> BuildVerdicts(IReadOnlyList<JudgeCalibrationCohort> cohorts)
    {
        return
        [
            new JudgeCalibrationVerdict(
                Condition: "execution-ground-truth",
                Kind: JudgeCalibrationVerdictKind.Unevaluable,
                Detail:
                "G3's first gate condition required the judge to rank-correlate with execution-grounded scores. "
                + "Code execution was removed from the project, so no such rows exist or can be produced. The judge "
                + "was promoted (G3) without this evidence; that is a recorded loss, not a pending measurement."),
            BuildScoreCollapseVerdict(cohorts),
            new JudgeCalibrationVerdict(
                Condition: "self-preference",
                Kind: JudgeCalibrationVerdictKind.Unevaluable,
                Detail:
                "Reported as numbers without a pass/fail ceiling. G-Eval documents the direction of this bias "
                + "(arXiv:2303.16634) but publishes no magnitude, and the only local yardstick - the static score - is "
                + "itself heuristic on Python and shell rows, so any ceiling would be invented. Re-open once several "
                + "candidate models have accumulated rows and the spread across deltas supplies an empirical baseline.")
        ];
    }

    /// <summary>
    /// Evaluates G3's second gate condition - a non-degenerate judge score distribution - across every
    /// cohort large enough to judge, failing if any one of them collapsed.
    /// </summary>
    /// <remarks>
    /// Per-cohort rather than pooled, and that is the point of the check. Pooling would hide the exact
    /// failure G-Eval predicts: rows scored through the single-sample fallback
    /// (<c>used_logprobs = false</c>) are the ones expected to collapse, and averaging them together with
    /// probability-weighted rows would let the healthy cohort's spread mask the degenerate one's. Because
    /// both cohorts sit in the same table, the report needs no absolute standard of "enough variety" to be
    /// informative - the reader can compare the two directly - but a threshold is still applied so the
    /// check can fail on its own, including when every cohort happens to be a fallback cohort.
    /// </remarks>
    private JudgeCalibrationVerdict BuildScoreCollapseVerdict(IReadOnlyList<JudgeCalibrationCohort> cohorts)
    {
        var options = _options.CurrentValue;
        var evaluable = cohorts.Where(c => c.SampleSize >= MinimumSampleSize).ToList();

        if (evaluable.Count == 0)
            return new JudgeCalibrationVerdict(
                Condition: "score-collapse",
                Kind: JudgeCalibrationVerdictKind.Insufficient,
                Detail: string.Create(CultureInfo.InvariantCulture,
                        $"No cohort has reached {MinimumSampleSize} rows yet ({cohorts.Count} cohort(s) present).")
                    + " Re-run once the judge has graded more traffic.");

        var collapsed = evaluable
            .Where(c => c.Distribution.StandardDeviation < options.MinimumStandardDeviation ||
                        c.Distribution.ModalShare > options.MaximumModalShare)
            .ToList();

        var floor = options.MinimumStandardDeviation.ToString(format: "F3", provider: CultureInfo.InvariantCulture);
        var ceiling = options.MaximumModalShare.ToString(format: "P0", provider: CultureInfo.InvariantCulture);

        if (collapsed.Count == 0)
            return new JudgeCalibrationVerdict(
                Condition: "score-collapse",
                Kind: JudgeCalibrationVerdictKind.Pass,
                Detail: string.Create(CultureInfo.InvariantCulture,
                    $"All {evaluable.Count} cohort(s) of at least {MinimumSampleSize} rows keep a standard deviation of at least {floor} and a modal share of at most {ceiling}."));

        return new JudgeCalibrationVerdict(
            Condition: "score-collapse",
            Kind: JudgeCalibrationVerdictKind.Fail,
            Detail: string.Create(CultureInfo.InvariantCulture,
                    $"{collapsed.Count} of {evaluable.Count} evaluable cohort(s) collapsed (standard deviation below {floor} or modal share above {ceiling}): ")
                + string.Join(separator: "; ", values: collapsed.Select(DescribeCollapsedCohort)) + ".");
    }

    /// <summary>Names one collapsed cohort and the numbers that condemned it, for a verdict's detail line.</summary>
    private static string DescribeCollapsedCohort(JudgeCalibrationCohort cohort)
    {
        var weighting = cohort.UsedLogprobs ? "logprobs" : "single-sample";
        return string.Create(CultureInfo.InvariantCulture,
            $"{cohort.Dimension}/{cohort.JudgeModel} ({weighting}, {cohort.StaticAuthority}) sd={cohort.Distribution.StandardDeviation:F3}, modal={cohort.Distribution.ModalShare:P0}, n={cohort.SampleSize}");
    }

    /// <summary>
    /// The four fields that make a set of shadow rows comparable with one another. Grouping on all four at
    /// once is what enforces <see cref="JudgeCalibrationReport"/>'s "segmented, never pooled" rule
    /// structurally, rather than leaving each statistic to remember to segment itself.
    /// </summary>
    /// <param name="Dimension">The task dimension.</param>
    /// <param name="JudgeModel">The backbone that produced the judge scores.</param>
    /// <param name="UsedLogprobs">Whether the scores were probability-weighted.</param>
    /// <param name="StaticAuthority">Whether a real parser produced the static grades.</param>
    private readonly record struct CohortKey(
        string Dimension,
        string JudgeModel,
        bool UsedLogprobs,
        StaticGradeAuthority StaticAuthority);
}
