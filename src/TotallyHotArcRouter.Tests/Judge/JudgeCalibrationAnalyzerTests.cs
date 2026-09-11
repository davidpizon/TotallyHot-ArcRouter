using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeCalibrationAnalyzer"/> against synthetic <c>judge_shadow_scores</c> fixtures with
/// known shapes, per docs/router/geval-shadow-scoring-plan.md's Phase G2 scope: the analyzer must segment
/// rather than pool, compute agreement and distribution correctly, suppress (not zero-fill) statistics
/// below the sample floor, report the permanently unevaluable conditions as such, and never attach a
/// verdict to self-preference.
/// </summary>
public class JudgeCalibrationAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_NoRows_ReportsEmptyCohortsAndTheUnevaluableConditions()
    {
        var report = await Analyze([]);

        Assert.Empty(report.Cohorts);
        Assert.Empty(report.SelfPreference);
        Assert.Equal(expected: 0, actual: report.TotalRowsAnalyzed);

        // The two permanently unevaluable conditions are reported even with no data at all: their absence
        // is a property of the project, not of the sample.
        Assert.Equal(
            expected: JudgeCalibrationVerdictKind.Unevaluable,
            actual: Verdict(report, "execution-ground-truth").Kind);
        Assert.Equal(
            expected: JudgeCalibrationVerdictKind.Unevaluable,
            actual: Verdict(report, "self-preference").Kind);
        Assert.Equal(
            expected: JudgeCalibrationVerdictKind.Insufficient,
            actual: Verdict(report, "score-collapse").Kind);
    }

    [Fact]
    public async Task AnalyzeAsync_JudgeTracksStaticExactly_ReportsCorrelationOfOne()
    {
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
        {
            var score = i / 29.0;
            rows.Add(MakeRow(i, staticScore: score, judgeScore: score));
        }

        var report = await Analyze(rows);

        var cohort = Assert.Single(report.Cohorts);
        Assert.Equal(expected: 30, actual: cohort.SampleSize);
        Assert.NotNull(cohort.Agreement.Correlation);
        Assert.InRange(cohort.Agreement.Correlation!.Value, 0.999, 1.0001);
        Assert.NotNull(cohort.Agreement.MeanAbsoluteDifference);
        Assert.InRange(cohort.Agreement.MeanAbsoluteDifference!.Value, 0.0, 1e-9);
    }

    [Fact]
    public async Task AnalyzeAsync_JudgeRanksCorrectlyButSitsHigh_ReportsHighCorrelationAndLargeMad()
    {
        // The case the two statistics exist to separate: a judge useful for choosing between models and
        // miscalibrated in absolute terms. One number alone could not say this.
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
        {
            var staticScore = i / 58.0;
            rows.Add(MakeRow(i, staticScore: staticScore, judgeScore: staticScore + 0.2));
        }

        var report = await Analyze(rows);

        var cohort = Assert.Single(report.Cohorts);
        Assert.InRange(cohort.Agreement.Correlation!.Value, 0.999, 1.0001);
        Assert.InRange(cohort.Agreement.MeanAbsoluteDifference!.Value, 0.1999, 0.2001);
    }

    [Fact]
    public async Task AnalyzeAsync_BelowMinimumSampleSize_SuppressesCorrelationButStillReportsTheMean()
    {
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 5; i++) rows.Add(MakeRow(i, staticScore: i / 4.0, judgeScore: i / 4.0));

        var report = await Analyze(rows);

        var cohort = Assert.Single(report.Cohorts);
        Assert.Null(cohort.Agreement.Correlation);

        // Not suppressed alongside it: a mean is meaningful from the first row, where a rank correlation
        // over five points is not.
        Assert.NotNull(cohort.Agreement.MeanAbsoluteDifference);
        Assert.NotNull(cohort.Agreement.MeanJudgeScore);
    }

    [Fact]
    public async Task AnalyzeAsync_ZeroVarianceJudgeScores_SuppressesCorrelationRatherThanReportingZero()
    {
        // A collapsed judge makes the correlation literally 0/0. Reporting 0.0 would read as "no
        // relationship measured", which is a different and far less alarming claim than "undefined".
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 30; i++) rows.Add(MakeRow(i, staticScore: i / 29.0, judgeScore: 0.6));

        var report = await Analyze(rows);

        var cohort = Assert.Single(report.Cohorts);
        Assert.Null(cohort.Agreement.Correlation);
        Assert.Equal(expected: 0.0, actual: cohort.Distribution.StandardDeviation!.Value, precision: 9);
        Assert.Equal(expected: 1, actual: cohort.Distribution.DistinctScoreCount);
    }

    [Fact]
    public async Task AnalyzeAsync_DifferentJudgeModels_AreNeverPooledIntoOneCohort()
    {
        // The segmentation requirement from G2's revision note. Two backbones with opposite behavior would
        // average into one meaningless "moderate agreement" figure if pooled.
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
        {
            var score = i / 29.0;
            rows.Add(MakeRow(i, staticScore: score, judgeScore: score, judgeModel: "judge-a"));
            rows.Add(MakeRow(i + 100, staticScore: score, judgeScore: 1.0 - score, judgeModel: "judge-b"));
        }

        var report = await Analyze(rows);

        Assert.Equal(expected: 2, actual: report.Cohorts.Count);
        var agreeing = report.Cohorts.Single(c => c.JudgeModel == "judge-a");
        var disagreeing = report.Cohorts.Single(c => c.JudgeModel == "judge-b");
        Assert.InRange(agreeing.Agreement.Correlation!.Value, 0.999, 1.0001);
        Assert.InRange(disagreeing.Agreement.Correlation!.Value, -1.0001, -0.999);
    }

    [Fact]
    public async Task AnalyzeAsync_LogprobsAndSingleSampleRows_AreSeparateCohorts()
    {
        // The cohort split that makes the score-collapse check self-calibrating: G-Eval predicts the
        // single-sample rows collapse, and keeping them apart is what lets a reader see it.
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
        {
            rows.Add(MakeRow(i, staticScore: i / 29.0, judgeScore: i / 29.0, usedLogprobs: true));
            rows.Add(MakeRow(i + 100, staticScore: i / 29.0, judgeScore: 0.6, usedLogprobs: false));
        }

        var report = await Analyze(rows);

        Assert.Equal(expected: 2, actual: report.Cohorts.Count);
        var weighted = report.Cohorts.Single(c => c.UsedLogprobs);
        var fallback = report.Cohorts.Single(c => !c.UsedLogprobs);
        Assert.True(weighted.Distribution.StandardDeviation > 0.2);
        Assert.Equal(expected: 0.0, actual: fallback.Distribution.StandardDeviation!.Value, precision: 9);
    }

    [Theory]
    [InlineData(null, StaticGradeAuthority.Unknown)]
    [InlineData(false, StaticGradeAuthority.Heuristic)]
    [InlineData(true, StaticGradeAuthority.Authoritative)]
    public async Task AnalyzeAsync_MapsSyntaxAuthoritativeOntoItsOwnBucket(bool? flag, StaticGradeAuthority expected)
    {
        var report = await Analyze([MakeRow(1, staticScore: 0.5, judgeScore: 0.5, syntaxAuthoritative: flag)]);

        var cohort = Assert.Single(report.Cohorts);
        Assert.Equal(expected: expected, actual: cohort.StaticAuthority);
    }

    [Fact]
    public async Task AnalyzeAsync_PreColumnRows_AreNotFoldedIntoTheParserOrHeuristicBucket()
    {
        // The migration leaves historical rows NULL on purpose. Guessing either way would put heuristic
        // rows in the trusted bucket half the time - the exact error the split exists to prevent.
        List<JudgeShadowScoreRecord> rows =
        [
            MakeRow(1, staticScore: 0.5, judgeScore: 0.5, syntaxAuthoritative: null),
            MakeRow(2, staticScore: 0.5, judgeScore: 0.5, syntaxAuthoritative: true)
        ];

        var report = await Analyze(rows);

        Assert.Equal(expected: 2, actual: report.Cohorts.Count);
        Assert.Contains(report.Cohorts, c => c.StaticAuthority == StaticGradeAuthority.Unknown);
        Assert.Contains(report.Cohorts, c => c.StaticAuthority == StaticGradeAuthority.Authoritative);
    }

    [Fact]
    public async Task AnalyzeAsync_HealthyDistribution_PassesTheScoreCollapseCondition()
    {
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 30; i++) rows.Add(MakeRow(i, staticScore: 0.5, judgeScore: i / 29.0));

        var report = await Analyze(rows);

        Assert.Equal(expected: JudgeCalibrationVerdictKind.Pass, actual: Verdict(report, "score-collapse").Kind);
    }

    [Fact]
    public async Task AnalyzeAsync_CollapsedDistribution_FailsTheScoreCollapseCondition()
    {
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 30; i++) rows.Add(MakeRow(i, staticScore: i / 29.0, judgeScore: 0.6));

        var report = await Analyze(rows);

        var verdict = Verdict(report, "score-collapse");
        Assert.Equal(expected: JudgeCalibrationVerdictKind.Fail, actual: verdict.Kind);
        Assert.Contains(expectedSubstring: "collapsed", actualString: verdict.Detail,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_MostlyOneScoreWithOneWideOutlier_FailsOnModalShareDespiteTheSpread()
    {
        // The case the standard deviation alone misses. 29 of 30 rows are a single value, but the lone
        // 0.0 supplies enough spread to clear the deviation floor; the modal-share ceiling is what
        // catches it.
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 29; i++) rows.Add(MakeRow(i, staticScore: i / 29.0, judgeScore: 0.6));
        rows.Add(MakeRow(29, staticScore: 1.0, judgeScore: 0.0));

        var report = await Analyze(rows);

        var cohort = Assert.Single(report.Cohorts);
        Assert.True(cohort.Distribution.StandardDeviation > 0.05,
            userMessage: "Fixture must clear the deviation floor, or it would not isolate the modal-share check.");
        Assert.True(cohort.Distribution.ModalShare > 0.8);
        Assert.Equal(expected: JudgeCalibrationVerdictKind.Fail, actual: Verdict(report, "score-collapse").Kind);
    }

    [Fact]
    public async Task AnalyzeAsync_CohortsBelowTheFloor_ReportScoreCollapseAsPendingRatherThanPassing()
    {
        // A too-small sample must never read as a clean bill of health.
        var report = await Analyze([MakeRow(1, staticScore: 0.5, judgeScore: 0.6)]);

        Assert.Equal(expected: JudgeCalibrationVerdictKind.Insufficient,
            actual: Verdict(report, "score-collapse").Kind);
    }

    [Fact]
    public async Task AnalyzeAsync_ComputesPerCandidateSelfPreferenceAndFlagsTheJudgesOwnBackbone()
    {
        List<JudgeShadowScoreRecord> rows =
        [
            // The judge grading its own backbone, generously.
            MakeRow(1, staticScore: 0.5, judgeScore: 0.9, judgeModel: "judge-a", model: "judge-a"),
            MakeRow(2, staticScore: 0.5, judgeScore: 0.9, judgeModel: "judge-a", model: "judge-a"),

            // The same judge grading a different model, in line with the static verifier.
            MakeRow(3, staticScore: 0.5, judgeScore: 0.5, judgeModel: "judge-a", model: "other-model")
        ];

        var report = await Analyze(rows);

        var own = report.SelfPreference.Single(r => r.CandidateModel == "judge-a");
        var other = report.SelfPreference.Single(r => r.CandidateModel == "other-model");

        Assert.True(own.IsOwnBackbone);
        Assert.False(other.IsOwnBackbone);
        Assert.Equal(expected: 0.4, actual: own.MeanScoreDelta, precision: 9);
        Assert.Equal(expected: 0.0, actual: other.MeanScoreDelta, precision: 9);
        Assert.Equal(expected: 2, actual: own.SampleSize);
    }

    [Fact]
    public async Task AnalyzeAsync_SelfPreferenceIsSegmentedByCohortNotPooledAcrossThem()
    {
        // The bug this test pins: pooling self-preference across dimensions (or logprobs modes, or
        // static-grade authority) could make a judge's own backbone look preferred merely because one
        // cohort has a different score distribution from another - a confound that has nothing to do with
        // self-preference. Two dimensions, opposite deltas for the same (judge, candidate) pair: pooling
        // them would average to a misleading ~0, while segmenting reports both real deltas distinctly.
        List<JudgeShadowScoreRecord> rows =
        [
            MakeRow(1, staticScore: 0.5, judgeScore: 0.9, judgeModel: "judge-a", model: "judge-a", dimension: "algorithm"),
            MakeRow(2, staticScore: 0.5, judgeScore: 0.9, judgeModel: "judge-a", model: "judge-a", dimension: "algorithm"),
            MakeRow(3, staticScore: 0.5, judgeScore: 0.1, judgeModel: "judge-a", model: "judge-a", dimension: "bug_fixing"),
            MakeRow(4, staticScore: 0.5, judgeScore: 0.1, judgeModel: "judge-a", model: "judge-a", dimension: "bug_fixing")
        ];

        var report = await Analyze(rows);

        Assert.Equal(expected: 2, actual: report.SelfPreference.Count);
        var algorithm = report.SelfPreference.Single(r => r.Dimension == "algorithm");
        var bugFixing = report.SelfPreference.Single(r => r.Dimension == "bug_fixing");
        Assert.Equal(expected: 0.4, actual: algorithm.MeanScoreDelta, precision: 9);
        Assert.Equal(expected: -0.4, actual: bugFixing.MeanScoreDelta, precision: 9);
        Assert.Equal(expected: 2, actual: algorithm.SampleSize);
        Assert.Equal(expected: 2, actual: bugFixing.SampleSize);
    }

    [Fact]
    public async Task AnalyzeAsync_SelfPreferenceSeparatesLogprobsCohorts()
    {
        List<JudgeShadowScoreRecord> rows =
        [
            MakeRow(1, staticScore: 0.5, judgeScore: 0.9, judgeModel: "judge-a", model: "judge-a", usedLogprobs: true),
            MakeRow(2, staticScore: 0.5, judgeScore: 0.5, judgeModel: "judge-a", model: "judge-a", usedLogprobs: false)
        ];

        var report = await Analyze(rows);

        Assert.Equal(expected: 2, actual: report.SelfPreference.Count);
        Assert.Contains(report.SelfPreference, r => r.UsedLogprobs && r.MeanScoreDelta > 0.39);
        Assert.Contains(report.SelfPreference, r => !r.UsedLogprobs && r.MeanScoreDelta == 0.0);
    }

    [Fact]
    public async Task AnalyzeAsync_SelfPreferenceSeparatesStaticAuthorityCohorts()
    {
        List<JudgeShadowScoreRecord> rows =
        [
            MakeRow(1, staticScore: 0.5, judgeScore: 0.9, judgeModel: "judge-a", model: "judge-a", syntaxAuthoritative: true),
            MakeRow(2, staticScore: 0.5, judgeScore: 0.5, judgeModel: "judge-a", model: "judge-a", syntaxAuthoritative: false)
        ];

        var report = await Analyze(rows);

        Assert.Equal(expected: 2, actual: report.SelfPreference.Count);
        Assert.Contains(report.SelfPreference,
            r => r.StaticAuthority == StaticGradeAuthority.Authoritative && r.MeanScoreDelta > 0.39);
        Assert.Contains(report.SelfPreference,
            r => r.StaticAuthority == StaticGradeAuthority.Heuristic && r.MeanScoreDelta == 0.0);
    }

    [Fact]
    public async Task AnalyzeAsync_SelfPreferenceNeverProducesAPassOrFail()
    {
        // The deliberate design decision, pinned by a test so a later change cannot quietly add a
        // threshold: G-Eval publishes this bias's direction but no magnitude, so any ceiling would be
        // invented, and an invented FAIL looks like a measurement.
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
            rows.Add(MakeRow(i, staticScore: 0.1, judgeScore: 1.0, judgeModel: "judge-a", model: "judge-a"));

        var report = await Analyze(rows);

        var verdict = Verdict(report, "self-preference");
        Assert.Equal(expected: JudgeCalibrationVerdictKind.Unevaluable, actual: verdict.Kind);
        Assert.DoesNotContain(report.Verdicts,
            v => v.Condition == "self-preference" && v.Kind is JudgeCalibrationVerdictKind.Pass
                or JudgeCalibrationVerdictKind.Fail);
    }

    [Fact]
    public async Task AnalyzeAsync_ExecutionGroundTruthCondition_IsUnevaluableRatherThanFailing()
    {
        // Unevaluable, not Fail and not Insufficient: no amount of accumulated traffic can ever satisfy a
        // condition whose evidence the project deleted.
        List<JudgeShadowScoreRecord> rows = [];
        for (var i = 0; i < 100; i++) rows.Add(MakeRow(i, staticScore: i / 99.0, judgeScore: i / 99.0));

        var report = await Analyze(rows);

        Assert.Equal(expected: JudgeCalibrationVerdictKind.Unevaluable,
            actual: Verdict(report, "execution-ground-truth").Kind);
    }

    [Fact]
    public async Task AnalyzeAsync_StampsGeneratedAtFromTheInjectedClock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));

        var report = await Analyze([], clock);

        Assert.Equal(expected: clock.GetUtcNow(), actual: report.GeneratedAtUtc);
    }

    /// <summary>Runs the analyzer over a fixed row set with default thresholds and a fixed clock.</summary>
    private static Task<JudgeCalibrationReport> Analyze(
        IReadOnlyList<JudgeShadowScoreRecord> rows,
        TimeProvider? timeProvider = null)
    {
        var analyzer = new JudgeCalibrationAnalyzer(
            store: new FakeJudgeShadowScoreStore(rows),
            options: new StaticOptionsMonitor<JudgeCalibrationOptions>(new JudgeCalibrationOptions()),
            timeProvider: timeProvider ?? new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        return analyzer.AnalyzeAsync();
    }

    /// <summary>Finds one named verdict, failing the test rather than returning null if it is absent.</summary>
    private static JudgeCalibrationVerdict Verdict(JudgeCalibrationReport report, string condition)
    {
        return Assert.Single(report.Verdicts, v => v.Condition == condition);
    }

    /// <summary>Builds one shadow row, defaulting every field the test under way does not care about.</summary>
    private static JudgeShadowScoreRecord MakeRow(
        long id,
        double staticScore,
        double judgeScore,
        string judgeModel = "judge-a",
        string model = "model-under-test",
        bool usedLogprobs = true,
        bool? syntaxAuthoritative = true,
        string dimension = "algorithm")
    {
        return new JudgeShadowScoreRecord(
            id,
            CorrelationId: $"corr-{id}",
            CreatedAtUtc: DateTimeOffset.UnixEpoch.AddMinutes(id),
            Dimension: dimension,
            Model: model,
            StaticScore: staticScore,
            JudgeScore: judgeScore,
            JudgeModel: judgeModel,
            JudgePromptVersion: "g-eval-v1",
            JudgeLatencyMs: 10,
            UsedLogprobs: usedLogprobs,
            SyntaxAuthoritative: syntaxAuthoritative);
    }

    /// <summary>A fixed clock, so <see cref="JudgeCalibrationReport.GeneratedAtUtc"/> is assertable.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    /// <summary>An <see cref="IOptionsMonitor{T}"/> over one fixed value; the analyzer never reloads.</summary>
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name)
        {
            return value;
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            return null;
        }
    }

    /// <summary>Serves a fixed row set; every mutating member throws, since the analyzer is read-only.</summary>
    private sealed class FakeJudgeShadowScoreStore(IReadOnlyList<JudgeShadowScoreRecord> rows) : IJudgeShadowScoreStore
    {
        public Task InsertAsync(JudgeShadowScoreRecord record, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(rows.Count);
        }

        public Task<IReadOnlyList<JudgeShadowScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(rows);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
