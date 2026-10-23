namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Decides <see cref="GradeStatus"/> from a scored result and the dimension's fixed weights. An abstention
/// changes status, never the weights (ADR-0026).
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
    /// and missing analysis only withhold Complete when those axes have nonzero weight.
    /// </summary>
    /// <param name="result">The result whose axes and confidence are inspected.</param>
    /// <param name="weights">The fixed per-dimension weights (must already sum to 1 at startup).</param>
    /// <param name="llmGraderWasAvailable">
    /// <see langword="true"/> when at least one weighted LLM grader (judge or portfolio) was expected to
    /// answer for this request; distinguishes <see cref="GradeStatus.StructureOnly"/> from
    /// <see cref="GradeStatus.Incomplete"/>.
    /// </param>
    /// <returns>The status to stamp onto the result before observation.</returns>
    public static GradeStatus Calculate(
        QualityResult result,
        DimensionWeightOptions weights,
        bool llmGraderWasAvailable)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(weights);

        if (!string.IsNullOrEmpty(result.IncompleteReason)) return GradeStatus.Incomplete;

        if (!result.QuestionPresent) return GradeStatus.Incomplete;

        if (weights.Syntax > 0.0 && result.SyntaxConfidence < MinimumSyntaxConfidenceForComplete)
            return GradeStatus.Incomplete;

        if (weights.Analysis > 0.0 && result.AnalysisScore is null) return GradeStatus.Incomplete;

        if (weights.Judge > 0.0 && result.JudgeScore is null)
            return llmGraderWasAvailable ? GradeStatus.Incomplete : GradeStatus.StructureOnly;

        foreach (var (graderKey, weight) in weights.ExtraWeights)
        {
            if (weight <= 0.0) continue;
            if (!result.GraderScores.ContainsKey(graderKey))
                return llmGraderWasAvailable ? GradeStatus.Incomplete : GradeStatus.StructureOnly;
        }

        return GradeStatus.Complete;
    }
}
