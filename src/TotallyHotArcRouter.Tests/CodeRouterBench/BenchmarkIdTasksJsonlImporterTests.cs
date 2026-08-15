using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Sandbox;

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
        var rowCount = BenchmarkIdTasksJsonlImporter.Import(new StringReader(jsonl), "probing", connection, transaction);
        transaction.Commit();

        Assert.Equal(1, rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT split, dimension, raw_json FROM benchmark_id_tasks WHERE task_id = 't1';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("probing", reader.GetString(0));
        Assert.Equal(RouterDimension.CodeGeneration, reader.GetString(1));
        Assert.Contains("do the thing", reader.GetString(2), StringComparison.Ordinal);
    }

    [Fact]
    public void Import_ReadsSourceSplitProperty_NotSplitProperty()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        // "split" here is the probing/id_test discriminator (already captured by the split column, and
        // deliberately given a decoy value); "source_split" is the train/val/test distinction this column
        // must round-trip.
        var jsonl = """{"task_id":"t1","split":"probing","source_split":"train","dimension":"code_generation"}""" + "\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdTasksJsonlImporter.Import(new StringReader(jsonl), "probing", connection, transaction);
        transaction.Commit();

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal("train", (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Import_MissingSourceSplitProperty_FallsBackToSplitArgument()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var jsonl = """{"task_id":"t1","dimension":"code_generation"}""" + "\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdTasksJsonlImporter.Import(new StringReader(jsonl), "id_test", connection, transaction);
        transaction.Commit();

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal("id_test", (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Import_SecondCallForSameSplit_ReplacesPriorRows()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Import(temp, """{"task_id":"t1","dimension":"code_generation"}""" + "\n", "probing");
        Import(temp, """{"task_id":"t2","dimension":"code_generation"}""" + "\n", "probing");

        Assert.Equal(1, CountRows(temp, "probing"));
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
                new StringReader("""{"dimension":"code_generation"}""" + "\n"), "probing", connection, transaction));
        Assert.Contains("task_id", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        var rowCount = BenchmarkIdTasksJsonlImporter.Import(new StringReader(jsonl), "probing", connection, transaction);
        transaction.Commit();

        Assert.Equal(2, rowCount);
    }

    private static void Import(TempBenchmarkDatabase temp, string jsonl, string split)
    {
        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkIdTasksJsonlImporter.Import(new StringReader(jsonl), split, connection, transaction);
        transaction.Commit();
    }

    private static int CountRows(TempBenchmarkDatabase temp, string split)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_id_tasks WHERE split = $split;";
        command.Parameters.AddWithValue("$split", split);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
