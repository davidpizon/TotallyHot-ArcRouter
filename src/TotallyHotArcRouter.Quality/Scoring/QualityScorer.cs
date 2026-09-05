using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Quality.Scoring;

/// <summary>
/// Maps a populated <see cref="QualityResult"/>'s syntax, static-analysis, and judge signals onto the
/// unified score u_i in [0,1], using per-dimension weights. Weights need not pre-sum to 1; the scorer
/// normalizes by their total, and drops any component that does not apply to a given result rather than
/// scoring it zero.
/// </summary>
/// <remarks>
/// The drop-rather-than-zero rule is the whole design, and it is worth stating plainly: an axis that could
/// not be measured is removed from both numerator and denominator, so a result is never marked down for a
/// signal the harness failed to collect. Scoring an absent judge grade as zero would make "the judge was
/// switched off" indistinguishable from "the judge hated it", and the router would learn from the
/// difference as though it were evidence about the model.
/// </remarks>
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

        var weights = _options.ResolveWeights(dimension);

        var wSyntax = weights.Syntax;
        var wAnalysis = weights.Analysis;
        var wJudge = weights.Judge;

        var sSyntax = result.SyntaxValid ? 1.0 : 0.0;

        // A heuristic verdict is a guess, not a compiler's answer, so it carries half the weight a real
        // parser's would. Halving the weight rather than the score is deliberate: a confident-but-cheap
        // "this looks fine" should move the total less, not report a worse snippet than it saw.
        if (!result.SyntaxAuthoritative) wSyntax *= 0.5;

        var sAnalysis = 0.0;
        if (result.AnalysisScore is { } analysis)
            sAnalysis = Math.Clamp(value: analysis, 0.0, 1.0);
        else
            wAnalysis = 0.0;

        var sJudge = 0.0;
        if (result.JudgeScore is { } judge)
            sJudge = Math.Clamp(value: judge, 0.0, 1.0);
        else
            wJudge = 0.0;

        var weightedSum = wSyntax * sSyntax + wAnalysis * sAnalysis + wJudge * sJudge;
        var totalWeight = wSyntax + wAnalysis + wJudge;

        // Graders beyond the three built-in axes (Phase Q3+) contribute through this keyed extension
        // point rather than a named local, so registering one never requires touching this method again.
        // Empty for every result today, which is exactly what keeps this byte-identical to the pre-Q1 blend.
        foreach (var (grader, score) in result.GraderScores)
        {
            var weight = weights.ResolveExtraWeight(graderKey: grader);
            if (weight <= 0.0) continue;

            weightedSum += weight * Math.Clamp(value: score, 0.0, 1.0);
            totalWeight += weight;
        }

        if (totalWeight <= 0.0) return 0.0;

        var u = weightedSum / totalWeight;
        return Math.Clamp(value: u, 0.0, 1.0);
    }
}