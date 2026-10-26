using Microsoft.Extensions.Logging;
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

    [Fact]
    public void Calculate_SyntaxWeightZero_IgnoresLowSyntaxConfidence()
    {
        var bootstrap = new DimensionWeightOptions { Syntax = 0.0, Analysis = 0.0, Judge = 1.0 };

        Assert.Equal(GradeStatus.Complete,
            actual: GradeStatusCalculator.Calculate(
                result: BaseResult(analysis: null, syntaxConfidence: 0.1),
                weights: bootstrap,
                true));
    }

    [Fact]
    public void Calculate_IncompleteReason_IsIncomplete()
    {
        var result = BaseResult() with { IncompleteReason = IncompleteReasons.NoJudge };

        Assert.Equal(GradeStatus.Incomplete,
            actual: GradeStatusCalculator.Calculate(result: result, weights: Weights, false));
    }

    [Fact]
    public void Calculator_Case1_Through_Case4()
    {
        var log = new List<string>();
        var logger = new ListLogger(log);
        var case1 = BaseResult();
        Assert.Equal(GradeStatus.Complete, GradeStatusCalculator.Calculate(
            result: case1, weights: Weights, llmGraderWasAvailable: true, logger: logger,
            weightsVersion: "3.0-judge-heavy", graderKind: "code"));
        Assert.Contains(log, line => line.Contains("case 1", StringComparison.Ordinal));

        log.Clear();
        var case2 = BaseResult(judge: null) with { LocalScore = 0.4, AllowLocalGradeOfRecord = true, LocalGraderVersion = "abc" };
        Assert.Equal(GradeStatus.Complete, GradeStatusCalculator.Calculate(
            result: case2, weights: Weights, llmGraderWasAvailable: false, logger: logger,
            localScoreIsGradeOfRecord: true, weightsVersion: "3.0-judge-heavy", graderKind: "code"));
        Assert.Contains(log, line => line.Contains("case 2", StringComparison.Ordinal));

        log.Clear();
        var extra = new DimensionWeightOptions
        {
            Syntax = 0.25, Analysis = 0.15, Judge = 0.50,
            ExtraWeights = new Dictionary<string, double> { ["code_judge"] = 0.10 }
        };
        Assert.Equal(GradeStatus.Incomplete, GradeStatusCalculator.Calculate(
            result: case2, weights: extra, llmGraderWasAvailable: true, logger: logger,
            localScoreIsGradeOfRecord: true));

        log.Clear();
        var case3 = BaseResult(judge: null) with { LocalScore = 0.4, LocalGraderVersion = "abc" };
        Assert.Equal(GradeStatus.Incomplete, GradeStatusCalculator.Calculate(
            result: case3, weights: Weights, llmGraderWasAvailable: true, logger: logger,
            localScoreIsGradeOfRecord: false, weightsVersion: "3.0-judge-heavy", graderKind: "code"));
        Assert.Contains(log, line => line.Contains("case 3", StringComparison.Ordinal));

        log.Clear();
        Assert.Equal(GradeStatus.StructureOnly, GradeStatusCalculator.Calculate(
            result: BaseResult(judge: null), weights: Weights, llmGraderWasAvailable: false, logger: logger,
            weightsVersion: "3.0-judge-heavy", graderKind: "code"));
        Assert.Contains(log, line => line.Contains("case 4", StringComparison.Ordinal));
    }

    private sealed class ListLogger(List<string> lines) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
