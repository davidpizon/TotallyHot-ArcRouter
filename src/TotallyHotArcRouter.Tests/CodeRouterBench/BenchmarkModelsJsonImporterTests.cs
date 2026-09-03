using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkModelsJsonImporter"/> against both accepted JSON shapes.</summary>
public class BenchmarkModelsJsonImporterTests
{
    [Fact]
    public void Import_ObjectShape_InsertsOneRowPerProperty()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var json = """
                   {
                     "MiniMax-M2.7": { "provider": "minimax", "tier": "flagship", "input_per_1m": 3.0, "output_per_1m": 12.0 }
                   }
                   """;

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkModelsJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(1, actual: rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText =
            "SELECT canonical_key, provider, input_per_1m FROM benchmark_models WHERE model = 'MiniMax-M2.7';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected: "minimax-m2-7", actual: reader.GetString(0));
        Assert.Equal(expected: "minimax", actual: reader.GetString(1));
        Assert.Equal(3.0, actual: reader.GetDouble(2));
    }

    [Fact]
    public void Import_ArrayShape_ReadsIdFromModelOrIdProperty()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var json = """[ { "model": "claude-opus-4-6" }, { "id": "glm-5" } ]""";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkModelsJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(2, actual: rowCount);
    }

    [Fact]
    public void Import_ObjectWrappedInModelsEnvelope_UnwrapsAndInsertsOneRowPerModel()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var json = """
                   {
                     "models": [
                       { "model": "claude-opus-4-6", "provider": "anthropic", "tier": "flagship", "input_per_1m": 15.0, "output_per_1m": 75.0 },
                       { "model": "gpt-6", "provider": "openai" },
                       { "model": "gemini-3-pro", "provider": "google" },
                       { "model": "grok-5", "provider": "xai" },
                       { "model": "MiniMax-M2.7", "provider": "minimax" },
                       { "model": "glm-5", "provider": "zhipu" },
                       { "model": "kimi-k2.5", "provider": "moonshot" },
                       { "model": "deepseek-v4", "provider": "deepseek" }
                     ]
                   }
                   """;

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkModelsJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(8, actual: rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText =
            "SELECT provider, input_per_1m, output_per_1m FROM benchmark_models WHERE model = 'claude-opus-4-6';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected: "anthropic", actual: reader.GetString(0));
        Assert.Equal(15.0, actual: reader.GetDouble(1));
        Assert.Equal(75.0, actual: reader.GetDouble(2));
    }

    [Fact]
    public void Import_ObjectMapWrappedInModelsEnvelope_UnwrapsAndInsertsOneRowPerModel()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var json = """
                   {
                     "models": {
                       "claude-opus-4-6": { "provider": "anthropic", "input_per_1m": 15.0 }
                     }
                   }
                   """;

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkModelsJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(1, actual: rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText =
            "SELECT provider, input_per_1m FROM benchmark_models WHERE model = 'claude-opus-4-6';";
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected: "anthropic", actual: reader.GetString(0));
        Assert.Equal(15.0, actual: reader.GetDouble(1));
    }

    [Fact]
    public void Import_ObjectShapeWithLiteralModelsKeyAmongOthers_TreatsModelsAsAnOrdinaryModelName()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        // The envelope unwrap only fires for a *single-key* object literally named "models" - if "models"
        // is one entry among several real model entries, it is an ordinary (if oddly-named) model id, not
        // an envelope, and must not be unwrapped.
        var json = """
                   {
                     "models": { "provider": "acme" },
                     "other-model": { "provider": "other" }
                   }
                   """;

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rowCount = BenchmarkModelsJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();

        Assert.Equal(2, actual: rowCount);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT COUNT(*) FROM benchmark_models WHERE model = 'models';";
        Assert.Equal(1L, actual: (long)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Import_SecondCall_ReplacesEveryPriorRow()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        Import(temp: temp, """{ "model-a": {} }""");
        Import(temp: temp, """{ "model-b": {} }""");

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_models;";
        Assert.Equal(1, actual: Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void Import_KeepsRawJsonVerbatim()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        var json = """{ "model-a": { "custom_field": "custom_value" } }""";

        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkModelsJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT raw_json FROM benchmark_models WHERE model = 'model-a';";
        Assert.Contains(expectedSubstring: "custom_value", actualString: (string)readCommand.ExecuteScalar()!,
            comparisonType: StringComparison.Ordinal);
    }

    private static void Import(TempBenchmarkDatabase temp, string json)
    {
        using var connection = temp.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BenchmarkModelsJsonImporter.Import(json: json, connection: connection, transaction: transaction);
        transaction.Commit();
    }
}