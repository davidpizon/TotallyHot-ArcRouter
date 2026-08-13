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

    // The Phase L access pattern: rows arrive in the dataset's spelling, but dim_best will look up by the
    // configured ModelName. Both sides run through ModelNameCanonicalizer, so the lookup hits.
    [Theory]
    [InlineData("claude-opus-4.6")]
    [InlineData("claude-opus-4-6")]
    [InlineData("Claude-Opus-4.6")]
    public void AverageScore_MatchesRows_AcrossModelIdSpellings(string queriedModel)
    {
        var matrix = DimensionModelScoreMatrix.FromRows([new("t1", "code_generation", "claude-opus-4-6", 1.0)]);

        Assert.Equal(1.0, matrix.AverageScore("code_generation", queriedModel));
    }

    // Rows whose ids differ only in spelling name one model, so they must average together rather than
    // occupy two cells.
    [Fact]
    public void FromRows_MergesRowsSpellingTheSameModelDifferently()
    {
        CodeRouterBenchResultRow[] rows =
        [
            new("t1", "code_generation", "MiniMax-M2.7", 1.0),
            new("t2", "code_generation", "minimax-m2.7", 0.0),
        ];

        var matrix = DimensionModelScoreMatrix.FromRows(rows);

        Assert.Equal(0.5, matrix.AverageScore("code_generation", "minimax-m2-7"));
    }

    [Fact]
    public void FromRows_EmptyInput_ProducesMatrixWithNoEntries()
    {
        var matrix = DimensionModelScoreMatrix.FromRows([]);

        Assert.Null(matrix.AverageScore("code_generation", "claude-opus-4-6"));
    }

    [Fact]
    public void FromDatabase_AveragesScores_FilteredToTheRequestedSplit()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger(); // schema only
        InsertResult(temp, "t1", "probing", "code_generation", "claude-opus-4-6", 1.0);
        InsertResult(temp, "t2", "probing", "code_generation", "claude-opus-4-6", 0.0);
        // A different split's row for the same pair must not leak into the probing-split average.
        InsertResult(temp, "t3", "id_test", "code_generation", "claude-opus-4-6", 1.0);

        var matrix = DimensionModelScoreMatrix.FromDatabase(temp.Database, "probing");

        Assert.Equal(0.5, matrix.AverageScore("code_generation", "claude-opus-4-6"));
    }

    [Fact]
    public void FromDatabase_CanonicalizesModelIds_OnRead()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger();
        InsertResult(temp, "t1", "probing", "code_generation", "MiniMax-M2.7", 1.0);

        var matrix = DimensionModelScoreMatrix.FromDatabase(temp.Database, "probing");

        Assert.Equal(1.0, matrix.AverageScore("code_generation", "minimax-m2.7"));
    }

    [Fact]
    public void FromDatabase_UnknownSplit_ProducesMatrixWithNoEntries()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger();
        InsertResult(temp, "t1", "probing", "code_generation", "claude-opus-4-6", 1.0);

        var matrix = DimensionModelScoreMatrix.FromDatabase(temp.Database, "id_test");

        Assert.Null(matrix.AverageScore("code_generation", "claude-opus-4-6"));
    }

    private static void InsertResult(TempBenchmarkDatabase temp, string taskId, string split, string dimension, string model, double score)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_id_results (task_id, split, source_split, dimension, model, score)
            VALUES ($taskId, $split, $split, $dimension, $model, $score);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$split", split);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$score", score);
        command.ExecuteNonQuery();
    }
}
