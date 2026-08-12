using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Unit tests for <see cref="DimensionModelScoreMatrix"/>.</summary>
public class DimensionModelScoreMatrixTests
{
    [Fact]
    public void FromRows_AveragesScores_PerDimensionModelPair()
    {
        CodeRouterBenchResultRow[] rows =
        [
            new("t1", "code_generation", "claude-opus-4-6", 1.0),
            new("t2", "code_generation", "claude-opus-4-6", 0.0),
            new("t3", "bug_fixing", "claude-opus-4-6", 0.5),
        ];

        var matrix = DimensionModelScoreMatrix.FromRows(rows);

        Assert.Equal(0.5, matrix.AverageScore("code_generation", "claude-opus-4-6"));
        Assert.Equal(0.5, matrix.AverageScore("bug_fixing", "claude-opus-4-6"));
    }

    [Fact]
    public void FromRows_KeepsDifferentModelsIndependent_ForTheSameDimension()
    {
        CodeRouterBenchResultRow[] rows =
        [
            new("t1", "code_generation", "claude-opus-4-6", 1.0),
            new("t1", "code_generation", "glm-5", 0.0),
        ];

        var matrix = DimensionModelScoreMatrix.FromRows(rows);

        Assert.Equal(1.0, matrix.AverageScore("code_generation", "claude-opus-4-6"));
        Assert.Equal(0.0, matrix.AverageScore("code_generation", "glm-5"));
    }

    [Fact]
    public void AverageScore_UnknownPair_ReturnsNull()
    {
        var matrix = DimensionModelScoreMatrix.FromRows([new("t1", "code_generation", "claude-opus-4-6", 1.0)]);

        Assert.Null(matrix.AverageScore("bug_fixing", "claude-opus-4-6"));
        Assert.Null(matrix.AverageScore("code_generation", "unknown-model"));
    }

    [Fact]
    public void FromRows_EmptyInput_ProducesMatrixWithNoEntries()
    {
        var matrix = DimensionModelScoreMatrix.FromRows([]);

        Assert.Null(matrix.AverageScore("code_generation", "claude-opus-4-6"));
    }
}
