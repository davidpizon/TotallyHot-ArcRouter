using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.Judge;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeCalibrationAdminGrpcService"/>'s domain-to-wire mapping
/// (docs/router/geval-shadow-scoring-plan.md Phase G2). Unit-tested directly against a
/// <see cref="TestServerCallContext"/>, the same style as <c>RegretHarnessAdminGrpcServiceTests</c>. The
/// load-bearing case here is the nullable-to-wrapper mapping: proto3 zeroes an unset scalar, so a
/// suppressed correlation would arrive as a measured 0.0 without the wrapper types - opposite findings
/// rendered identically.
/// </summary>
public class JudgeCalibrationAdminGrpcServiceTests
{
    [Fact]
    public async Task GetJudgeCalibrationReport_EmptyReport_ReturnsZeroRowsRatherThanFailing()
    {
        var service = new JudgeCalibrationAdminGrpcService(new FakeAnalyzer(MakeReport()));

        var response = await Invoke(service);

        Assert.Equal(expected: 0, actual: response.TotalRowsAnalyzed);
        Assert.Empty(response.Cohorts);
        Assert.Empty(response.SelfPreference);
    }

    [Fact]
    public async Task GetJudgeCalibrationReport_SuppressedStatistics_ArriveUnsetRatherThanAsZero()
    {
        var service = new JudgeCalibrationAdminGrpcService(new FakeAnalyzer(MakeReport(
            cohorts: [MakeCohort(correlation: null, standardDeviation: null, modalShare: null)])));

        var response = await Invoke(service);

        var cohort = Assert.Single(response.Cohorts);
        Assert.Null(cohort.Correlation);
        Assert.Null(cohort.StandardDeviation);
        Assert.Null(cohort.ModalShare);
    }

    [Fact]
    public async Task GetJudgeCalibrationReport_MeasuredZero_SurvivesAsAValueNotAnAbsence()
    {
        // The other half of the wrapper contract: 0.0 is a real finding and must not be indistinguishable
        // from "we could not compute it".
        var service = new JudgeCalibrationAdminGrpcService(new FakeAnalyzer(MakeReport(
            cohorts: [MakeCohort(correlation: 0.0)])));

        var response = await Invoke(service);

        var cohort = Assert.Single(response.Cohorts);
        Assert.NotNull(cohort.Correlation);
        Assert.Equal(expected: 0.0, actual: cohort.Correlation!.Value, precision: 9);
    }

    [Theory]
    [InlineData(JudgeCalibrationVerdictKind.Pass, Contract.JudgeCalibrationVerdictKind.Pass)]
    [InlineData(JudgeCalibrationVerdictKind.Fail, Contract.JudgeCalibrationVerdictKind.Fail)]
    [InlineData(JudgeCalibrationVerdictKind.Insufficient, Contract.JudgeCalibrationVerdictKind.Insufficient)]
    [InlineData(JudgeCalibrationVerdictKind.Unevaluable, Contract.JudgeCalibrationVerdictKind.Unevaluable)]
    public async Task GetJudgeCalibrationReport_MapsEveryVerdictKindDistinctly(
        JudgeCalibrationVerdictKind domain,
        Contract.JudgeCalibrationVerdictKind wire)
    {
        var service = new JudgeCalibrationAdminGrpcService(new FakeAnalyzer(MakeReport(verdicts:
            [new JudgeCalibrationVerdict(Condition: "c", Kind: domain, Detail: "d")])));

        var response = await Invoke(service);

        Assert.Equal(expected: wire, actual: Assert.Single(response.Verdicts).Kind);
    }

    [Theory]
    [InlineData(StaticGradeAuthority.Unknown, Contract.StaticGradeAuthority.Unknown)]
    [InlineData(StaticGradeAuthority.Heuristic, Contract.StaticGradeAuthority.Heuristic)]
    [InlineData(StaticGradeAuthority.Authoritative, Contract.StaticGradeAuthority.Authoritative)]
    public async Task GetJudgeCalibrationReport_MapsEveryStaticAuthorityBucketDistinctly(
        StaticGradeAuthority domain,
        Contract.StaticGradeAuthority wire)
    {
        var service = new JudgeCalibrationAdminGrpcService(new FakeAnalyzer(MakeReport(
            cohorts: [MakeCohort(staticAuthority: domain)])));

        var response = await Invoke(service);

        Assert.Equal(expected: wire, actual: Assert.Single(response.Cohorts).StaticAuthority);
    }

    [Fact]
    public async Task GetJudgeCalibrationReport_CarriesTheSelfPreferenceRowsAndTheOwnBackboneFlag()
    {
        var service = new JudgeCalibrationAdminGrpcService(new FakeAnalyzer(MakeReport(selfPreference:
        [
            new JudgeSelfPreferenceRow(Dimension: "algorithm", JudgeModel: "judge-a", UsedLogprobs: true,
                StaticAuthority: StaticGradeAuthority.Authoritative, CandidateModel: "judge-a", MeanScoreDelta: 0.4,
                IsOwnBackbone: true, SampleSize: 7)
        ])));

        var response = await Invoke(service);

        var row = Assert.Single(response.SelfPreference);
        Assert.Equal(expected: "algorithm", actual: row.Dimension);
        Assert.True(row.UsedLogprobs);
        Assert.Equal(expected: Contract.StaticGradeAuthority.Authoritative, actual: row.StaticAuthority);
        Assert.True(row.IsOwnBackbone);
        Assert.Equal(expected: 0.4, actual: row.MeanScoreDelta, precision: 9);
        Assert.Equal(expected: 7, actual: row.SampleSize);
    }

    [Fact]
    public async Task GetJudgeCalibrationReport_IncludesTheSameMarkdownTheCliPrints()
    {
        // The single-rendering guarantee: the panel and a piped CLI run must never disagree about
        // formatting, so the service ships the formatter's own output rather than re-rendering.
        var report = MakeReport(cohorts: [MakeCohort()]);
        var service = new JudgeCalibrationAdminGrpcService(new FakeAnalyzer(report));

        var response = await Invoke(service);

        Assert.Equal(expected: JudgeCalibrationReportFormatter.FormatMarkdown(report), actual: response.Markdown);
    }

    [Fact]
    public async Task GetJudgeCalibrationReport_OverTheNullAnalyzer_SaysSoRatherThanLookingMerelyIdle()
    {
        // A misconfigured server must not render as a clean "no rows yet" state.
        var service = new JudgeCalibrationAdminGrpcService(new NullJudgeCalibrationAnalyzer());

        var response = await Invoke(service);

        Assert.Equal(expected: 0, actual: response.TotalRowsAnalyzed);
        var verdict = Assert.Single(response.Verdicts);
        Assert.Equal(expected: Contract.JudgeCalibrationVerdictKind.Unevaluable, actual: verdict.Kind);
        Assert.Contains(expectedSubstring: "not configured", actualString: verdict.Detail,
            comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Calls the one RPC against a throwaway server call context.</summary>
    private static Task<Contract.JudgeCalibrationReportResponse> Invoke(JudgeCalibrationAdminGrpcService service)
    {
        return service.GetJudgeCalibrationReport(
            request: new Contract.GetJudgeCalibrationReportRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));
    }

    private static ServerCallContext CreateContext(CancellationToken cancellationToken)
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: cancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
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

    /// <summary>Builds one cohort with plausible values, overridable where a test cares.</summary>
    private static JudgeCalibrationCohort MakeCohort(
        double? correlation = 0.75,
        double? standardDeviation = 0.25,
        double? modalShare = 0.3,
        StaticGradeAuthority staticAuthority = StaticGradeAuthority.Authoritative)
    {
        return new JudgeCalibrationCohort(
            Dimension: "algorithm",
            JudgeModel: "judge-a",
            UsedLogprobs: true,
            StaticAuthority: staticAuthority,
            Agreement: new JudgeAgreement(
                Correlation: correlation,
                MeanAbsoluteDifference: 0.12,
                MeanJudgeScore: 0.6,
                MeanStaticScore: 0.5),
            Distribution: new JudgeScoreDistribution(
                StandardDeviation: standardDeviation,
                ModalShare: modalShare,
                DistinctScoreCount: 12),
            SampleSize: 30);
    }

    /// <summary>Returns one fixed report, so the test controls exactly what the mapping is handed.</summary>
    private sealed class FakeAnalyzer(JudgeCalibrationReport report) : IJudgeCalibrationAnalyzer
    {
        public int MinimumSampleSize => 30;

        public Task<JudgeCalibrationReport> AnalyzeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(report);
        }
    }
}
