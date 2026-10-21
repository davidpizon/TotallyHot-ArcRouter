using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers <see cref="GradeStatusCalculator"/> status mapping for issue #293.</summary>
public class GradeStatusCalculatorTests
{
    private static readonly DimensionWeightOptions Weights = new()
    {
        Syntax = 0.25,
        Analysis = 0.15,
        Judge = 0.60
    };

    private static QualityResult BaseResult(
        double? analysis = 1.0,
        double? judge = 1.0,
        double syntaxConfidence = 1.0,
        bool questionPresent = true,
        bool syntaxValid = true) =>
        new()
        {
            SyntaxValid = syntaxValid,
            SyntaxAuthoritative = syntaxConfidence >= 1.0,
            SyntaxConfidence = syntaxConfidence,
            QuestionPresent = questionPresent,
            AnalysisScore = analysis,
            JudgeScore = judge
        };

    [Fact]
    public void Calculate_AllAxesPresent_IsComplete()
    {
        Assert.Equal(GradeStatus.Complete,
            actual: GradeStatusCalculator.Calculate(result: BaseResult(), weights: Weights, true));
    }

    [Fact]
    public void Calculate_JudgeAbsentAndUnavailable_IsStructureOnly()
    {
        Assert.Equal(GradeStatus.StructureOnly,
            actual: GradeStatusCalculator.Calculate(result: BaseResult(judge: null), weights: Weights, false));
    }

    [Fact]
    public void Calculate_JudgeAbsentButWasAvailable_IsIncomplete()
    {
        Assert.Equal(GradeStatus.Incomplete,
            actual: GradeStatusCalculator.Calculate(result: BaseResult(judge: null), weights: Weights, true));
    }

    [Fact]
    public void Calculate_QuestionMissing_IsIncomplete()
    {
        Assert.Equal(GradeStatus.Incomplete,
            actual: GradeStatusCalculator.Calculate(
                result: BaseResult(questionPresent: false), weights: Weights, true));
    }

    [Fact]
    public void Calculate_LowSyntaxConfidence_IsIncomplete()
    {
        Assert.Equal(GradeStatus.Incomplete,
            actual: GradeStatusCalculator.Calculate(
                result: BaseResult(syntaxConfidence: 0.55), weights: Weights, true));
    }

    [Fact]
    public void Calculate_LanguageAwareHeuristicAtThreshold_CanBeComplete()
    {
        Assert.Equal(GradeStatus.Complete,
            actual: GradeStatusCalculator.Calculate(
                result: BaseResult(syntaxConfidence: 0.75), weights: Weights, true));
    }

    [Fact]
    public void Calculate_MissingAnalysisWithWeight_IsIncomplete()
    {
        Assert.Equal(GradeStatus.Incomplete,
            actual: GradeStatusCalculator.Calculate(
                result: BaseResult(analysis: null), weights: Weights, false));
    }
}
