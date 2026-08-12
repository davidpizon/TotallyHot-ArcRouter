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
    public void EnsureCreated_CreatesAllEightTables()
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
