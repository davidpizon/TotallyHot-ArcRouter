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

        Assert.Contains(expected: "benchmark_files", collection: tables);
        Assert.Contains(expected: "benchmark_id_results", collection: tables);
        Assert.Contains(expected: "benchmark_ood_results", collection: tables);
        Assert.Contains(expected: "benchmark_id_tasks", collection: tables);
        Assert.Contains(expected: "benchmark_ood_tasks", collection: tables);
        Assert.Contains(expected: "benchmark_models", collection: tables);
        Assert.Contains(expected: "benchmark_summary", collection: tables);
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
        while (reader.Read()) indexes.Add(reader.GetString(0));

        Assert.Contains(expected: "idx_benchmark_id_results_dimension_model", collection: indexes);
        Assert.Contains(expected: "idx_benchmark_id_results_split", collection: indexes);
        Assert.Contains(expected: "idx_benchmark_id_results_task_id", collection: indexes);
        Assert.Contains(expected: "idx_benchmark_ood_results_task_id", collection: indexes);
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
        InsertGarbageModelsRow(database: temp.Database, arrayJson: arrayJson);

        // Re-running EnsureCreated is the only trigger a real startup path has - no network call is made.
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM benchmark_models WHERE model = 'models';";
        Assert.Equal(0L, actual: (long)countCommand.ExecuteScalar()!);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText =
            "SELECT provider, input_per_1m FROM benchmark_models WHERE model = 'claude-opus-4-6';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected: "anthropic", actual: reader.GetString(0));
        Assert.Equal(15.0, actual: reader.GetDouble(1));
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
        Assert.Equal(1L, actual: (long)countCommand.ExecuteScalar()!);
    }

    [Fact]
    public void EnsureCreated_IdTasksSourceSplitHoldsSplitValue_RepairsFromRawJsonInPlace()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertIdTaskRow(
            database: temp.Database,
            taskId: "t1",
            split: "probing",
            badSourceSplit: "probing",
            """{"task_id":"t1","split":"probing","source_split":"train","dimension":"code_generation"}""");

        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal(expected: "train", actual: (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void EnsureCreated_IdTasksRawJsonHasNoSourceSplitProperty_LeavesRowUntouched()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertIdTaskRow(
            database: temp.Database,
            taskId: "t1",
            split: "probing",
            badSourceSplit: "probing",
            """{"task_id":"t1","dimension":"code_generation"}""");

        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal(expected: "probing", actual: (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void EnsureCreated_IdTasksSourceSplitAlreadyValid_IsNoOp()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertIdTaskRow(
            database: temp.Database,
            taskId: "t1",
            split: "probing",
            badSourceSplit: "train",
            """{"task_id":"t1","split":"probing","source_split":"val","dimension":"code_generation"}""");

        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal(expected: "train", actual: (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void EnsureCreated_IdTasksSourceSplitInRawJsonIsInvalidValue_LeavesRowUntouched()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        InsertIdTaskRow(
            database: temp.Database,
            taskId: "t1",
            split: "probing",
            badSourceSplit: "probing",
            """{"task_id":"t1","split":"probing","source_split":"not_a_real_split","dimension":"code_generation"}""");

        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT source_split FROM benchmark_id_tasks WHERE task_id = 't1';";
        Assert.Equal(expected: "probing", actual: (string)readCommand.ExecuteScalar()!);
    }

    private static void InsertGarbageModelsRow(BenchmarkDatabase database, string arrayJson)
    {
        using var connection = database.OpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO benchmark_models (model, canonical_key, raw_json) VALUES ('models', 'models', $rawJson);";
        insert.Parameters.AddWithValue(parameterName: "$rawJson", value: arrayJson);
        insert.ExecuteNonQuery();
    }

    private static void InsertIdTaskRow(BenchmarkDatabase database, string taskId, string split, string badSourceSplit,
        string rawJson)
    {
        using var connection = database.OpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
                             INSERT INTO benchmark_id_tasks (task_id, split, source_split, dimension, raw_json)
                             VALUES ($taskId, $split, $sourceSplit, 'code_generation', $rawJson);
                             """;
        insert.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        insert.Parameters.AddWithValue(parameterName: "$split", value: split);
        insert.Parameters.AddWithValue(parameterName: "$sourceSplit", value: badSourceSplit);
        insert.Parameters.AddWithValue(parameterName: "$rawJson", value: rawJson);
        insert.ExecuteNonQuery();
    }

    private static List<string> ReadTableNames(BenchmarkDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        List<string> tables = [];
        using var reader = command.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));

        return tables;
    }
}