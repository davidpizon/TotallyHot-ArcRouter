using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkIdTasksJsonlImporter"/> against small, hand-written fixture JSONL.</summary>
public class BenchmarkIdTasksJsonlImporterTests
{
    [Fact]
    public void Import_InsertsEveryLine_KeepingRawJsonVerbatim()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var jsonl = """{"task_id":"t1","dimension":"code_generation","prompt":"do the thing"}""" + "\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkIdTasksJsonlImporter.Import(reader: new StringReader(jsonl), split: "probing",
            connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(1, actual: rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT split, dimension, raw_json FROM benchmark_id_tasks WHERE task_id = 't1';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected: "probing", actual: reader.GetString(0));
        Assert.Equal(expected: RouterDimension.CodeGeneration, actual: reader.GetString(1));
        Assert.Contains(expectedSubstring: "do the thing", actualString: reader.GetString(2),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Import_ReadsSourceSplitProperty_NotSplitProperty()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        // "split" here is the probing/id_test discriminator (already captured by the split column, and
        // deliberately given a decoy value); "source_split" is the train/val/test distinction this column
        // must round-trip.
        var jsonl = """{"task_id":"t1","split":"probing","source_split":"train","dimension":"code_generation"}""" +
                    "\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdTasksJsonlImporter.Import(reader: new StringReader(jsonl), split: "probing", connection: connection,
            transaction: transaction);
        transaction.Commit();

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal(expected: "train", actual: (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Import_MissingSourceSplitProperty_FallsBackToSplitArgument()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var jsonl = """{"task_id":"t1","dimension":"code_generation"}""" + "\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdTasksJsonlImporter.Import(reader: new StringReader(jsonl), split: "id_test", connection: connection,
            transaction: transaction);
        transaction.Commit();

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal(expected: "id_test", actual: (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Import_SecondCallForSameSplit_ReplacesPriorRows()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Import(temp: temp, jsonl: """{"task_id":"t1","dimension":"code_generation"}""" + "\n", split: "probing");
        Import(temp: temp, jsonl: """{"task_id":"t2","dimension":"code_generation"}""" + "\n", split: "probing");

        Assert.Equal(1, actual: CountRows(temp: temp, split: "probing"));
    }

    [Fact]
    public void Import_MissingTaskId_ThrowsFormatException()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var ex = Assert.Throws<FormatException>(() =>
            BenchmarkIdTasksJsonlImporter.Import(
                reader: new StringReader("""{"dimension":"code_generation"}""" + "\n"), split: "probing",
                connection: connection, transaction: transaction));
        Assert.Contains(expectedSubstring: "task_id", actualString: ex.Message,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_SkipsBlankLines()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var jsonl = """{"task_id":"t1","dimension":"code_generation"}""" + "\n\n" +
                    """{"task_id":"t2","dimension":"code_generation"}""" + "\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkIdTasksJsonlImporter.Import(reader: new StringReader(jsonl), split: "probing",
            connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(2, actual: rowCount);
    }

    private static void Import(TempBenchmarkDatabase temp, string jsonl, string split)
    {
        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdTasksJsonlImporter.Import(reader: new StringReader(jsonl), split: split, connection: connection,
            transaction: transaction);
        transaction.Commit();
    }

    private static int CountRows(TempBenchmarkDatabase temp, string split)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_id_tasks WHERE split = $split;";
        command.Parameters.AddWithValue(parameterName: "$split", value: split);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}