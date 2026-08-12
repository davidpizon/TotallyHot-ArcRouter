using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Sandbox;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Unit tests for <see cref="CodeRouterBenchCsvReader"/> against small, hand-written fixture CSVs.</summary>
public class CodeRouterBenchCsvReaderTests : IDisposable
{
    private readonly List<string> _fixturePaths = [];

    public void Dispose()
    {
        foreach (var path in _fixturePaths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Read_ParsesRowsByHeaderColumnOrder_IgnoringExtraColumns()
    {
        var path = WriteFixture(
            "cost_usd,task_id,dimension,model,score",
            "0.001,t1,code_generation,claude-opus-4-6,1.0",
            "0.002,t2,bug_fixing,claude-opus-4-6,0.0");

        var rows = CodeRouterBenchCsvReader.Read(path);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new CodeRouterBenchResultRow("t1", "code_generation", "claude-opus-4-6", 1.0), rows[0]);
        Assert.Equal(new CodeRouterBenchResultRow("t2", "bug_fixing", "claude-opus-4-6", 0.0), rows[1]);
    }

    // The released tables spell several models differently from the router's own configuration, so the
    // reader hands back the canonical comparison key rather than the CSV's own casing/separators.
    [Theory]
    [InlineData("MiniMax-M2.7", "minimax-m2-7")]
    [InlineData("Qwen3-Max", "qwen3-max")]
    [InlineData("claude-opus-4.6", "claude-opus-4-6")]
    public void Read_CanonicalizesModelIds_ToTheRouterComparisonKey(string rawModel, string expected)
    {
        var path = WriteFixture(
            "task_id,dimension,model,score",
            $"t1,code_generation,{rawModel},1.0");

        var rows = CodeRouterBenchCsvReader.Read(path);

        Assert.Equal(expected, rows[0].Model);
    }

    [Fact]
    public void Read_RowWithEmptyModel_ThrowsFormatException()
    {
        var path = WriteFixture(
            "task_id,dimension,model,score",
            "t1,code_generation,,1.0");

        var ex = Assert.Throws<FormatException>(() => CodeRouterBenchCsvReader.Read(path));
        Assert.Contains("row 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_NormalizesAlgorithmDimension_ToRouterDimensionVocabulary()
    {
        var path = WriteFixture(
            "task_id,dimension,model,score",
            "t1,algorithm,glm-5,0.5");

        var rows = CodeRouterBenchCsvReader.Read(path);

        Assert.Equal(RouterDimension.AlgorithmDesign, rows[0].Dimension);
    }

    [Fact]
    public void Read_LeavesAlreadyCanonicalDimensions_Unchanged()
    {
        var path = WriteFixture(
            "task_id,dimension,model,score",
            "t1,data_science,glm-5,0.5");

        var rows = CodeRouterBenchCsvReader.Read(path);

        Assert.Equal(RouterDimension.DataScience, rows[0].Dimension);
    }

    [Fact]
    public void Read_HandlesQuotedFieldsContainingCommas()
    {
        var path = WriteFixture(
            "task_id,dimension,model,score,cost_source",
            "t1,code_generation,claude-opus-4-6,1.0,\"token_log_pricing, fallback\"");

        var rows = CodeRouterBenchCsvReader.Read(path);

        Assert.Equal("t1", rows[0].TaskId);
    }

    [Fact]
    public void Read_SkipsBlankLines()
    {
        var path = WriteFixture(
            "task_id,dimension,model,score",
            "t1,code_generation,claude-opus-4-6,1.0",
            "",
            "t2,code_generation,claude-opus-4-6,0.0");

        var rows = CodeRouterBenchCsvReader.Read(path);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Read_MissingRequiredColumn_ThrowsFormatException()
    {
        var path = WriteFixture(
            "task_id,model,score",
            "t1,claude-opus-4-6,1.0");

        var ex = Assert.Throws<FormatException>(() => CodeRouterBenchCsvReader.Read(path));
        Assert.Contains("dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RowMissingRequiredField_ThrowsFormatException()
    {
        var path = WriteFixture(
            "task_id,dimension,model,score",
            "t1,code_generation,claude-opus-4-6");

        var ex = Assert.Throws<FormatException>(() => CodeRouterBenchCsvReader.Read(path));
        Assert.Contains("row 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_MissingHeaderRow_ThrowsFormatException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crb-empty-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, string.Empty);

        try
        {
            Assert.Throws<FormatException>(() => CodeRouterBenchCsvReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private string WriteFixture(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"crb-fixture-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        _fixturePaths.Add(path);
        return path;
    }
}
