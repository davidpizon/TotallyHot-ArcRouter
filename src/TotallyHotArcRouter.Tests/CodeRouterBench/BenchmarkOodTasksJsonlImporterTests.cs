using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkOodTasksJsonlImporter"/> against small, hand-written fixture JSONL.</summary>
public class BenchmarkOodTasksJsonlImporterTests
{
    [Fact]
    public void Import_InsertsEveryLine_WithOptionalFieldsWhenPresent()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var jsonl =
            """{"task_id":"t1","bench":"swebench","dimension":"code_generation","language":"python","difficulty":"hard"}""" +
            "\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkOodTasksJsonlImporter.Import(reader: new StringReader(jsonl), connection: connection,
            transaction: transaction);
        transaction.Commit();

        Assert.Equal(1, actual: rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT bench, language, difficulty FROM benchmark_ood_tasks WHERE task_id = 't1';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected: "swebench", actual: reader.GetString(0));
        Assert.Equal(expected: "python", actual: reader.GetString(1));
        Assert.Equal(expected: "hard", actual: reader.GetString(2));
    }

    [Fact]
    public void Import_MissingOptionalFields_LeavesThemNull()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var jsonl = """{"task_id":"t1","bench":"swebench","dimension":"code_generation"}""" + "\n";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkOodTasksJsonlImporter.Import(reader: new StringReader(jsonl), connection: connection,
            transaction: transaction);
        transaction.Commit();

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT language, difficulty FROM benchmark_ood_tasks WHERE task_id = 't1';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public void Import_SecondCall_ReplacesEveryPriorRow()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Import(temp: temp, jsonl: """{"task_id":"t1","bench":"swebench","dimension":"code_generation"}""" + "\n");
        Import(temp: temp, jsonl: """{"task_id":"t2","bench":"swebench","dimension":"code_generation"}""" + "\n");

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_ood_tasks;";
        Assert.Equal(1, actual: Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void Import_MissingBench_ThrowsFormatException()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var ex = Assert.Throws<FormatException>(() =>
            BenchmarkOodTasksJsonlImporter.Import(
                reader: new StringReader("""{"task_id":"t1","dimension":"code_generation"}""" + "\n"),
                connection: connection, transaction: transaction));
        Assert.Contains(expectedSubstring: "bench", actualString: ex.Message,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    private static void Import(TempBenchmarkDatabase temp, string jsonl)
    {
        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkOodTasksJsonlImporter.Import(reader: new StringReader(jsonl), connection: connection,
            transaction: transaction);
        transaction.Commit();
    }
}