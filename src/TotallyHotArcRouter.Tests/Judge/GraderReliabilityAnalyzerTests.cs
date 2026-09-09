using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderReliabilityAnalyzer"/> against a synthetic <c>grader_scores</c> fixture with
/// known correlations, per docs/router/grader-reliability-plan.md's Q4 exit criterion: the analyzer must
/// compute the three statistics correctly and suppress (not zero-fill) any statistic below
/// <see cref="IGraderReliabilityAnalyzer.MinimumSampleSize"/>.
/// </summary>
public class GraderReliabilityAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_TwoGradersPerfectlyAgree_ReportsCorrelationOfOne()
    {
        List<GraderScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
        {
            var correlationId = $"corr-{i}";
            var score = i / 29.0;
            rows.Add(MakeRow(correlationId, dimension: "bug_fixing", graderKey: "judge", score: score));
            rows.Add(MakeRow(correlationId, dimension: "bug_fixing", graderKey: "codejudge", score: score));
        }

        var report = await Analyze(rows);

        var dimension = Assert.Single(report.Dimensions);
        var pair = Assert.Single(dimension.PairAgreements);
        Assert.Equal(30, actual: pair.SampleSize);
        Assert.NotNull(pair.Correlation);
        Assert.InRange(pair.Correlation!.Value, 0.999, 1.0001);
    }

    [Fact]
    public async Task AnalyzeAsync_TwoGradersPerfectlyDisagree_ReportsCorrelationOfNegativeOne()
    {
        List<GraderScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
        {
            var correlationId = $"corr-{i}";
            rows.Add(MakeRow(correlationId, dimension: "bug_fixing", graderKey: "judge", score: i / 29.0));
            rows.Add(MakeRow(correlationId, dimension: "bug_fixing", graderKey: "codejudge", score: 1.0 - i / 29.0));
        }

        var report = await Analyze(rows);

        var pair = Assert.Single(Assert.Single(report.Dimensions).PairAgreements);
        Assert.NotNull(pair.Correlation);
        Assert.InRange(pair.Correlation!.Value, -1.0001, -0.999);
    }

    [Fact]
    public async Task AnalyzeAsync_FewerPairedObservationsThanMinimum_SuppressesTheCorrelation()
    {
        List<GraderScoreRecord> rows = [];
        for (var i = 0; i < 5; i++)
        {
            var correlationId = $"corr-{i}";
            rows.Add(MakeRow(correlationId, dimension: "bug_fixing", graderKey: "judge", score: i / 4.0));
            rows.Add(MakeRow(correlationId, dimension: "bug_fixing", graderKey: "codejudge", score: i / 4.0));
        }

        var report = await Analyze(rows);

        var pair = Assert.Single(Assert.Single(report.Dimensions).PairAgreements);
        Assert.Equal(5, actual: pair.SampleSize);
        Assert.Null(pair.Correlation);
    }

    [Fact]
    public async Task AnalyzeAsync_OneGraderAlwaysScoresTheSameValue_SuppressesTheCorrelationAsUndefined()
    {
        List<GraderScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
        {
            var correlationId = $"corr-{i}";
            rows.Add(MakeRow(correlationId, dimension: "bug_fixing", graderKey: "judge", score: 0.5));
            rows.Add(MakeRow(correlationId, dimension: "bug_fixing", graderKey: "codejudge", score: i / 29.0));
        }

        var report = await Analyze(rows);

        var pair = Assert.Single(Assert.Single(report.Dimensions).PairAgreements);
        Assert.Equal(30, actual: pair.SampleSize);
        Assert.Null(pair.Correlation);
    }

    [Fact]
    public async Task AnalyzeAsync_GraderNeverScoresTheSameRequestAsAnother_ReportsNoPairAgreement()
    {
        List<GraderScoreRecord> rows =
        [
            MakeRow("corr-1", dimension: "bug_fixing", graderKey: "judge", score: 0.5)
        ];

        var report = await Analyze(rows);

        Assert.Empty(Assert.Single(report.Dimensions).PairAgreements);
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyCorrelationIdRows_ExcludedFromPairAgreementRatherThanIncorrectlyGrouped()
    {
        // Two unrelated no-correlation-id requests, each graded by both graders. If these were grouped
        // together by their shared empty CorrelationId, they would look like one request both graders
        // scored twice - and ToDictionary would throw on the resulting duplicate GraderKey.
        List<GraderScoreRecord> rows =
        [
            MakeRow(correlationId: string.Empty, dimension: "bug_fixing", graderKey: "judge", score: 0.9),
            MakeRow(correlationId: string.Empty, dimension: "bug_fixing", graderKey: "codejudge", score: 0.1),
            MakeRow(correlationId: string.Empty, dimension: "bug_fixing", graderKey: "judge", score: 0.2),
            MakeRow(correlationId: string.Empty, dimension: "bug_fixing", graderKey: "codejudge", score: 0.8)
        ];

        var report = await Analyze(rows);

        var pair = Assert.Single(Assert.Single(report.Dimensions).PairAgreements);
        Assert.Equal(0, actual: pair.SampleSize);
    }

    [Fact]
    public async Task AnalyzeAsync_DuplicateRowsForSameGraderAndRequest_CollapsesToTheMostRecentRatherThanThrowing()
    {
        var older = MakeRow("corr-1", dimension: "bug_fixing", graderKey: "judge", score: 0.1) with
        {
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var newer = MakeRow("corr-1", dimension: "bug_fixing", graderKey: "judge", score: 0.9) with
        {
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        List<GraderScoreRecord> rows =
        [
            older,
            newer,
            MakeRow("corr-1", dimension: "bug_fixing", graderKey: "codejudge", score: 0.9)
        ];

        var report = await Analyze(rows);

        var pair = Assert.Single(Assert.Single(report.Dimensions).PairAgreements);
        Assert.Equal(1, actual: pair.SampleSize);
    }

    [Fact]
    public async Task AnalyzeAsync_ScoreTracksResponseLength_ReportsPositiveVerbositySkew()
    {
        List<GraderScoreRecord> rows = [];
        for (var i = 0; i < 30; i++)
            rows.Add(MakeRow($"corr-{i}", dimension: "bug_fixing", graderKey: "judge", score: i / 29.0,
                responseLengthChars: i * 100));

        var report = await Analyze(rows);

        var skew = Assert.Single(Assert.Single(report.Dimensions).VerbositySkews);
        Assert.Equal(30, actual: skew.SampleSize);
        Assert.NotNull(skew.Correlation);
        Assert.InRange(skew.Correlation!.Value, 0.999, 1.0001);
    }

    [Fact]
    public async Task AnalyzeAsync_RowsWithNoResponseLength_ExcludedFromVerbositySampleSize()
    {
        List<GraderScoreRecord> rows =
        [
            MakeRow("corr-1", dimension: "bug_fixing", graderKey: "judge", score: 0.5, responseLengthChars: null)
        ];

        var report = await Analyze(rows);

        var skew = Assert.Single(Assert.Single(report.Dimensions).VerbositySkews);
        Assert.Equal(0, actual: skew.SampleSize);
        Assert.Null(skew.Correlation);
    }

    [Fact]
    public async Task AnalyzeAsync_GraderScoresItsOwnBackboneHigher_ReportsPositiveSelfPreferenceDelta()
    {
        List<GraderScoreRecord> rows =
        [
            MakeRow("corr-1", dimension: "bug_fixing", graderKey: "judge", score: 0.9, model: "judge-model",
                graderBackboneModel: "judge-model"),
            MakeRow("corr-2", dimension: "bug_fixing", graderKey: "judge", score: 0.5, model: "other-model",
                graderBackboneModel: "judge-model")
        ];

        var report = await Analyze(rows);

        var skew = Assert.Single(Assert.Single(report.Dimensions).SelfPreferenceSkews);
        Assert.Equal(1, actual: skew.OwnModelSampleSize);
        Assert.Equal(1, actual: skew.OtherModelSampleSize);
        Assert.NotNull(skew.MeanScoreDelta);
        Assert.InRange(skew.MeanScoreDelta!.Value, 0.399, 0.401);
    }

    [Fact]
    public async Task AnalyzeAsync_BackboneNeverAppearsAsACandidate_ReportsUndefinedNotZero()
    {
        List<GraderScoreRecord> rows =
        [
            MakeRow("corr-1", dimension: "bug_fixing", graderKey: "judge", score: 0.5, model: "other-model",
                graderBackboneModel: "judge-model")
        ];

        var report = await Analyze(rows);

        var skew = Assert.Single(Assert.Single(report.Dimensions).SelfPreferenceSkews);
        Assert.Equal(0, actual: skew.OwnModelSampleSize);
        Assert.Null(skew.MeanScoreDelta);
    }

    [Fact]
    public async Task AnalyzeAsync_NoBackboneRecorded_GraderHasNoSelfPreferenceEntryAtAll()
    {
        List<GraderScoreRecord> rows =
        [
            MakeRow("corr-1", dimension: "bug_fixing", graderKey: "analysis", score: 0.5, graderBackboneModel: null)
        ];

        var report = await Analyze(rows);

        Assert.Empty(Assert.Single(report.Dimensions).SelfPreferenceSkews);
    }

    [Fact]
    public async Task AnalyzeAsync_RowsAcrossTwoDimensions_ReportsOneEntryPerDimension()
    {
        List<GraderScoreRecord> rows =
        [
            MakeRow("corr-1", dimension: "bug_fixing", graderKey: "judge", score: 0.5),
            MakeRow("corr-2", dimension: "algorithm", graderKey: "judge", score: 0.5)
        ];

        var report = await Analyze(rows);

        Assert.Equal(2, actual: report.Dimensions.Count);
        Assert.Equal(2, actual: report.TotalRowsAnalyzed);
    }

    private static async Task<GraderReliabilityReport> Analyze(IReadOnlyList<GraderScoreRecord> rows)
    {
        var analyzer = new GraderReliabilityAnalyzer(new FakeGraderScoreStore(rows));
        return await analyzer.AnalyzeAsync(TestContext.Current.CancellationToken);
    }

    private static GraderScoreRecord MakeRow(
        string correlationId,
        string dimension,
        string graderKey,
        double score,
        string model = "some-model",
        string? graderBackboneModel = null,
        int? responseLengthChars = null)
    {
        return new GraderScoreRecord(
            0,
            CorrelationId: correlationId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Dimension: dimension,
            Model: model,
            GraderKey: graderKey,
            Score: score,
            GraderBackboneModel: graderBackboneModel,
            ResponseLengthChars: responseLengthChars);
    }

    private sealed class FakeGraderScoreStore(IReadOnlyList<GraderScoreRecord> rows) : IGraderScoreStore
    {
        public Task InsertAsync(GraderScoreRecord record, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(rows.Count);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<GraderScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(rows);
        }
    }
}
