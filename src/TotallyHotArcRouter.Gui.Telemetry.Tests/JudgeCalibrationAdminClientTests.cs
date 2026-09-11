using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="JudgeCalibrationAdminClient"/> - the wire-to-view mapping and error translation
/// behind the Governance → Judge Calibration panel (docs/router/geval-shadow-scoring-plan.md Phase G2),
/// mirroring <see cref="RegretHarnessAdminClientTests"/>.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, same reasoning as
/// <c>RegretHarnessAdminClientTests</c>: the generated client exposes a protected parameterless
/// constructor precisely for test doubles. The load-bearing cases here are the nullable-wrapper mapping
/// (an unset <c>DoubleValue</c> must arrive as <see langword="null"/>, never a measured 0.0) and the
/// unknown-enum fallback (a value this build cannot map must never read as a passing/known state).
/// </remarks>
public class JudgeCalibrationAdminClientTests
{
    [Fact]
    public async Task GetReportAsync_EmptyResponse_MapsToAnEmptyReport()
    {
        var stub = new StubClient { Response = new Contract.JudgeCalibrationReportResponse() };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        report.TotalRowsAnalyzed.Should().Be(0);
        report.Verdicts.Should().BeEmpty();
        report.Cohorts.Should().BeEmpty();
        report.SelfPreference.Should().BeEmpty();
        report.Markdown.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReportAsync_MissingTimestamp_MapsToTheDefaultInstant()
    {
        // GeneratedAtUtc is a message field in the wire contract - proto3 leaves it unset (null) rather
        // than zeroing it, unlike a scalar. The client must not throw dereferencing a null timestamp.
        var stub = new StubClient
        { Response = new Contract.JudgeCalibrationReportResponse { GeneratedAtUtc = null } };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        report.GeneratedAtUtc.Should().Be(default(DateTimeOffset));
    }

    [Fact]
    public async Task GetReportAsync_PresentTimestamp_RoundTrips()
    {
        var generatedAt = new DateTimeOffset(2026, 9, 10, 12, 0, 0, offset: TimeSpan.Zero);
        var stub = new StubClient
        {
            Response = new Contract.JudgeCalibrationReportResponse
            { GeneratedAtUtc = Timestamp.FromDateTimeOffset(generatedAt), TotalRowsAnalyzed = 42 }
        };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        report.GeneratedAtUtc.Should().Be(generatedAt);
        report.TotalRowsAnalyzed.Should().Be(42);
    }

    [Fact]
    public async Task GetReportAsync_UnsetDoubleValueWrappers_MapToNullRatherThanZero()
    {
        // The load-bearing case: a suppressed correlation and a measured 0.0 are opposite findings, and
        // only the wrapper's presence/absence - not its value - can tell them apart on the wire.
        var stub = new StubClient
        {
            Response = new Contract.JudgeCalibrationReportResponse
            {
                Cohorts =
                {
                    new Contract.JudgeCalibrationCohort
                    {
                        Dimension = "algorithm",
                        JudgeModel = "judge-a",
                        Correlation = null,
                        MeanAbsoluteDifference = null,
                        MeanJudgeScore = null,
                        MeanStaticScore = null,
                        StandardDeviation = null,
                        ModalShare = null
                    }
                }
            }
        };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        var cohort = report.Cohorts.Single();
        cohort.Correlation.Should().BeNull();
        cohort.MeanAbsoluteDifference.Should().BeNull();
        cohort.MeanJudgeScore.Should().BeNull();
        cohort.MeanStaticScore.Should().BeNull();
        cohort.StandardDeviation.Should().BeNull();
        cohort.ModalShare.Should().BeNull();
    }

    [Fact]
    public async Task GetReportAsync_MeasuredZeroDoubleValueWrappers_SurviveAsAValueNotAnAbsence()
    {
        var stub = new StubClient
        {
            Response = new Contract.JudgeCalibrationReportResponse
            {
                Cohorts =
                {
                    new Contract.JudgeCalibrationCohort
                    {
                        Dimension = "algorithm",
                        JudgeModel = "judge-a",
                        Correlation = 0.0,
                        StandardDeviation = 0.0
                    }
                }
            }
        };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        var cohort = report.Cohorts.Single();
        cohort.Correlation.Should().NotBeNull();
        cohort.Correlation!.Value.Should().Be(0.0);
        cohort.StandardDeviation.Should().NotBeNull();
        cohort.StandardDeviation!.Value.Should().Be(0.0);
    }

    [Theory]
    [InlineData(Contract.JudgeCalibrationVerdictKind.Pass, JudgeCalibrationVerdictKindInfo.Pass)]
    [InlineData(Contract.JudgeCalibrationVerdictKind.Fail, JudgeCalibrationVerdictKindInfo.Fail)]
    [InlineData(Contract.JudgeCalibrationVerdictKind.Insufficient, JudgeCalibrationVerdictKindInfo.Insufficient)]
    [InlineData(Contract.JudgeCalibrationVerdictKind.Unevaluable, JudgeCalibrationVerdictKindInfo.Unevaluable)]
    // Unspecified stands in for a value this build cannot map. It must not read as Pass: the panel would
    // otherwise render an unrecognized wire value as a clean bill of health.
    [InlineData(Contract.JudgeCalibrationVerdictKind.Unspecified, JudgeCalibrationVerdictKindInfo.Unevaluable)]
    public async Task GetReportAsync_MapsEveryVerdictKindDistinctly(
        Contract.JudgeCalibrationVerdictKind wireKind,
        JudgeCalibrationVerdictKindInfo expected)
    {
        var stub = new StubClient
        {
            Response = new Contract.JudgeCalibrationReportResponse
            {
                Verdicts = { new Contract.JudgeCalibrationVerdict { Condition = "c", Kind = wireKind, Detail = "d" } }
            }
        };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        report.Verdicts.Single().Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData(Contract.StaticGradeAuthority.Unknown, StaticGradeAuthorityInfo.Unknown)]
    [InlineData(Contract.StaticGradeAuthority.Heuristic, StaticGradeAuthorityInfo.Heuristic)]
    [InlineData(Contract.StaticGradeAuthority.Authoritative, StaticGradeAuthorityInfo.Authoritative)]
    public async Task GetReportAsync_MapsEveryStaticAuthorityBucketDistinctlyOnCohorts(
        Contract.StaticGradeAuthority wireValue,
        StaticGradeAuthorityInfo expected)
    {
        var stub = new StubClient
        {
            Response = new Contract.JudgeCalibrationReportResponse
            {
                Cohorts =
                {
                    new Contract.JudgeCalibrationCohort
                    { Dimension = "algorithm", JudgeModel = "judge-a", StaticAuthority = wireValue }
                }
            }
        };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        report.Cohorts.Single().StaticAuthority.Should().Be(expected);
    }

    [Fact]
    public async Task GetReportAsync_CarriesTheSelfPreferenceCohortKeysAndOwnBackboneFlag()
    {
        var stub = new StubClient
        {
            Response = new Contract.JudgeCalibrationReportResponse
            {
                SelfPreference =
                {
                    new Contract.JudgeSelfPreferenceRow
                    {
                        Dimension = "algorithm",
                        JudgeModel = "judge-a",
                        UsedLogprobs = true,
                        StaticAuthority = Contract.StaticGradeAuthority.Authoritative,
                        CandidateModel = "judge-a",
                        MeanScoreDelta = 0.4,
                        IsOwnBackbone = true,
                        SampleSize = 7
                    }
                }
            }
        };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        var row = report.SelfPreference.Single();
        row.Dimension.Should().Be("algorithm");
        row.UsedLogprobs.Should().BeTrue();
        row.StaticAuthority.Should().Be(StaticGradeAuthorityInfo.Authoritative);
        row.IsOwnBackbone.Should().BeTrue();
        row.MeanScoreDelta.Should().Be(0.4);
        row.SampleSize.Should().Be(7);
    }

    [Fact]
    public async Task GetReportAsync_CarriesTheMarkdownFieldVerbatim()
    {
        var stub = new StubClient
        { Response = new Contract.JudgeCalibrationReportResponse { Markdown = "### Gate conditions" } };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        report.Markdown.Should().Be("### Gate conditions");
    }

    [Fact]
    public async Task GetReportAsync_MultipleCohortsAndVerdicts_PreserveOrderAndCount()
    {
        var stub = new StubClient
        {
            Response = new Contract.JudgeCalibrationReportResponse
            {
                Verdicts =
                {
                    new Contract.JudgeCalibrationVerdict { Condition = "a", Kind = Contract.JudgeCalibrationVerdictKind.Pass },
                    new Contract.JudgeCalibrationVerdict { Condition = "b", Kind = Contract.JudgeCalibrationVerdictKind.Fail }
                },
                Cohorts =
                {
                    new Contract.JudgeCalibrationCohort { Dimension = "algorithm", JudgeModel = "judge-a" },
                    new Contract.JudgeCalibrationCohort { Dimension = "bug_fixing", JudgeModel = "judge-b" }
                }
            }
        };
        using var client = new JudgeCalibrationAdminClient(stub);

        var report = await client.GetReportAsync(TestContext.Current.CancellationToken);

        report.Verdicts.Select(v => v.Condition).Should().Equal("a", "b");
        report.Cohorts.Select(c => c.Dimension).Should().Equal("algorithm", "bug_fixing");
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        using var client = new JudgeCalibrationAdminClient(stub);

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            client.GetReportAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the judge calibration report: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Internal, detail: "boom")) };
        using var client = new JudgeCalibrationAdminClient(stub);

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            client.GetReportAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the judge calibration report: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new JudgeCalibrationAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new JudgeCalibrationAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new JudgeCalibrationAdminClient(
                (Contract.JudgeCalibrationAdminService.JudgeCalibrationAdminServiceClient)null!));
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overload: the generated
    /// convenience overloads delegate to it, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.JudgeCalibrationAdminService.JudgeCalibrationAdminServiceClient
    {
        public Contract.JudgeCalibrationReportResponse Response { get; init; } = new();

        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.JudgeCalibrationReportResponse> GetJudgeCalibrationReportAsync(
            Contract.GetJudgeCalibrationReportRequest request,
            CallOptions options)
        {
            return new AsyncUnaryCall<Contract.JudgeCalibrationReportResponse>(
                responseAsync: Failure is null
                    ? Task.FromResult(Response)
                    : Task.FromException<Contract.JudgeCalibrationReportResponse>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}
