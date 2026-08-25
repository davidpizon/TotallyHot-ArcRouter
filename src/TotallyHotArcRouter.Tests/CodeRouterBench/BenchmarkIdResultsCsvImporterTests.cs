using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkIdResultsCsvImporter"/> against small, hand-written fixture CSVs.</summary>
public class BenchmarkIdResultsCsvImporterTests
{
    [Fact]
    public void Import_InsertsEveryRow_WithTheGivenSplit()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var csv = "task_id,dimension,model,score,cost_usd\n" +
                   "t1,code_generation,claude-opus-4-6,1.0,0.001\n" +
                   "t2,bug_fixing,claude-opus-4-6,0.5,0.002\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkIdResultsCsvImporter.Import(new StringReader(csv), "probing", connection, transaction);
        transaction.Commit();

        Assert.Equal(2, rowCount);
        Assert.Equal(2, CountRows(temp, "probing"));
    }

    [Fact]
    public void Import_NormalizesAlgorithmDimension_AndCanonicalizesModel()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var csv = "task_id,dimension,model,score\nt1,algorithm,MiniMax-M2.7,1.0\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdResultsCsvImporter.Import(new StringReader(csv), "probing", connection, transaction);
        transaction.Commit();

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT dimension, model FROM benchmark_id_results WHERE task_id = 't1';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(RouterDimension.AlgorithmDesign, reader.GetString(0));
        Assert.Equal("minimax-m2-7", reader.GetString(1));
    }

    [Fact]
    public void Import_SecondCallForSameSplit_ReplacesPriorRowsRatherThanAppending()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        ImportCsv(temp, "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\n", "probing");
        ImportCsv(temp, "task_id,dimension,model,score\nt2,code_generation,claude-opus-4-6,0.0\n", "probing");

        Assert.Equal(1, CountRows(temp, "probing"));
    }

    [Fact]
    public void Import_DoesNotTouchRowsFromADifferentSplit()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        ImportCsv(temp, "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\n", "probing");
        ImportCsv(temp, "task_id,dimension,model,score\nt2,code_generation,claude-opus-4-6,0.0\n", "id_test");

        Assert.Equal(1, CountRows(temp, "probing"));
        Assert.Equal(1, CountRows(temp, "id_test"));
    }

    [Fact]
    public void Import_MissingRequiredColumn_ThrowsFormatException()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var ex = Assert.Throws<FormatException>(() =>
            BenchmarkIdResultsCsvImporter.Import(new StringReader("task_id,model,score\nt1,m,1.0\n"), "probing", connection, transaction));
        Assert.Contains("dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_MissingOptionalColumns_LeavesThemNull()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        ImportCsv(temp, "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\n", "probing");

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT cost_usd, input_tokens FROM benchmark_id_results WHERE task_id = 't1';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    private static void ImportCsv(TempBenchmarkDatabase temp, string csv, string split)
    {
        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdResultsCsvImporter.Import(new StringReader(csv), split, connection, transaction);
        transaction.Commit();
    }

    private static int CountRows(TempBenchmarkDatabase temp, string split)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_id_results WHERE split = $split;";
        command.Parameters.AddWithValue("$split", split);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
