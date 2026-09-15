using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="JudgeCalibrationAdmin"/>: the Governance tab's Judge Calibration panel
/// (docs/router/geval-shadow-scoring-plan.md Phase G2). Driven through a fake
/// <see cref="IJudgeCalibrationAdminClient"/> so nothing here needs a live proxy or a gRPC channel.
/// </summary>
public sealed class JudgeCalibrationAdminTests
{
    [Fact]
    public void No_rows_renders_the_empty_state_rather_than_an_empty_table()
    {
        using var ctx = NewContext(new FakeClient(MakeReport()));

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        cut.Markup.Should().Contain("No shadow rows yet");
    }

    [Fact]
    public void Renders_every_gate_condition_with_its_verdict_and_detail()
    {
        using var ctx = NewContext(new FakeClient(MakeReport(verdicts:
        [
            new JudgeCalibrationVerdictInfo("score-collapse", JudgeCalibrationVerdictKindInfo.Fail,
                "2 of 3 evaluable cohort(s) collapsed."),
            new JudgeCalibrationVerdictInfo("execution-ground-truth", JudgeCalibrationVerdictKindInfo.Unevaluable,
                "Code execution was removed from the project.")
        ])));

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        cut.Markup.Should().Contain("score-collapse").And.Contain("FAIL");
        cut.Markup.Should().Contain("2 of 3 evaluable cohort(s) collapsed.");
        cut.Markup.Should().Contain("execution-ground-truth").And.Contain("N/A");
        cut.Markup.Should().Contain("Code execution was removed from the project.");
    }

    [Fact]
    public void An_unevaluable_condition_is_not_coloured_as_a_failure()
    {
        // Neither "pending" nor "permanently unevaluable" is a problem with the judge, so neither may
        // borrow the failure colour and read as an alarm.
        using var ctx = NewContext(new FakeClient(MakeReport(verdicts:
        [
            new JudgeCalibrationVerdictInfo("self-preference", JudgeCalibrationVerdictKindInfo.Unevaluable, "d")
        ])));

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        cut.Markup.Should().NotContain("text-red-400");
    }

    [Fact]
    public void Renders_each_cohort_separately_rather_than_pooling_them()
    {
        using var ctx = NewContext(new FakeClient(MakeReport(cohorts:
        [
            MakeCohort(judgeModel: "judge-a", usedLogprobs: true, correlation: 0.812),
            MakeCohort(judgeModel: "judge-a", usedLogprobs: false, correlation: null)
        ])));

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        cut.Markup.Should().Contain("logprobs").And.Contain("single-sample");
        cut.Markup.Should().Contain("0.812");
    }

    [Fact]
    public void A_suppressed_correlation_says_so_rather_than_rendering_as_a_measured_zero()
    {
        using var ctx = NewContext(new FakeClient(MakeReport(cohorts: [MakeCohort(correlation: null)])));

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        cut.Markup.Should().Contain("suppressed");
    }

    [Fact]
    public void The_judges_own_backbone_is_flagged_in_the_self_preference_table()
    {
        using var ctx = NewContext(new FakeClient(MakeReport(
            cohorts: [MakeCohort()],
            selfPreference:
            [
                new JudgeSelfPreferenceRowInfo(Dimension: "algorithm", JudgeModel: "judge-a", UsedLogprobs: true,
                    StaticAuthority: StaticGradeAuthorityInfo.Authoritative, CandidateModel: "judge-a",
                    MeanScoreDelta: 0.4, IsOwnBackbone: true, SampleSize: 12)
            ])));

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        cut.Markup.Should().Contain("own backbone");
        cut.Markup.Should().Contain("+0.400");
    }

    [Fact]
    public void The_self_preference_table_always_states_that_it_carries_no_verdict()
    {
        using var ctx = NewContext(new FakeClient(MakeReport(cohorts: [MakeCohort()])));

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        // Matched within one rendered line: the razor source wraps this sentence, so the full phrase is
        // never a contiguous substring of the markup.
        cut.Markup.Should().Contain("Reported without a pass/fail");
    }

    [Fact]
    public void Renders_an_unreachable_state_when_the_router_cannot_be_reached()
    {
        using var ctx = NewContext(new FakeClient
        { Error = new GrpcAdminException(message: "nope", isUnavailable: true) });

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Retry");
    }

    [Fact]
    public void Renders_a_distinct_error_state_when_the_load_fails_but_the_router_is_reachable()
    {
        // A reachable RPC failure (the router answered, the call itself failed) must never be labeled
        // "Router unreachable" - that phrase is reserved for an actual connectivity failure.
        using var ctx = NewContext(new FakeClient
        { Error = new GrpcAdminException(message: "permission denied", isUnavailable: false) });

        var cut = ctx.Render<JudgeCalibrationAdmin>();

        cut.Markup.Should().Contain("Report unavailable");
        cut.Markup.Should().Contain("permission denied");
        cut.Markup.Should().NotContain("Router unreachable");
        cut.Markup.Should().NotContain("No shadow rows yet");
    }

    [Fact]
    public void A_failed_refresh_after_a_successful_load_never_leaves_the_stale_report_on_screen()
    {
        // The regression this store exists to prevent: every call recomputes, so a report that failed to
        // recompute must never be left standing in for the one that would have replaced it.
        var client = new SequencedClient(
            MakeReport(cohorts: [MakeCohort(judgeModel: "stale-judge-model")]),
            new GrpcAdminException(message: "boom", isUnavailable: false));
        using var ctx = NewContext(client);

        var cut = ctx.Render<JudgeCalibrationAdmin>();
        cut.Markup.Should().Contain("stale-judge-model");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Refresh").Click();

        cut.Markup.Should().NotContain("stale-judge-model");
        cut.Markup.Should().Contain("Report unavailable");
        cut.Markup.Should().Contain("boom");
    }

    [Fact]
    public void Refresh_recomputes_the_report_rather_than_re_reading_a_cached_one()
    {
        var client = new FakeClient(MakeReport());
        using var ctx = NewContext(client);

        var cut = ctx.Render<JudgeCalibrationAdmin>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Refresh").Click();

        // Two calls, not one: the router recomputes per call, so Refresh is a genuine re-run.
        client.CallCount.Should().Be(2);
    }

    private static BunitContext NewContext(IJudgeCalibrationAdminClient client)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new JudgeCalibrationAdminStore(client));
        return ctx;
    }

    /// <summary>Builds a report, defaulting every section the test under way does not exercise to empty.</summary>
    private static JudgeCalibrationReportInfo MakeReport(
        IReadOnlyList<JudgeCalibrationCohortInfo>? cohorts = null,
        IReadOnlyList<JudgeSelfPreferenceRowInfo>? selfPreference = null,
        IReadOnlyList<JudgeCalibrationVerdictInfo>? verdicts = null)
    {
        return new JudgeCalibrationReportInfo(
            GeneratedAtUtc: new DateTimeOffset(2026, 9, 10, 12, 0, 0, offset: TimeSpan.Zero),
            TotalRowsAnalyzed: cohorts?.Sum(c => c.SampleSize) ?? 0,
            Verdicts: verdicts ?? [],
            Cohorts: cohorts ?? [],
            SelfPreference: selfPreference ?? [],
            Markdown: "### Gate conditions");
    }

    /// <summary>Builds one cohort with plausible values, overridable where a test cares.</summary>
    private static JudgeCalibrationCohortInfo MakeCohort(
        string judgeModel = "judge-a",
        bool usedLogprobs = true,
        double? correlation = 0.75)
    {
        return new JudgeCalibrationCohortInfo(
            Dimension: "algorithm",
            JudgeModel: judgeModel,
            UsedLogprobs: usedLogprobs,
            StaticAuthority: StaticGradeAuthorityInfo.Authoritative,
            SampleSize: 30,
            Correlation: correlation,
            MeanAbsoluteDifference: 0.12,
            MeanJudgeScore: 0.6,
            MeanStaticScore: 0.5,
            StandardDeviation: 0.25,
            ModalShare: 0.3,
            DistinctScoreCount: 12);
    }

    /// <summary>Serves one fixed report, or throws one fixed error, counting calls.</summary>
    private sealed class FakeClient(JudgeCalibrationReportInfo? report = null) : IJudgeCalibrationAdminClient
    {
        public GrpcAdminException? Error { get; init; }

        public int CallCount { get; private set; }

        public Task<JudgeCalibrationReportInfo> GetReportAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Error is not null) throw Error;
            return Task.FromResult(report!);
        }
    }

    /// <summary>Succeeds on the first call and fails on every call after, for testing a failed refresh.</summary>
    private sealed class SequencedClient(JudgeCalibrationReportInfo firstReport, GrpcAdminException laterFailure)
        : IJudgeCalibrationAdminClient
    {
        private int _callCount;

        public Task<JudgeCalibrationReportInfo> GetReportAsync(CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == 1) return Task.FromResult(firstReport);
            throw laterFailure;
        }
    }
}
