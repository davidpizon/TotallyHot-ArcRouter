using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Parses <c>summary.json</c> directly into <c>benchmark_summary</c> rows, replacing every prior row.
/// One row is written per top-level property when the document root is a JSON object (each keyed by its
/// property name, with the property's own value as <c>raw_json</c>); a non-object root is stored as a
/// single row keyed <c>"summary"</c>, verbatim.
/// </summary>
public static class BenchmarkSummaryJsonImporter
{
    /// <summary>The key a non-object document root is stored under.</summary>
    public const string WholeDocumentKey = "summary";

    /// <summary>
    /// Deletes every existing row of <c>benchmark_summary</c> and inserts the rows derived from
    /// <paramref name="json"/>, all on <paramref name="transaction"/> so the replace is atomic.
    /// </summary>
    /// <param name="json">The full text of <c>summary.json</c>.</param>
    /// <param name="connection">The open database connection to import into.</param>
    /// <param name="transaction">The transaction the delete and every insert run on.</param>
    /// <returns>The number of rows imported.</returns>
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
            delete.CommandText = "DELETE FROM benchmark_summary;";
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO benchmark_summary (key, raw_json)
            VALUES ($key, $rawJson)
            ON CONFLICT(key) DO UPDATE SET raw_json = excluded.raw_json;
            """;
        var keyParam = insert.Parameters.Add("$key", SqliteType.Text);
        var rawJsonParam = insert.Parameters.Add("$rawJson", SqliteType.Text);

        var rowCount = 0;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                keyParam.Value = property.Name;
                rawJsonParam.Value = property.Value.GetRawText();
                insert.ExecuteNonQuery();
                rowCount++;
            }
        }
        else
        {
            keyParam.Value = WholeDocumentKey;
            rawJsonParam.Value = root.GetRawText();
            insert.ExecuteNonQuery();
            rowCount++;
        }

        return rowCount;
    }
}
