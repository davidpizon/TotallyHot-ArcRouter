using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkDatabase.EnsureCreated"/>.</summary>
public class BenchmarkDatabaseTests
{
    [Fact]
    public void EnsureCreated_FirstCall_CreatesFileAndReportsItDidNotExist()
    {
        using var temp = new TempBenchmarkDatabase();

        var alreadyExisted = temp.Database.EnsureCreated();

        Assert.False(alreadyExisted);
        Assert.True(File.Exists(temp.Path_));
    }

    [Fact]
    public void EnsureCreated_SecondCall_IsNoOpAndReportsItAlreadyExisted()
    {
        using var temp = new TempBenchmarkDatabase();

        temp.Database.EnsureCreated();
        var alreadyExisted = temp.Database.EnsureCreated();

        Assert.True(alreadyExisted);
    }

    [Fact]
    public void EnsureCreated_CreatesAllSevenTables()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var tables = ReadTableNames(temp.Database);

        Assert.Contains("benchmark_files", tables);
        Assert.Contains("benchmark_id_results", tables);
        Assert.Contains("benchmark_ood_results", tables);
        Assert.Contains("benchmark_id_tasks", tables);
        Assert.Contains("benchmark_ood_tasks", tables);
        Assert.Contains("benchmark_models", tables);
        Assert.Contains("benchmark_summary", tables);
    }

    [Fact]
    public void EnsureCreated_CreatesTheDocumentedIndexes()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";

        List<string> indexes = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            indexes.Add(reader.GetString(0));
        }

        Assert.Contains("idx_benchmark_id_results_dimension_model", indexes);
        Assert.Contains("idx_benchmark_id_results_split", indexes);
        Assert.Contains("idx_benchmark_id_results_task_id", indexes);
        Assert.Contains("idx_benchmark_ood_results_task_id", indexes);
    }

    [Fact]
    public void EnsureCreated_ModelsEnvelopeGarbageRowPresent_RepairsInPlaceWithoutNetworkAccess()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var arrayJson = """
            [
              { "model": "claude-opus-4-6", "provider": "anthropic", "input_per_1m": 15.0 },
              { "model": "gpt-6", "provider": "openai" }
            ]
            """;
        InsertGarbageModelsRow(temp.Database, arrayJson);

        // Re-running EnsureCreated is the only trigger a real startup path has - no network call is made.
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM benchmark_models WHERE model = 'models';";
        Assert.Equal(0L, (long)countCommand.ExecuteScalar()!);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT provider, input_per_1m FROM benchmark_models WHERE model = 'claude-opus-4-6';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("anthropic", reader.GetString(0));
        Assert.Equal(15.0, reader.GetDouble(1));
    }

    [Fact]
    public void EnsureCreated_NoModelsEnvelopeGarbageRow_IsNoOp()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using (var connection = temp.Database.OpenConnection())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO benchmark_models (model, canonical_key, raw_json) VALUES ('claude-opus-4-6', 'claude-opus-4-6', '{}');";
            insert.ExecuteNonQuery();
        }

        temp.Database.EnsureCreated();

        using var readConnection = temp.Database.OpenConnection();
        using var countCommand = readConnection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM benchmark_models;";
        Assert.Equal(1L, (long)countCommand.ExecuteScalar()!);
    }

    [Fact]
    public void EnsureCreated_IdTasksSourceSplitHoldsSplitValue_RepairsFromRawJsonInPlace()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertIdTaskRow(
            temp.Database,
            taskId: "t1",
            split: "probing",
            badSourceSplit: "probing",
            rawJson: """{"task_id":"t1","split":"probing","source_split":"train","dimension":"code_generation"}""");

        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal("train", (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void EnsureCreated_IdTasksRawJsonHasNoSourceSplitProperty_LeavesRowUntouched()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertIdTaskRow(
            temp.Database,
            taskId: "t1",
            split: "probing",
            badSourceSplit: "probing",
            rawJson: """{"task_id":"t1","dimension":"code_generation"}""");

        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal("probing", (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void EnsureCreated_IdTasksSourceSplitAlreadyValid_IsNoOp()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertIdTaskRow(
            temp.Database,
            taskId: "t1",
            split: "probing",
            badSourceSplit: "train",
            rawJson: """{"task_id":"t1","split":"probing","source_split":"val","dimension":"code_generation"}""");

        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal("train", (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void EnsureCreated_IdTasksSourceSplitInRawJsonIsInvalidValue_LeavesRowUntouched()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertIdTaskRow(
            temp.Database,
            taskId: "t1",
            split: "probing",
            badSourceSplit: "probing",
            rawJson: """{"task_id":"t1","split":"probing","source_split":"not_a_real_split","dimension":"code_generation"}""");

        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal("probing", (string)readCommand.ExecuteScalar()!);
    }

    private static void InsertGarbageModelsRow(BenchmarkDatabase database, string arrayJson)
    {
        using var connection = database.OpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO benchmark_models (model, canonical_key, raw_json) VALUES ('models', 'models', $rawJson);";
        insert.Parameters.AddWithValue("$rawJson", arrayJson);
        insert.ExecuteNonQuery();
    }

    private static void InsertIdTaskRow(BenchmarkDatabase database, string taskId, string split, string badSourceSplit, string rawJson)
    {
        using var connection = database.OpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO benchmark_id_tasks (task_id, split, source_split, dimension, raw_json)
            VALUES ($taskId, $split, $sourceSplit, 'code_generation', $rawJson);
            """;
        insert.Parameters.AddWithValue("$taskId", taskId);
        insert.Parameters.AddWithValue("$split", split);
        insert.Parameters.AddWithValue("$sourceSplit", badSourceSplit);
        insert.Parameters.AddWithValue("$rawJson", rawJson);
        insert.ExecuteNonQuery();
    }

    private static List<string> ReadTableNames(BenchmarkDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        List<string> tables = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
