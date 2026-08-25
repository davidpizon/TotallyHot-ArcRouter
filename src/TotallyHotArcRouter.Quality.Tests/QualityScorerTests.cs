using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Scoring;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers the unified-score mapping and per-dimension weighting: the three axes, the normalize-by-total
/// rule, and - most importantly - the drop-rather-than-zero rule for an axis that could not be measured.
/// </summary>
public class QualityScorerTests
{
    private static QualityScorer CreateScorer(QualityOptions? options = null) =>
        new(Options.Create(options ?? new QualityOptions()));

    private static QualityOptions WithWeights(string dimension, double syntax, double analysis, double judge) =>
        new()
        {
            DimensionWeights =
            {
                [dimension] = new DimensionWeightOptions { Syntax = syntax, Analysis = analysis, Judge = judge },
            },
        };

    /// <summary>A result whose syntax verdict came from a real parser, so no weight halving applies.</summary>
    private static QualityResult Authoritative(bool syntaxValid = true, double? analysis = null, double? judge = null) =>
        new()
        {
            SyntaxValid = syntaxValid,
            SyntaxAuthoritative = true,
            AnalysisScore = analysis,
            JudgeScore = judge,
        };

    [Fact]
    public void Score_RejectsNullResult()
    {
        Assert.Throws<ArgumentNullException>(() => CreateScorer().Score(null!, "code_generation"));
    }

    [Fact]
    public void Score_AllWeightsZero_ReturnsZeroRatherThanDividingByZero()
    {
        var scorer = CreateScorer(WithWeights("zero_weight", 0.0, 0.0, 0.0));

        Assert.Equal(0.0, scorer.Score(Authoritative(analysis: 1.0, judge: 1.0), "zero_weight"));
    }

    [Fact]
    public void Score_EveryAxisPerfect_IsOne()
    {
        var scorer = CreateScorer(WithWeights("d", 0.4, 0.2, 0.4));

        Assert.Equal(1.0, scorer.Score(Authoritative(analysis: 1.0, judge: 1.0), "d"));
    }

    [Fact]
    public void Score_EveryAxisFailing_IsZero()
    {
        var scorer = CreateScorer(WithWeights("d", 0.4, 0.2, 0.4));

        Assert.Equal(0.0, scorer.Score(Authoritative(syntaxValid: false, analysis: 0.0, judge: 0.0), "d"));
    }

    // The central rule. A missing judge grade must be removed from the normalization, not scored zero -
    // otherwise "the judge was switched off" would be indistinguishable from "the judge hated it", and a
    // static-only score could never reach 1.0 no matter how good the code was.
    [Fact]
    public void Score_MissingJudge_DropsJudgeWeightRatherThanScoringItZero()
    {
        var scorer = CreateScorer(WithWeights("d", 0.4, 0.2, 0.4));

        var withoutJudge = scorer.Score(Authoritative(analysis: 1.0, judge: null), "d");

        Assert.Equal(1.0, withoutJudge);
    }

    [Fact]
    public void Score_MissingAnalysis_DropsAnalysisWeightRatherThanScoringItZero()
    {
        var scorer = CreateScorer(WithWeights("d", 0.4, 0.2, 0.4));

        Assert.Equal(1.0, scorer.Score(Authoritative(analysis: null, judge: 1.0), "d"));
    }

    [Fact]
    public void Score_BothOptionalAxesMissing_ScoresOnSyntaxAlone()
    {
        var scorer = CreateScorer(WithWeights("d", 0.4, 0.2, 0.4));

        Assert.Equal(1.0, scorer.Score(Authoritative(analysis: null, judge: null), "d"));
        Assert.Equal(0.0, scorer.Score(Authoritative(syntaxValid: false), "d"));
    }

    // A heuristic verdict is a guess. It still contributes, but at half weight, so a bracket count can
    // never carry as much of a Python score as Roslyn carries of a C# one.
    [Fact]
    public void Score_NonAuthoritativeSyntax_CarriesHalfWeight()
    {
        var scorer = CreateScorer(WithWeights("d", 0.4, 0.0, 0.4));

        var heuristic = new QualityResult { SyntaxValid = true, SyntaxAuthoritative = false, JudgeScore = 0.0 };
        var authoritative = new QualityResult { SyntaxValid = true, SyntaxAuthoritative = true, JudgeScore = 0.0 };

        // Authoritative: (0.4*1 + 0.4*0) / 0.8 = 0.5. Heuristic: (0.2*1 + 0.4*0) / 0.6 = 0.333...
        Assert.Equal(0.5, authoritative.SyntaxAuthoritative ? scorer.Score(authoritative, "d") : -1);
        Assert.True(
            scorer.Score(heuristic, "d") < scorer.Score(authoritative, "d"),
            "a heuristic syntax verdict must move the score less than a parser's");
    }

    [Fact]
    public void Score_WeightsNeedNotSumToOne()
    {
        var scorer = CreateScorer(WithWeights("d", 3.0, 1.0, 6.0));

        // (3*1 + 1*0 + 6*1) / 10 = 0.9
        Assert.Equal(0.9, scorer.Score(Authoritative(analysis: 0.0, judge: 1.0), "d"), 10);
    }

    [Fact]
    public void Score_UnknownDimension_UsesBalancedDefault()
    {
        var scorer = CreateScorer();

        var unknown = scorer.Score(Authoritative(analysis: 1.0, judge: 1.0), "never_configured");

        Assert.Equal(1.0, unknown);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(5.0)]
    public void Score_OutOfRangeAxisValues_AreClampedIntoTheUnitInterval(double rogue)
    {
        var scorer = CreateScorer(WithWeights("d", 0.4, 0.2, 0.4));

        var score = scorer.Score(Authoritative(analysis: rogue, judge: rogue), "d");

        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void Score_AlwaysWithinUnitInterval()
    {
        var scorer = CreateScorer();

        foreach (var syntaxValid in new[] { true, false })
        {
            foreach (var authoritative in new[] { true, false })
            {
                foreach (var analysis in new double?[] { null, 0.0, 0.5, 1.0 })
                {
                    foreach (var judge in new double?[] { null, 0.0, 0.5, 1.0 })
                    {
                        var result = new QualityResult
                        {
                            SyntaxValid = syntaxValid,
                            SyntaxAuthoritative = authoritative,
                            AnalysisScore = analysis,
                            JudgeScore = judge,
                        };

                        Assert.InRange(scorer.Score(result, "code_generation"), 0.0, 1.0);
                    }
                }
            }
        }
    }
}
