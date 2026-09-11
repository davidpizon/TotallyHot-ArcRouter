using System.Globalization;
using System.Text;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Renders a <see cref="JudgeCalibrationReport"/> as Markdown for Phase G2's two surfaces - the
/// <c>--run-judge-calibration-report</c> CLI flag and the Governance UI's Judge Calibration panel
/// (docs/router/geval-shadow-scoring-plan.md Phase G2).
/// </summary>
/// <remarks>
/// One formatter rather than one per surface, following <c>RegretComparisonReportBuilder</c>'s
/// "publish these numbers" convention. The point is not to save code: a CLI table and a GUI table that
/// format the same report independently will eventually round, suppress, or label something differently,
/// and the operator comparing a panel against a piped CLI run has no way to tell an arithmetic difference
/// from a rendering one.
/// </remarks>
public static class JudgeCalibrationReportFormatter
{
    /// <summary>Formats the whole report - verdicts, cohorts, and self-preference - as Markdown.</summary>
    /// <param name="report">The report to render.</param>
    /// <returns>The report as Markdown, ending in a newline.</returns>
    public static string FormatMarkdown(JudgeCalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture,
            $"Generated {report.GeneratedAtUtc:u} from {report.TotalRowsAnalyzed} judge_shadow_scores row(s).");
        builder.AppendLine();
        builder.AppendLine();

        AppendVerdicts(builder: builder, report: report);
        AppendCohorts(builder: builder, report: report);
        AppendSelfPreference(builder: builder, report: report);

        return builder.ToString();
    }

    /// <summary>Appends the G3 gate conditions and their outcomes.</summary>
    private static void AppendVerdicts(StringBuilder builder, JudgeCalibrationReport report)
    {
        builder.AppendLine("### Gate conditions");
        builder.AppendLine();
        builder.AppendLine("| Condition | Verdict | Detail |");
        builder.AppendLine("|---|---|---|");
        foreach (var verdict in report.Verdicts)
            builder.AppendLine(
                $"| {EscapeCell(verdict.Condition)} | {DescribeVerdict(verdict.Kind)} | {EscapeCell(verdict.Detail)} |");
        builder.AppendLine();
    }

    /// <summary>Appends the per-cohort agreement and distribution table, or a note when no rows exist.</summary>
    private static void AppendCohorts(StringBuilder builder, JudgeCalibrationReport report)
    {
        builder.AppendLine("### Judge vs. static agreement, by cohort");
        builder.AppendLine();

        if (report.Cohorts.Count == 0)
        {
            builder.AppendLine("No shadow rows yet. The judge records one per graded request while it is enabled.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine(
            "| Dimension | Judge model | Weighting | Static grade | N | Spearman | MAD | Mean judge | Mean static | SD | Modal share | Distinct |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var cohort in report.Cohorts)
            builder.AppendLine(
                $"| {EscapeCell(cohort.Dimension)} "
                + $"| {EscapeCell(cohort.JudgeModel)} "
                + $"| {(cohort.UsedLogprobs ? "logprobs" : "single-sample")} "
                + $"| {DescribeAuthority(cohort.StaticAuthority)} "
                + $"| {cohort.SampleSize} "
                + $"| {FormatCorrelation(cohort.Agreement.Correlation)} "
                + $"| {FormatValue(cohort.Agreement.MeanAbsoluteDifference)} "
                + $"| {FormatValue(cohort.Agreement.MeanJudgeScore)} "
                + $"| {FormatValue(cohort.Agreement.MeanStaticScore)} "
                + $"| {FormatValue(cohort.Distribution.StandardDeviation)} "
                + $"| {FormatShare(cohort.Distribution.ModalShare)} "
                + $"| {cohort.Distribution.DistinctScoreCount} |");
        builder.AppendLine();
    }

    /// <summary>
    /// Appends the per-candidate self-preference table, prefaced by the standing note that it carries no
    /// verdict on purpose - so the numbers are never read as having silently passed something.
    /// </summary>
    private static void AppendSelfPreference(StringBuilder builder, JudgeCalibrationReport report)
    {
        builder.AppendLine("### Self-preference (judge minus static, per candidate model)");
        builder.AppendLine();
        builder.AppendLine(
            "Reported without a pass/fail ceiling - G-Eval documents this bias's direction but no magnitude, "
            + "and the static score is itself heuristic on some rows. A judge's own backbone appearing at the "
            + "top of its list is the paper's finding reproduced locally.");
        builder.AppendLine();

        if (report.SelfPreference.Count == 0)
        {
            builder.AppendLine("No shadow rows yet.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine(
            "| Dimension | Judge model | Weighting | Static grade | Candidate model | Own backbone | Mean delta | N |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var row in report.SelfPreference)
            builder.AppendLine(
                $"| {EscapeCell(row.Dimension)} "
                + $"| {EscapeCell(row.JudgeModel)} "
                + $"| {(row.UsedLogprobs ? "logprobs" : "single-sample")} "
                + $"| {DescribeAuthority(row.StaticAuthority)} "
                + $"| {EscapeCell(row.CandidateModel)} "
                + $"| {(row.IsOwnBackbone ? "yes" : "no")} "
                + $"| {row.MeanScoreDelta.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)} "
                + $"| {row.SampleSize} |");
        builder.AppendLine();
    }

    /// <summary>Names a verdict kind for display, spelling out the two different reasons a check produced no verdict.</summary>
    private static string DescribeVerdict(JudgeCalibrationVerdictKind kind)
    {
        return kind switch
        {
            JudgeCalibrationVerdictKind.Pass => "PASS",
            JudgeCalibrationVerdictKind.Fail => "FAIL",
            JudgeCalibrationVerdictKind.Insufficient => "not enough data yet",
            JudgeCalibrationVerdictKind.Unevaluable => "permanently unevaluable",
            _ => "unknown"
        };
    }

    /// <summary>Names a static-grade authority bucket for display.</summary>
    private static string DescribeAuthority(StaticGradeAuthority authority)
    {
        return authority switch
        {
            StaticGradeAuthority.Authoritative => "parser",
            StaticGradeAuthority.Heuristic => "heuristic",
            StaticGradeAuthority.Unknown => "unknown (pre-column)",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Formats a correlation, naming why it is absent rather than printing a blank cell - a suppressed
    /// statistic and a computed zero are opposite findings and must never look alike.
    /// </summary>
    private static string FormatCorrelation(double? correlation)
    {
        return correlation is { } value ? value.ToString("F3", CultureInfo.InvariantCulture) : "suppressed";
    }

    /// <summary>Formats a nullable score-scale value to three decimals, or an em dash when undefined.</summary>
    private static string FormatValue(double? value)
    {
        return value is { } number ? number.ToString("F3", CultureInfo.InvariantCulture) : "-";
    }

    /// <summary>Formats a nullable proportion as a whole percentage, or an em dash when undefined.</summary>
    private static string FormatShare(double? share)
    {
        return share is { } value ? value.ToString("P0", CultureInfo.InvariantCulture) : "-";
    }

    /// <summary>
    /// Escapes a free-text table cell so it cannot break the Markdown table it sits in: pipes are escaped
    /// (a raw <c>|</c> would be read as a new column boundary) and embedded newlines are collapsed to a
    /// space (a raw newline would be read as the end of the row). Applied to every cell whose value is
    /// operator-controlled free text - a dimension name, a model id, a verdict condition, or a detail
    /// message - since routing validation only rejects blank/duplicate values, not table-breaking
    /// characters. Cells derived internally (numbers, enum labels) never need this.
    /// </summary>
    private static string EscapeCell(string text)
    {
        return text
            .Replace(oldValue: "|", newValue: @"\|", comparisonType: StringComparison.Ordinal)
            .Replace(oldValue: "\r\n", newValue: " ", comparisonType: StringComparison.Ordinal)
            .Replace(oldValue: "\n", newValue: " ", comparisonType: StringComparison.Ordinal)
            .Replace(oldValue: "\r", newValue: " ", comparisonType: StringComparison.Ordinal);
    }
}
