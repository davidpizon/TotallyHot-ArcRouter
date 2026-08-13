using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkOodResultsCsvImporter"/> against small, hand-written fixture CSVs.</summary>
public class BenchmarkOodResultsCsvImporterTests
{
    private const string Header = "task_id,source_split,bench,dimension,model,resolved,apply_ok,graded,cost_usd";

    [Fact]
    public void Import_InsertsEveryRow_AndParsesBooleanFlags()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var csv = $"{Header}\nt1,ood,swebench,code_generation,claude-opus-4-6,1,0,true,0.01\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkOodResultsCsvImporter.Import(new StringReader(csv), connection, transaction);
        transaction.Commit();

        Assert.Equal(1, rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT resolved, apply_ok, graded, model FROM benchmark_ood_results WHERE task_id = 't1';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal("claude-opus-4-6", reader.GetString(3));
    }

    [Fact]
    public void Import_SecondCall_ReplacesEveryPriorRow()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Import(temp, $"{Header}\nt1,ood,swebench,code_generation,claude-opus-4-6,1,1,1,0.01\n");
        Import(temp, $"{Header}\nt2,ood,swebench,code_generation,claude-opus-4-6,1,1,1,0.01\n");

        Assert.Equal(1, CountRows(temp));
    }

    [Fact]
    public void Import_MissingRequiredColumn_ThrowsFormatException()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var ex = Assert.Throws<FormatException>(() =>
            BenchmarkOodResultsCsvImporter.Import(
                new StringReader("task_id,bench,dimension,model\nt1,swebench,code_generation,m\n"), connection, transaction));
        Assert.Contains("source_split", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void Import(TempBenchmarkDatabase temp, string csv)
    {
        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkOodResultsCsvImporter.Import(new StringReader(csv), connection, transaction);
        transaction.Commit();
    }

    private static int CountRows(TempBenchmarkDatabase temp)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_ood_results;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
