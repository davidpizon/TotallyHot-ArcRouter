using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Scoring;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers the fixed-weight unified score (ADR-0026 / issue #293): missing axes contribute zero rather
/// than shrinking the denominator, and heuristic syntax no longer halves its weight.
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

    private static QualityResult Authoritative(bool syntaxValid = true, double? analysis = null, double? judge = null)
    {
        return new QualityResult
        {
            SyntaxValid = syntaxValid,
            SyntaxAuthoritative = true,
            SyntaxConfidence = 1.0,
            QuestionPresent = true,
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
    public void Score_AllWeightsZero_ReturnsZero()
    {
        var scorer = CreateScorer(WithWeights(dimension: "zero_weight", 0.0, 0.0, 0.0));

        Assert.Equal(0.0,
            actual: scorer.Score(result: Authoritative(analysis: 1.0, judge: 1.0), dimension: "zero_weight"));
    }

    [Fact]
    public void Score_EveryAxisPerfect_IsOne()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.25, 0.15, 0.60));

        Assert.Equal(1.0, actual: scorer.Score(result: Authoritative(analysis: 1.0, judge: 1.0), dimension: "d"));
    }

    [Fact]
    public void Score_EveryAxisFailing_IsZero()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.25, 0.15, 0.60));

        Assert.Equal(0.0, actual: scorer.Score(result: Authoritative(false, 0.0, 0.0), dimension: "d"));
    }

    [Fact]
    public void Score_MissingJudge_ContributesZeroRatherThanRenormalizing()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.25, 0.15, 0.60));

        // Perfect syntax+analysis, absent judge: 0.25 + 0.15 + 0 = 0.40 (not renormalized to 1.0).
        Assert.Equal(0.40,
            actual: scorer.Score(result: Authoritative(analysis: 1.0, judge: null), dimension: "d"), 10);
    }

    [Fact]
    public void Score_MissingAnalysis_ContributesZeroRatherThanRenormalizing()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.25, 0.15, 0.60));

        Assert.Equal(0.85,
            actual: scorer.Score(result: Authoritative(analysis: null, judge: 1.0), dimension: "d"), 10);
    }

    [Fact]
    public void Score_BothOptionalAxesMissing_IsSyntaxWeightAlone()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.25, 0.15, 0.60));

        Assert.Equal(0.25, actual: scorer.Score(result: Authoritative(analysis: null, judge: null), dimension: "d"),
            10);
        Assert.Equal(0.0, actual: scorer.Score(result: Authoritative(false, analysis: null, judge: null), dimension: "d"));
    }

    [Fact]
    public void Score_NonAuthoritativeSyntax_KeepsFullConfiguredWeight()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.40, 0.0, 0.60));

        var heuristic = new QualityResult
        {
            SyntaxValid = true,
            SyntaxAuthoritative = false,
            SyntaxConfidence = 0.75,
            JudgeScore = 0.0
        };
        var authoritative = new QualityResult
        {
            SyntaxValid = true,
            SyntaxAuthoritative = true,
            SyntaxConfidence = 1.0,
            JudgeScore = 0.0
        };

        // Fixed weights: both are 0.40*1 + 0.60*0 = 0.40. Halving is gone.
        Assert.Equal(0.40, actual: scorer.Score(result: authoritative, dimension: "d"), 10);
        Assert.Equal(0.40, actual: scorer.Score(result: heuristic, dimension: "d"), 10);
    }

    [Fact]
    public void Score_CompleteBlend_EqualsPlainWeightedSum()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.25, 0.15, 0.60));

        var result = Authoritative(analysis: 0.5, judge: 0.5);
        // 0.25*1 + 0.15*0.5 + 0.60*0.5 = 0.25 + 0.075 + 0.30 = 0.625
        Assert.Equal(0.625, actual: scorer.Score(result: result, dimension: "d"), 10);
    }

    [Fact]
    public void Score_UnknownDimension_UsesJudgeHeavyDefault()
    {
        var scorer = CreateScorer();

        Assert.Equal(1.0,
            actual: scorer.Score(result: Authoritative(analysis: 1.0, judge: 1.0), dimension: "never_configured"));
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(5.0)]
    public void Score_OutOfRangeAxisValues_AreClampedIntoTheUnitInterval(double rogue)
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.25, 0.15, 0.60));

        var score = scorer.Score(result: Authoritative(analysis: rogue, judge: rogue), dimension: "d");

        Assert.InRange(actual: score, 0.0, 1.0);
    }

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

        Assert.Equal(0.4, actual: scorer.Score(result: result, dimension: "d"), 10);
    }

    [Fact]
    public void Score_ExtraGraderWithNoConfiguredWeight_DoesNotContribute()
    {
        var scorer = CreateScorer(WithWeights(dimension: "d", 0.4, 0.0, 0.0));

        var result = Authoritative(analysis: null, judge: null) with
        {
            GraderScores = new Dictionary<string, double> { ["unconfigured_grader"] = 0.0 }
        };

        Assert.Equal(0.4, actual: scorer.Score(result: result, dimension: "d"), 10);
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
                            SyntaxConfidence = authoritative ? 1.0 : 0.75,
                            AnalysisScore = analysis,
                            JudgeScore = judge
                        };

                        Assert.InRange(actual: scorer.Score(result: result, dimension: "code_generation"), 0.0, 1.0);
                    }
    }

    [Fact]
    public void DimensionWeights_Default_SumsToOne()
    {
        DimensionWeightOptions.Default.EnsureSumsToOne(dimensionLabel: "default");
    }

    [Fact]
    public void DimensionWeights_Misconfigured_FailsEnsureSumsToOne()
    {
        var weights = new DimensionWeightOptions { Syntax = 0.5, Analysis = 0.5, Judge = 0.5 };
        Assert.Throws<InvalidOperationException>(() => weights.EnsureSumsToOne(dimensionLabel: "bad"));
    }
}
