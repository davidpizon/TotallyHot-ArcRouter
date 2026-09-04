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
        var rowCount = BenchmarkIdResultsCsvImporter.Import(reader: new StringReader(csv), split: "probing",
            connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(2, actual: rowCount);
        Assert.Equal(2, actual: CountRows(temp: temp, split: "probing"));
    }

    [Fact]
    public void Import_NormalizesAlgorithmDimension_AndCanonicalizesModel()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var csv = "task_id,dimension,model,score\nt1,algorithm,MiniMax-M2.7,1.0\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdResultsCsvImporter.Import(reader: new StringReader(csv), split: "probing", connection: connection,
            transaction: transaction);
        transaction.Commit();

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT dimension, model FROM benchmark_id_results WHERE task_id = 't1';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected: RouterDimension.AlgorithmDesign, actual: reader.GetString(0));
        Assert.Equal(expected: "minimax-m2-7", actual: reader.GetString(1));
    }

    [Fact]
    public void Import_SecondCallForSameSplit_ReplacesPriorRowsRatherThanAppending()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        ImportCsv(temp: temp, csv: "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\n",
            split: "probing");
        ImportCsv(temp: temp, csv: "task_id,dimension,model,score\nt2,code_generation,claude-opus-4-6,0.0\n",
            split: "probing");

        Assert.Equal(1, actual: CountRows(temp: temp, split: "probing"));
    }

    [Fact]
    public void Import_DoesNotTouchRowsFromADifferentSplit()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        ImportCsv(temp: temp, csv: "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\n",
            split: "probing");
        ImportCsv(temp: temp, csv: "task_id,dimension,model,score\nt2,code_generation,claude-opus-4-6,0.0\n",
            split: "id_test");

        Assert.Equal(1, actual: CountRows(temp: temp, split: "probing"));
        Assert.Equal(1, actual: CountRows(temp: temp, split: "id_test"));
    }

    [Fact]
    public void Import_MissingRequiredColumn_ThrowsFormatException()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var ex = Assert.Throws<FormatException>(() =>
            BenchmarkIdResultsCsvImporter.Import(reader: new StringReader("task_id,model,score\nt1,m,1.0\n"),
                split: "probing", connection: connection, transaction: transaction));
        Assert.Contains(expectedSubstring: "dimension", actualString: ex.Message,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_MissingOptionalColumns_LeavesThemNull()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        ImportCsv(temp: temp, csv: "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\n",
            split: "probing");

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
        BenchmarkIdResultsCsvImporter.Import(reader: new StringReader(csv), split: split, connection: connection,
            transaction: transaction);
        transaction.Commit();
    }

    private static int CountRows(TempBenchmarkDatabase temp, string split)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_id_results WHERE split = $split;";
        command.Parameters.AddWithValue(parameterName: "$split", value: split);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}