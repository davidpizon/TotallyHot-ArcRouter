using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeCalibrationReportFormatter"/> - the single rendering shared by Phase G2's CLI
/// flag and its Governance panel. The assertions here are about honesty in presentation rather than
/// layout: a suppressed statistic must not read as a measured zero, a verdict that cannot be evaluated
/// must not read as a pass, and free text must not be able to break the table it sits in.
/// </summary>
public class JudgeCalibrationReportFormatterTests
{
    [Fact]
    public void FormatMarkdown_SuppressedCorrelation_SaysSoRatherThanPrintingZero()
    {
        var report = MakeReport(cohorts: [MakeCohort(correlation: null)]);

        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(report);

        Assert.Contains(expectedSubstring: "suppressed", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "| 0.000 |", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_MeasuredZeroCorrelation_IsRenderedAsANumber()
    {
        // The counterpart to the test above: 0.000 is a real finding ("judge and static are unrelated")
        // and must survive as one rather than being swept into the same word as "we could not compute it".
        var report = MakeReport(cohorts: [MakeCohort(correlation: 0.0)]);

        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(report);

        Assert.Contains(expectedSubstring: "0.000", actualString: markdown, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_EmptyReport_SaysThereAreNoRowsRatherThanRenderingAnEmptyTable()
    {
        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(MakeReport());

        Assert.Contains(expectedSubstring: "No shadow rows yet", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_AlwaysCarriesTheSelfPreferenceCaveat()
    {
        // Whether or not any rows exist: numbers with no verdict beside them must never be mistaken for
        // numbers that quietly passed one.
        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(MakeReport());

        Assert.Contains(expectedSubstring: "without a pass/fail ceiling", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(JudgeCalibrationVerdictKind.Pass, "PASS")]
    [InlineData(JudgeCalibrationVerdictKind.Fail, "FAIL")]
    [InlineData(JudgeCalibrationVerdictKind.Insufficient, "not enough data yet")]
    [InlineData(JudgeCalibrationVerdictKind.Unevaluable, "permanently unevaluable")]
    public void FormatMarkdown_DistinguishesEveryVerdictKind(JudgeCalibrationVerdictKind kind, string expected)
    {
        // "Not enough data yet" and "permanently unevaluable" must never collapse into one label - one
        // resolves with traffic and the other never will.
        var report = MakeReport(verdicts:
            [new JudgeCalibrationVerdict(Condition: "c", Kind: kind, Detail: "d")]);

        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(report);

        Assert.Contains(expectedSubstring: expected, actualString: markdown, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_PipeInVerdictDetail_IsEscapedRatherThanBreakingTheTable()
    {
        var report = MakeReport(verdicts:
        [
            new JudgeCalibrationVerdict(Condition: "score-collapse", Kind: JudgeCalibrationVerdictKind.Fail,
                Detail: "cohort a|b collapsed")
        ]);

        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(report);

        Assert.Contains(expectedSubstring: @"a\|b", actualString: markdown, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_PipeInAModelOrDimensionName_IsEscapedRatherThanBreakingTheTable()
    {
        // Model names are operator-configurable, and routing validation only rejects blank/duplicate
        // values - not table-breaking characters. Every free-text cell needs the same protection Detail
        // gets, not just Detail.
        var report = MakeReport(
            cohorts: [MakeCohort() with { Dimension = "weird|dimension", JudgeModel = "weird|model" }],
            selfPreference:
            [
                new JudgeSelfPreferenceRow(Dimension: "weird|dimension", JudgeModel: "weird|model",
                    UsedLogprobs: true, StaticAuthority: StaticGradeAuthority.Authoritative,
                    CandidateModel: "weird|candidate", MeanScoreDelta: 0.1, IsOwnBackbone: false, SampleSize: 3)
            ],
            verdicts: [new JudgeCalibrationVerdict(Condition: "weird|condition", Kind: JudgeCalibrationVerdictKind.Pass, Detail: "d")]);

        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(report);

        Assert.Contains(expectedSubstring: @"weird\|dimension", actualString: markdown, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: @"weird\|model", actualString: markdown, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: @"weird\|candidate", actualString: markdown, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: @"weird\|condition", actualString: markdown, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_EmbeddedNewlineInAFreeTextCell_IsCollapsedRatherThanBreakingTheRow()
    {
        var report = MakeReport(verdicts:
        [
            new JudgeCalibrationVerdict(Condition: "score-collapse", Kind: JudgeCalibrationVerdictKind.Fail,
                Detail: "line one\nline two")
        ]);

        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(report);

        Assert.Contains(expectedSubstring: "line one line two", actualString: markdown,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_SelfPreferenceDelta_CarriesAnExplicitSign()
    {
        // The direction is the finding. An unsigned "0.400" hides whether the judge is more or less
        // generous than the static verifier, which is the entire question.
        var report = MakeReport(selfPreference:
        [
            new JudgeSelfPreferenceRow(Dimension: "algorithm", JudgeModel: "judge-a", UsedLogprobs: true,
                StaticAuthority: StaticGradeAuthority.Authoritative, CandidateModel: "judge-a", MeanScoreDelta: 0.4,
                IsOwnBackbone: true, SampleSize: 5)
        ]);

        var markdown = JudgeCalibrationReportFormatter.FormatMarkdown(report);

        Assert.Contains(expectedSubstring: "+0.400", actualString: markdown, comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Builds a report, defaulting every section the test under way does not exercise to empty.</summary>
    private static JudgeCalibrationReport MakeReport(
        IReadOnlyList<JudgeCalibrationCohort>? cohorts = null,
        IReadOnlyList<JudgeSelfPreferenceRow>? selfPreference = null,
        IReadOnlyList<JudgeCalibrationVerdict>? verdicts = null)
    {
        return new JudgeCalibrationReport(
            Cohorts: cohorts ?? [],
            SelfPreference: selfPreference ?? [],
            Verdicts: verdicts ?? [],
            GeneratedAtUtc: DateTimeOffset.UnixEpoch,
            TotalRowsAnalyzed: cohorts?.Sum(c => c.SampleSize) ?? 0);
    }

    /// <summary>Builds one cohort with a caller-chosen correlation and plausible values elsewhere.</summary>
    private static JudgeCalibrationCohort MakeCohort(double? correlation = 0.75)
    {
        return new JudgeCalibrationCohort(
            Dimension: "algorithm",
            JudgeModel: "judge-a",
            UsedLogprobs: true,
            StaticAuthority: StaticGradeAuthority.Authoritative,
            Agreement: new JudgeAgreement(
                Correlation: correlation,
                MeanAbsoluteDifference: 0.123,
                MeanJudgeScore: 0.6,
                MeanStaticScore: 0.5),
            Distribution: new JudgeScoreDistribution(
                StandardDeviation: 0.25,
                ModalShare: 0.3,
                DistinctScoreCount: 12),
            SampleSize: 30);
    }
}
