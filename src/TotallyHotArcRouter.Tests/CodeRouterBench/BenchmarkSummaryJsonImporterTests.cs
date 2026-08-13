using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkSummaryJsonImporter"/> against both accepted document shapes.</summary>
public class BenchmarkSummaryJsonImporterTests
{
    [Fact]
    public void Import_ObjectRoot_InsertsOneRowPerTopLevelProperty()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var json = """{ "totals": { "count": 9999 }, "notes": "generated" }""";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkSummaryJsonImporter.Import(json, connection, transaction);
        transaction.Commit();

        Assert.Equal(2, rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT raw_json FROM benchmark_summary WHERE key = 'totals';";
        Assert.Contains("9999", (string)readCommand.ExecuteScalar()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_ArrayRoot_StoresOneRowUnderTheWholeDocumentKey()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkSummaryJsonImporter.Import("[1,2,3]", connection, transaction);
        transaction.Commit();

        Assert.Equal(1, rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT key FROM benchmark_summary;";
        Assert.Equal(BenchmarkSummaryJsonImporter.WholeDocumentKey, (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Import_SecondCall_ReplacesEveryPriorRow()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Import(temp, """{ "a": 1, "b": 2 }""");
        Import(temp, """{ "c": 3 }""");

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_summary;";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }

    private static void Import(TempBenchmarkDatabase temp, string json)
    {
        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkSummaryJsonImporter.Import(json, connection, transaction);
        transaction.Commit();
    }
}
