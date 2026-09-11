using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// gRPC service backing the Governance → Judge Calibration panel
/// (docs/router/geval-shadow-scoring-plan.md Phase G2): recomputes and returns the judge-vs-static
/// calibration report over the accumulated <c>judge_shadow_scores</c> rows. Mapped by
/// <see cref="Proxy.ProxyServer"/> onto the same loopback TLS endpoint as <c>TelemetryService</c> and the
/// other admin services, unconditionally - like <c>RegretHarnessAdminGrpcService</c>, its one dependency
/// is never an optional feature.
/// </summary>
/// <remarks>
/// Unary-only, deliberately unlike the streaming admin services beside it. Those stream because they run
/// something slow that mutates state and the operator needs progress; this one groups and correlates rows
/// from a table already bounded by <see cref="JudgeOptions.MaxRows"/>, so there is no progress worth
/// reporting and - crucially - no "last run" to remember. Every call recomputes, which also means the
/// panel can never show a stale verdict for a judge whose behavior has since changed.
/// </remarks>
public sealed class JudgeCalibrationAdminGrpcService
    : Contract.JudgeCalibrationAdminService.JudgeCalibrationAdminServiceBase
{
    private readonly IJudgeCalibrationAnalyzer _analyzer;

    /// <summary>Initializes a new instance of the <see cref="JudgeCalibrationAdminGrpcService"/> class.</summary>
    /// <param name="analyzer">Computes the report this service returns.</param>
    public JudgeCalibrationAdminGrpcService(IJudgeCalibrationAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _analyzer = analyzer;
    }

    /// <inheritdoc/>
    public override async Task<Contract.JudgeCalibrationReportResponse> GetJudgeCalibrationReport(
        Contract.GetJudgeCalibrationReportRequest request,
        ServerCallContext context)
    {
        var report = await _analyzer.AnalyzeAsync(context.CancellationToken).ConfigureAwait(false);

        return new Contract.JudgeCalibrationReportResponse
        {
            GeneratedAtUtc = Timestamp.FromDateTimeOffset(report.GeneratedAtUtc),
            TotalRowsAnalyzed = report.TotalRowsAnalyzed,
            Verdicts = { report.Verdicts.Select(MapVerdict) },
            Cohorts = { report.Cohorts.Select(MapCohort) },
            SelfPreference = { report.SelfPreference.Select(MapSelfPreference) },
            Markdown = JudgeCalibrationReportFormatter.FormatMarkdown(report)
        };
    }

    /// <summary>Converts one domain verdict onto its wire message.</summary>
    private static Contract.JudgeCalibrationVerdict MapVerdict(JudgeCalibrationVerdict verdict)
    {
        return new Contract.JudgeCalibrationVerdict
        {
            Condition = verdict.Condition,
            Kind = verdict.Kind switch
            {
                JudgeCalibrationVerdictKind.Pass => Contract.JudgeCalibrationVerdictKind.Pass,
                JudgeCalibrationVerdictKind.Fail => Contract.JudgeCalibrationVerdictKind.Fail,
                JudgeCalibrationVerdictKind.Insufficient => Contract.JudgeCalibrationVerdictKind.Insufficient,
                JudgeCalibrationVerdictKind.Unevaluable => Contract.JudgeCalibrationVerdictKind.Unevaluable,
                _ => Contract.JudgeCalibrationVerdictKind.Unspecified
            },
            Detail = verdict.Detail
        };
    }

    /// <summary>
    /// Converts one domain cohort onto its wire message. Each nullable statistic maps to an unset wrapper
    /// rather than a zero, so a suppressed correlation stays distinguishable from a measured 0.0 on the
    /// wire - they are opposite findings and the panel renders them differently.
    /// </summary>
    private static Contract.JudgeCalibrationCohort MapCohort(JudgeCalibrationCohort cohort)
    {
        return new Contract.JudgeCalibrationCohort
        {
            Dimension = cohort.Dimension,
            JudgeModel = cohort.JudgeModel,
            UsedLogprobs = cohort.UsedLogprobs,
            StaticAuthority = cohort.StaticAuthority switch
            {
                StaticGradeAuthority.Heuristic => Contract.StaticGradeAuthority.Heuristic,
                StaticGradeAuthority.Authoritative => Contract.StaticGradeAuthority.Authoritative,
                _ => Contract.StaticGradeAuthority.Unknown
            },
            SampleSize = cohort.SampleSize,
            Correlation = cohort.Agreement.Correlation,
            MeanAbsoluteDifference = cohort.Agreement.MeanAbsoluteDifference,
            MeanJudgeScore = cohort.Agreement.MeanJudgeScore,
            MeanStaticScore = cohort.Agreement.MeanStaticScore,
            StandardDeviation = cohort.Distribution.StandardDeviation,
            ModalShare = cohort.Distribution.ModalShare,
            DistinctScoreCount = cohort.Distribution.DistinctScoreCount
        };
    }

    /// <summary>Converts one domain self-preference row onto its wire message.</summary>
    private static Contract.JudgeSelfPreferenceRow MapSelfPreference(JudgeSelfPreferenceRow row)
    {
        return new Contract.JudgeSelfPreferenceRow
        {
            Dimension = row.Dimension,
            JudgeModel = row.JudgeModel,
            UsedLogprobs = row.UsedLogprobs,
            StaticAuthority = row.StaticAuthority switch
            {
                StaticGradeAuthority.Heuristic => Contract.StaticGradeAuthority.Heuristic,
                StaticGradeAuthority.Authoritative => Contract.StaticGradeAuthority.Authoritative,
                _ => Contract.StaticGradeAuthority.Unknown
            },
            CandidateModel = row.CandidateModel,
            MeanScoreDelta = row.MeanScoreDelta,
            IsOwnBackbone = row.IsOwnBackbone,
            SampleSize = row.SampleSize
        };
    }
}
