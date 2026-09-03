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
        var rowCount =
            BenchmarkSummaryJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(2, actual: rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT raw_json FROM benchmark_summary WHERE key = 'totals';";
        Assert.Contains(expectedSubstring: "9999", actualString: (string)readCommand.ExecuteScalar()!,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Import_ArrayRoot_StoresOneRowUnderTheWholeDocumentKey()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount =
            BenchmarkSummaryJsonImporter.Import(json: "[1,2,3]", connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(1, actual: rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT key FROM benchmark_summary;";
        Assert.Equal(expected: BenchmarkSummaryJsonImporter.WholeDocumentKey,
            actual: (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Import_SecondCall_ReplacesEveryPriorRow()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Import(temp: temp, """{ "a": 1, "b": 2 }""");
        Import(temp: temp, """{ "c": 3 }""");

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_summary;";
        Assert.Equal(1, actual: Convert.ToInt32(command.ExecuteScalar()));
    }

    private static void Import(TempBenchmarkDatabase temp, string json)
    {
        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkSummaryJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();
    }
}