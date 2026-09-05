using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Scoring;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers the unified-score mapping and per-dimension weighting: the three axes, the normalize-by-total
/// rule, and - most importantly - the drop-rather-than-zero rule for an axis that could not be measured.
/// </summary>
public class QualityScorerTests
{
    private static QualityScorer CreateScorer(QualityOptions? options = null)
    {
        return new QualityScorer(Options.Create(options ?? new QualityOptions()));
    }

    private static QualityOptions WithWeights(string dimension, double syntax, double analysis, double judge)
    {
        return new QualityOptions
        {
            DimensionWeights =
            {
                [dimension] = new DimensionWeightOptions { Syntax = syntax, Analysis = analysis, Judge = judge }
            }
        };
    }

    /// <summary>A result whose syntax verdict came from a real parser, so no weight halving applies.</summary>
    private static QualityResult Authoritative(bool syntaxValid = true, double? analysis = null, double? judge = null)
    {
        return new QualityResult
        {
            SyntaxValid = syntaxValid,
            SyntaxAuthoritative = true,
            AnalysisScore = analysis,
            JudgeScore = judge
        };
    }

    [Fact]
    public void Score_RejectsNullResult()
    {
        Assert.Throws<ArgumentNullException>(() => CreateScorer().Score(result: null!, dimension: "code_generation"));
    }

    [Fact]
    public void Score_AllWeightsZero_ReturnsZeroRatherThanDividingByZero()
    {
        var scorer = CreateScorer(WithWeights(dimension: "zero_weight", 0.0, 0.0, 0.0));

        Assert.Equal(0.0,
            actual: scorer.Score(result: Authoritative(analysis: 1.0, judge: 1.0), dimension: "zero_weight"));
    }

    [Fact]
    public void Score_EveryAxisPerfect_IsOne()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.2, 0.4));

        Assert.Equal(1.0, actual: scorer.Score(result: Authoritative(analysis: 1.0, judge: 1.0), dimension: "d"));
    }

    [Fact]
    public void Score_EveryAxisFailing_IsZero()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.2, 0.4));

        Assert.Equal(0.0, actual: scorer.Score(result: Authoritative(false, 0.0, 0.0), dimension: "d"));
    }

    // The central rule. A missing judge grade must be removed from the normalization, not scored zero -
    // otherwise "the judge was switched off" would be indistinguishable from "the judge hated it", and a
    // static-only score could never reach 1.0 no matter how good the code was.
    [Fact]
    public void Score_MissingJudge_DropsJudgeWeightRatherThanScoringItZero()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.2, 0.4));

        var withoutJudge = scorer.Score(result: Authoritative(analysis: 1.0, judge: null), dimension: "d");

        Assert.Equal(1.0, actual: withoutJudge);
    }

    [Fact]
    public void Score_MissingAnalysis_DropsAnalysisWeightRatherThanScoringItZero()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.2, 0.4));

        Assert.Equal(1.0, actual: scorer.Score(result: Authoritative(analysis: null, judge: 1.0), dimension: "d"));
    }

    [Fact]
    public void Score_BothOptionalAxesMissing_ScoresOnSyntaxAlone()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.2, 0.4));

        Assert.Equal(1.0, actual: scorer.Score(result: Authoritative(analysis: null, judge: null), dimension: "d"));
        Assert.Equal(0.0, actual: scorer.Score(result: Authoritative(syntaxValid: false), dimension: "d"));
    }

    // A heuristic verdict is a guess. It still contributes, but at half weight, so a bracket count can
    // never carry as much of a Python score as Roslyn carries of a C# one.
    [Fact]
    public void Score_NonAuthoritativeSyntax_CarriesHalfWeight()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.0, 0.4));

        var heuristic = new QualityResult { SyntaxValid = true, SyntaxAuthoritative = false, JudgeScore = 0.0 };
        var authoritative = new QualityResult { SyntaxValid = true, SyntaxAuthoritative = true, JudgeScore = 0.0 };

        // Authoritative: (0.4*1 + 0.4*0) / 0.8 = 0.5. Heuristic: (0.2*1 + 0.4*0) / 0.6 = 0.333...
        Assert.Equal(0.5,
            actual: authoritative.SyntaxAuthoritative ? scorer.Score(result: authoritative, dimension: "d") : -1);
        Assert.True(
            condition: scorer.Score(result: heuristic, dimension: "d") <
                       scorer.Score(result: authoritative, dimension: "d"),
            userMessage: "a heuristic syntax verdict must move the score less than a parser's");
    }

    [Fact]
    public void Score_WeightsNeedNotSumToOne()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 3.0, 1.0, 6.0));

        // (3*1 + 1*0 + 6*1) / 10 = 0.9
        Assert.Equal(0.9, actual: scorer.Score(result: Authoritative(analysis: 0.0, judge: 1.0), dimension: "d"), 10);
    }

    [Fact]
    public void Score_UnknownDimension_UsesBalancedDefault()
    {
        var scorer = CreateScorer();

        var unknown = scorer.Score(result: Authoritative(analysis: 1.0, judge: 1.0), dimension: "never_configured");

        Assert.Equal(1.0, actual: unknown);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(5.0)]
    public void Score_OutOfRangeAxisValues_AreClampedIntoTheUnitInterval(double rogue)
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.2, 0.4));

        var score = scorer.Score(result: Authoritative(analysis: rogue, judge: rogue), dimension: "d");

        Assert.InRange(actual: score, 0.0, 1.0);
    }

    // Phase Q1: an extra grader beyond the three built-in axes contributes through GraderScores/
    // ExtraWeights without this class special-casing its name.
    [Fact]
    public void Score_ExtraGraderWithConfiguredWeight_AddsWeightedAxis()
    {
        var scorer = CreateScorer(new QualityOptions
        {
            DimensionWeights =
            {
                ["d"] = new DimensionWeightOptions
                {
                    Syntax = 0.4,
                    Analysis = 0.0,
                    Judge = 0.0,
                    ExtraWeights = new Dictionary<string, double> { ["codejudge"] = 0.6 }
                }
            }
        });

        var result = Authoritative(analysis: null, judge: null) with
        {
            GraderScores = new Dictionary<string, double> { ["codejudge"] = 0.0 }
        };

        // Syntax at weight 0.4 scores 1.0; the extra grader at weight 0.6 scores 0.0.
        // (0.4*1 + 0.6*0) / 1.0 = 0.4.
        Assert.Equal(0.4, actual: scorer.Score(result: result, dimension: "d"), 10);
    }

    [Fact]
    public void Score_ExtraGraderWithNoConfiguredWeight_IsDroppedRatherThanScoredZero()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.0, 0.0));

        var result = Authoritative(analysis: null, judge: null) with
        {
            GraderScores = new Dictionary<string, double> { ["unconfigured_grader"] = 0.0 }
        };

        // The unconfigured extra grader must not drag the score down: syntax alone still scores 1.0.
        Assert.Equal(1.0, actual: scorer.Score(result: result, dimension: "d"));
    }

    [Fact]
    public void Score_NoExtraGraders_MatchesPreQ1Blend()
    {
        // The default QualityResult carries an empty GraderScores map, so the loop over it is a no-op and
        // the result is exactly the three-axis blend Q1 must not change.
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.2, 0.4));

        Assert.Equal(1.0, actual: scorer.Score(result: Authoritative(analysis: 1.0, judge: 1.0), dimension: "d"));
        Assert.Empty(collection: new QualityResult().GraderScores);
    }

    [Fact]
    public void Score_AlwaysWithinUnitInterval()
    {
        var scorer = CreateScorer();

        foreach (var syntaxValid in new[] { true, false })
            foreach (var authoritative in new[] { true, false })
                foreach (var analysis in new double?[] { null, 0.0, 0.5, 1.0 })
                    foreach (var judge in new double?[] { null, 0.0, 0.5, 1.0 })
                    {
                        var result = new QualityResult
                        {
                            SyntaxValid = syntaxValid,
                            SyntaxAuthoritative = authoritative,
                            AnalysisScore = analysis,
                            JudgeScore = judge
                        };

                        Assert.InRange(actual: scorer.Score(result: result, dimension: "code_generation"), 0.0, 1.0);
                    }
    }
}