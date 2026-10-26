using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Decides <see cref="GradeStatus"/> from a scored result and the dimension's fixed weights. An abstention
/// changes status, never the weights (ADR-0026). The judge axis has four cases. A local score fills that
/// axis only when the caller says it is the grade of record.
/// </summary>
public static class GradeStatusCalculator
{
    /// <summary>
    /// Minimum <see cref="QualityResult.SyntaxConfidence"/> required for a <see cref="GradeStatus.Complete"/>
    /// grade. Below this, a heuristic guess must not train the router.
    /// </summary>
    public const double MinimumSyntaxConfidenceForComplete = 0.70;

    /// <summary>
    /// Computes the grade status for <paramref name="result"/> under <paramref name="weights"/>. A
    /// pre-stamped <see cref="QualityResult.IncompleteReason"/> is always Incomplete. Syntax confidence
    /// and missing analysis only withhold Complete when those axes have nonzero weight. The four judge-axis
    /// cases log once, at Debug, after the status they actually return is known. A null logger writes nothing.
    /// </summary>
    /// <param name="result">The result whose axes and confidence are inspected.</param>
    /// <param name="weights">The fixed per-dimension weights (must already sum to 1 at startup).</param>
    /// <param name="llmGraderWasAvailable">
    /// <see langword="true"/> when at least one weighted LLM grader (judge or portfolio) was expected to
    /// answer for this request; distinguishes <see cref="GradeStatus.StructureOnly"/> from
    /// <see cref="GradeStatus.Incomplete"/>.
    /// </param>
    /// <param name="logger">Debug logger for the four judge-axis templates. Null writes nothing.</param>
    /// <param name="localScoreIsGradeOfRecord">
    /// Whether <see cref="QualityResult.LocalScore"/> fills the judge axis. False keeps a missing
    /// <see cref="QualityResult.JudgeScore"/> as case 3 or case 4.
    /// </param>
    /// <param name="weightsVersion">The weights version the receipt will store. Logged, not interpreted.</param>
    /// <param name="graderKind">The grader kind, <c>code</c> or <c>non-code</c>. Logged, not interpreted.</param>
    /// <returns>The status to stamp onto the result before observation.</returns>
    public static GradeStatus Calculate(
        QualityResult result,
        DimensionWeightOptions weights,
        bool llmGraderWasAvailable,
        ILogger? logger = null,
        bool localScoreIsGradeOfRecord = false,
        string? weightsVersion = null,
        string? graderKind = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(weights);

        if (!string.IsNullOrEmpty(result.IncompleteReason)) return GradeStatus.Incomplete;

        if (!result.QuestionPresent) return GradeStatus.Incomplete;

        if (weights.Syntax > 0.0 && result.SyntaxConfidence < MinimumSyntaxConfidenceForComplete)
            return GradeStatus.Incomplete;

        if (weights.Analysis > 0.0 && result.AnalysisScore is null) return GradeStatus.Incomplete;

        if (weights.Judge > 0.0 && result.JudgeScore is null && !localScoreIsGradeOfRecord)
        {
            var withheld = llmGraderWasAvailable ? GradeStatus.Incomplete : GradeStatus.StructureOnly;
            if (result.LocalScore is not null)
            {
                logger?.LogDebug(
                    message: "Judge axis case 3 local score not of record for correlation {CorrelationId} weights {WeightsVersion} kind {Kind} status {Status} local grader {LocalGraderVersion} flag {LocalScoreIsGradeOfRecord}.",
                    result.RequestCorrelationId,
                    weightsVersion,
                    graderKind,
                    withheld,
                    result.LocalGraderVersion,
                    localScoreIsGradeOfRecord);
            }
            else
            {
                logger?.LogDebug(
                    message: "Judge axis case 4 neither score for correlation {CorrelationId} weights {WeightsVersion} kind {Kind} status {Status}.",
                    result.RequestCorrelationId,
                    weightsVersion,
                    graderKind,
                    withheld);
            }

            return withheld;
        }

        foreach (var (graderKey, weight) in weights.ExtraWeights)
        {
            if (weight <= 0.0) continue;
            if (!result.GraderScores.ContainsKey(graderKey))
            {
                var withheld = llmGraderWasAvailable ? GradeStatus.Incomplete : GradeStatus.StructureOnly;
                LogRecordedAxis(logger, result, localScoreIsGradeOfRecord, weightsVersion, graderKind, withheld);
                return withheld;
            }
        }

        LogRecordedAxis(logger, result, localScoreIsGradeOfRecord, weightsVersion, graderKind, GradeStatus.Complete);
        return GradeStatus.Complete;
    }

    /// <summary>
    /// Cases 1 and 2 log after the extra-weight loop, so the status is Complete only when that loop passed.
    /// Cases 3 and 4 return before this method.
    /// </summary>
    private static void LogRecordedAxis(
        ILogger? logger,
        QualityResult result,
        bool localScoreIsGradeOfRecord,
        string? weightsVersion,
        string? graderKind,
        GradeStatus status)
    {
        if (result.JudgeScore is not null)
        {
            logger?.LogDebug(
                message: "Judge axis case 1 teacher score for correlation {CorrelationId} weights {WeightsVersion} kind {Kind} status {Status}.",
                result.RequestCorrelationId,
                weightsVersion,
                graderKind,
                status);
            return;
        }

        if (localScoreIsGradeOfRecord && result.LocalScore is not null)
        {
            logger?.LogDebug(
                message: "Judge axis case 2 local grade of record for correlation {CorrelationId} weights {WeightsVersion} kind {Kind} status {Status} local grader {LocalGraderVersion} flag {LocalScoreIsGradeOfRecord}.",
                result.RequestCorrelationId,
                weightsVersion,
                graderKind,
                status,
                result.LocalGraderVersion,
                localScoreIsGradeOfRecord);
        }
    }
}
