using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a judge-calibration call fails. Carries a message fit to render in the Governance panel
/// rather than a raw <see cref="RpcException"/>, mirroring <see cref="RegretHarnessAdminException"/>. See
/// <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class JudgeCalibrationAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="JudgeCalibrationAdminException"/> class.</summary>
    public JudgeCalibrationAdminException(string message, Exception? innerException = null,
        bool isUnavailable = false)
        : base(message: message, innerException: innerException, isUnavailable: isUnavailable)
    {
    }
}

/// <summary>Whether one gate condition passed, failed, lacks data, or can never be evaluated.</summary>
public enum JudgeCalibrationVerdictKindInfo
{
    /// <summary>The condition was evaluated and met.</summary>
    Pass,

    /// <summary>The condition was evaluated and not met.</summary>
    Fail,

    /// <summary>Not enough data to evaluate the condition yet.</summary>
    Insufficient,

    /// <summary>The condition can never be evaluated - waiting for more data will not change it.</summary>
    Unevaluable
}

/// <summary>Whether a real parser or a heuristic produced the static grade a judge score is compared against.</summary>
public enum StaticGradeAuthorityInfo
{
    /// <summary>The row predates the <c>syntax_authoritative</c> column and carries no value.</summary>
    Unknown,

    /// <summary>A heuristic produced the static grade (Python, shell).</summary>
    Heuristic,

    /// <summary>A real parser produced the static grade (C#, JS/TS).</summary>
    Authoritative
}

/// <summary>One G3 gate condition's outcome.</summary>
/// <param name="Condition">A short identifier for the condition (e.g. <c>score-collapse</c>).</param>
/// <param name="Kind">Whether the condition passed, failed, lacks data, or is permanently unevaluable.</param>
/// <param name="Detail">A human-readable explanation naming the cohorts and numbers behind the outcome.</param>
public sealed record JudgeCalibrationVerdictInfo(
    string Condition,
    JudgeCalibrationVerdictKindInfo Kind,
    string Detail);

/// <summary>
/// One comparable slice of the shadow table. Every statistic is nullable for the same reason it is on the
/// wire: a suppressed value (too few rows, or zero variance) and a measured 0.0 are opposite findings, so
/// the panel must be able to tell them apart.
/// </summary>
/// <param name="Dimension">The task dimension these rows were graded under.</param>
/// <param name="JudgeModel">The backbone that produced these judge scores.</param>
/// <param name="UsedLogprobs">Whether these scores were probability-weighted rather than parsed from a single sample.</param>
/// <param name="StaticAuthority">Whether a real parser produced the static grades being compared against.</param>
/// <param name="SampleSize">The number of rows in this cohort.</param>
/// <param name="Correlation">Spearman rank correlation between judge and static score, or <see langword="null"/> when suppressed.</param>
/// <param name="MeanAbsoluteDifference">The mean of <c>|judge − static|</c>, or <see langword="null"/> for an empty cohort.</param>
/// <param name="MeanJudgeScore">The cohort's mean judge score.</param>
/// <param name="MeanStaticScore">The cohort's mean static score.</param>
/// <param name="StandardDeviation">The spread of the judge's own scores - near zero means score collapse.</param>
/// <param name="ModalShare">The share of the cohort taken by its single most common score.</param>
/// <param name="DistinctScoreCount">The number of distinct judge scores present in the cohort.</param>
public sealed record JudgeCalibrationCohortInfo(
    string Dimension,
    string JudgeModel,
    bool UsedLogprobs,
    StaticGradeAuthorityInfo StaticAuthority,
    int SampleSize,
    double? Correlation,
    double? MeanAbsoluteDifference,
    double? MeanJudgeScore,
    double? MeanStaticScore,
    double? StandardDeviation,
    double? ModalShare,
    int DistinctScoreCount);

/// <summary>
/// One candidate model's judge-minus-static delta, within one judge backbone and one (dimension,
/// logprobs, static authority) cohort - never pooled across cohorts, matching every other statistic in
/// the report.
/// </summary>
/// <param name="Dimension">The task dimension these rows were graded under.</param>
/// <param name="JudgeModel">The backbone whose grading is described.</param>
/// <param name="UsedLogprobs">Whether these scores were probability-weighted.</param>
/// <param name="StaticAuthority">Whether a real parser produced the static grades being compared against.</param>
/// <param name="CandidateModel">The model whose responses were graded.</param>
/// <param name="MeanScoreDelta">Mean judge score minus mean static score; positive means the judge is more generous.</param>
/// <param name="IsOwnBackbone">Whether the candidate is the judge's own backbone - G-Eval's self-preference case.</param>
/// <param name="SampleSize">The number of rows behind this candidate's means.</param>
public sealed record JudgeSelfPreferenceRowInfo(
    string Dimension,
    string JudgeModel,
    bool UsedLogprobs,
    StaticGradeAuthorityInfo StaticAuthority,
    string CandidateModel,
    double MeanScoreDelta,
    bool IsOwnBackbone,
    int SampleSize);

/// <summary>
/// A freshly computed calibration report. Unlike <see cref="RegretHarnessStatusInfo"/> there is no
/// "has run" flag: the server recomputes on every call, so the only empty state is an empty table.
/// </summary>
/// <param name="GeneratedAtUtc">When the server computed this report.</param>
/// <param name="TotalRowsAnalyzed">The total <c>judge_shadow_scores</c> row count behind it.</param>
/// <param name="Verdicts">The outcome of every G3 gate condition.</param>
/// <param name="Cohorts">One entry per comparable slice of the shadow table.</param>
/// <param name="SelfPreference">Per-candidate judge-minus-static deltas, carrying no verdict by design.</param>
/// <param name="Markdown">The same rendering the <c>--run-judge-calibration-report</c> CLI flag prints.</param>
public sealed record JudgeCalibrationReportInfo(
    DateTimeOffset GeneratedAtUtc,
    int TotalRowsAnalyzed,
    IReadOnlyList<JudgeCalibrationVerdictInfo> Verdicts,
    IReadOnlyList<JudgeCalibrationCohortInfo> Cohorts,
    IReadOnlyList<JudgeSelfPreferenceRowInfo> SelfPreference,
    string Markdown);

/// <summary>
/// Client for the proxy's <c>JudgeCalibrationAdminService</c> - the Governance → Judge Calibration panel's
/// read surface (docs/router/geval-shadow-scoring-plan.md Phase G2). Lives in this plain <c>net10.0</c>
/// library rather than the Windows-only MAUI project so CI can unit-test it, exactly like
/// <see cref="RegretHarnessAdminClient"/>.
/// </summary>
public sealed class JudgeCalibrationAdminClient
    : GrpcAdminClientBase<Contract.JudgeCalibrationAdminService.JudgeCalibrationAdminServiceClient,
            JudgeCalibrationAdminException>,
        IJudgeCalibrationAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JudgeCalibrationAdminClient"/> class, creating and
    /// owning a channel to <paramref name="serverAddress"/>.
    /// </summary>
    public JudgeCalibrationAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress: serverAddress,
            createClient: callInvoker =>
                new Contract.JudgeCalibrationAdminService.JudgeCalibrationAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JudgeCalibrationAdminClient"/> class over a
    /// caller-supplied generated client. The seam tests use to substitute a fake without a live server;
    /// the caller owns the channel's lifetime.
    /// </summary>
    public JudgeCalibrationAdminClient(
        Contract.JudgeCalibrationAdminService.JudgeCalibrationAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<JudgeCalibrationReportInfo> GetReportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetJudgeCalibrationReportAsync(request: new Contract.GetJudgeCalibrationReportRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new JudgeCalibrationReportInfo(
                GeneratedAtUtc: response.GeneratedAtUtc?.ToDateTimeOffset() ?? default,
                TotalRowsAnalyzed: response.TotalRowsAnalyzed,
                Verdicts: [.. response.Verdicts.Select(MapVerdict)],
                Cohorts: [.. response.Cohorts.Select(MapCohort)],
                SelfPreference: [.. response.SelfPreference.Select(MapSelfPreference)],
                Markdown: response.Markdown);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the judge calibration report");
        }
    }

    /// <inheritdoc/>
    protected override JudgeCalibrationAdminException CreateException(string message, Exception? innerException,
        bool isUnavailable)
    {
        return new JudgeCalibrationAdminException(message: message, innerException: innerException,
            isUnavailable: isUnavailable);
    }

    /// <summary>Converts a gRPC-contract verdict into the client's <see cref="JudgeCalibrationVerdictInfo"/>.</summary>
    private static JudgeCalibrationVerdictInfo MapVerdict(Contract.JudgeCalibrationVerdict verdict)
    {
        return new JudgeCalibrationVerdictInfo(
            Condition: verdict.Condition,
            Kind: verdict.Kind switch
            {
                Contract.JudgeCalibrationVerdictKind.Pass => JudgeCalibrationVerdictKindInfo.Pass,
                Contract.JudgeCalibrationVerdictKind.Fail => JudgeCalibrationVerdictKindInfo.Fail,
                Contract.JudgeCalibrationVerdictKind.Insufficient => JudgeCalibrationVerdictKindInfo.Insufficient,
                // Unspecified maps here too: a verdict the server could not categorize is one this panel
                // must not render as a pass.
                _ => JudgeCalibrationVerdictKindInfo.Unevaluable
            },
            Detail: verdict.Detail);
    }

    /// <summary>Converts a gRPC-contract cohort into the client's <see cref="JudgeCalibrationCohortInfo"/>.</summary>
    private static JudgeCalibrationCohortInfo MapCohort(Contract.JudgeCalibrationCohort cohort)
    {
        return new JudgeCalibrationCohortInfo(
            Dimension: cohort.Dimension,
            JudgeModel: cohort.JudgeModel,
            UsedLogprobs: cohort.UsedLogprobs,
            StaticAuthority: cohort.StaticAuthority switch
            {
                Contract.StaticGradeAuthority.Heuristic => StaticGradeAuthorityInfo.Heuristic,
                Contract.StaticGradeAuthority.Authoritative => StaticGradeAuthorityInfo.Authoritative,
                _ => StaticGradeAuthorityInfo.Unknown
            },
            SampleSize: cohort.SampleSize,
            Correlation: cohort.Correlation,
            MeanAbsoluteDifference: cohort.MeanAbsoluteDifference,
            MeanJudgeScore: cohort.MeanJudgeScore,
            MeanStaticScore: cohort.MeanStaticScore,
            StandardDeviation: cohort.StandardDeviation,
            ModalShare: cohort.ModalShare,
            DistinctScoreCount: cohort.DistinctScoreCount);
    }

    /// <summary>Converts a gRPC-contract self-preference row into the client's <see cref="JudgeSelfPreferenceRowInfo"/>.</summary>
    private static JudgeSelfPreferenceRowInfo MapSelfPreference(Contract.JudgeSelfPreferenceRow row)
    {
        return new JudgeSelfPreferenceRowInfo(
            Dimension: row.Dimension,
            JudgeModel: row.JudgeModel,
            UsedLogprobs: row.UsedLogprobs,
            StaticAuthority: row.StaticAuthority switch
            {
                Contract.StaticGradeAuthority.Heuristic => StaticGradeAuthorityInfo.Heuristic,
                Contract.StaticGradeAuthority.Authoritative => StaticGradeAuthorityInfo.Authoritative,
                _ => StaticGradeAuthorityInfo.Unknown
            },
            CandidateModel: row.CandidateModel,
            MeanScoreDelta: row.MeanScoreDelta,
            IsOwnBackbone: row.IsOwnBackbone,
            SampleSize: row.SampleSize);
    }
}
