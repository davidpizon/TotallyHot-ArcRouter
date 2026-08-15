using System.Text.Json;
using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Parses <c>models.json</c> directly into <c>benchmark_models</c> rows, replacing every prior row.
/// Accepts either shape Hugging Face JSON catalogs commonly publish - an object keyed by model id, or an
/// array of per-model objects carrying their own <c>model</c>/<c>id</c> field - since <c>raw_json</c> is
/// what actually matters (nothing in the codebase reads the typed columns yet; see the schema notes in
/// docs/router/coderouterbench-sqlite-migration-plan.md).
/// </summary>
public static class BenchmarkModelsJsonImporter
{
    /// <summary>
    /// Deletes every existing row of <c>benchmark_models</c> and inserts one row per model entry in
    /// <paramref name="json"/>, all on <paramref name="transaction"/> so the replace is atomic.
    /// </summary>
    /// <param name="json">The full text of <c>models.json</c>.</param>
    /// <param name="connection">The open database connection to import into.</param>
    /// <param name="transaction">The transaction the delete and every insert run on.</param>
    /// <returns>The number of rows imported.</returns>
    /// <exception cref="FormatException">The document's root is neither a JSON object nor an array.</exception>
    public static int Import(string json, SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM benchmark_models;";
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO benchmark_models (model, canonical_key, provider, tier, input_per_1m, output_per_1m, raw_json)
            VALUES ($model, $canonicalKey, $provider, $tier, $inputPer1M, $outputPer1M, $rawJson)
            ON CONFLICT(model) DO UPDATE SET
                canonical_key  = excluded.canonical_key,
                provider       = excluded.provider,
                tier           = excluded.tier,
                input_per_1m   = excluded.input_per_1m,
                output_per_1m  = excluded.output_per_1m,
                raw_json       = excluded.raw_json;
            """;
        var modelParam = insert.Parameters.Add("$model", SqliteType.Text);
        var canonicalKeyParam = insert.Parameters.Add("$canonicalKey", SqliteType.Text);
        var providerParam = insert.Parameters.Add("$provider", SqliteType.Text);
        var tierParam = insert.Parameters.Add("$tier", SqliteType.Text);
        var inputPer1MParam = insert.Parameters.Add("$inputPer1M", SqliteType.Real);
        var outputPer1MParam = insert.Parameters.Add("$outputPer1M", SqliteType.Real);
        var rawJsonParam = insert.Parameters.Add("$rawJson", SqliteType.Text);

        var rowCount = 0;
        foreach (var (modelId, element) in EnumerateModels(root))
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            modelParam.Value = modelId;
            canonicalKeyParam.Value = ModelNameCanonicalizer.Canonicalize(modelId);
            providerParam.Value = (object?)ReadOptionalString(element, "provider") ?? DBNull.Value;
            tierParam.Value = (object?)ReadOptionalString(element, "tier") ?? DBNull.Value;
            inputPer1MParam.Value = (object?)ReadOptionalNumber(element, "input_per_1m", "input_cost_per_1m") ?? DBNull.Value;
            outputPer1MParam.Value = (object?)ReadOptionalNumber(element, "output_per_1m", "output_cost_per_1m") ?? DBNull.Value;
            rawJsonParam.Value = element.GetRawText();

            insert.ExecuteNonQuery();
            rowCount++;
        }

        return rowCount;
    }

    /// <summary>
    /// Walks the document's models, whichever of the three accepted shapes it is in: a bare object keyed
    /// by model id, a bare array of per-model objects, or either of those wrapped in a single-key
    /// <c>{"models": ...}</c> envelope - the shape the published <c>models.json</c> actually uses.
    /// </summary>
    private static IEnumerable<(string ModelId, JsonElement Element)> EnumerateModels(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("models", out var envelope) &&
            root.EnumerateObject().Count() == 1 &&
            (envelope.ValueKind == JsonValueKind.Array || envelope.ValueKind == JsonValueKind.Object))
        {
            root = envelope;
        }

        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in root.EnumerateObject())
                {
                    yield return (property.Name, property.Value);
                }

                break;

            case JsonValueKind.Array:
                foreach (var element in root.EnumerateArray())
                {
                    var id = ReadOptionalString(element, "model") ?? ReadOptionalString(element, "id");
                    if (id is not null)
                    {
                        yield return (id, element);
                    }
                }

                break;

            default:
                throw new FormatException("'models.json' root must be a JSON object or array.");
        }
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadOptionalNumber(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }
        }

        return null;
    }
}
