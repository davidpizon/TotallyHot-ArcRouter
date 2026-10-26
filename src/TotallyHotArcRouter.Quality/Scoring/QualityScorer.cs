using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Quality.Scoring;

/// <summary>
/// Collapses syntax, static-analysis, judge, and extra-grader signals into the unified score using
/// <em>fixed</em> per-dimension weights that sum to 1 (Zhou et al. Eq. 8; ADR-0026). Missing axes
/// contribute zero rather than shrinking the denominator — abstentions are expressed as
/// <see cref="GradeStatus"/>, not as a silent weight shift.
/// </summary>
public sealed class QualityScorer : IQualityScorer
{
    private readonly QualityOptions _options;

    /// <summary>Initializes a new instance of the <see cref="QualityScorer"/> class.</summary>
    /// <param name="options">The quality options carrying per-dimension weights.</param>
    public QualityScorer(IOptions<QualityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc/>
    public double Score(QualityResult result, string dimension)
    {
        ArgumentNullException.ThrowIfNull(result);

        var weights = _options.ResolveWeights(dimension: dimension, recipeId: result.RecipeId);

        var sSyntax = result.SyntaxValid ? 1.0 : 0.0;
        var sAnalysis = result.AnalysisScore is { } analysis ? Math.Clamp(value: analysis, 0.0, 1.0) : 0.0;
        double sJudge = 0.0;
        if (result.AllowLocalGradeOfRecord)
        {
            if (result.LocalScore is { } local) sJudge = Math.Clamp(value: local, 0.0, 1.0);
        }
        else if (result.JudgeScore is { } judge)
        {
            sJudge = Math.Clamp(value: judge, 0.0, 1.0);
        }

        var weightedSum = weights.Syntax * sSyntax + weights.Analysis * sAnalysis + weights.Judge * sJudge;

        foreach (var (grader, score) in result.GraderScores)
        {
            var weight = weights.ResolveExtraWeight(graderKey: grader);
            if (weight <= 0.0) continue;

            weightedSum += weight * Math.Clamp(value: score, 0.0, 1.0);
        }

        return Math.Clamp(value: weightedSum, 0.0, 1.0);
    }
}
