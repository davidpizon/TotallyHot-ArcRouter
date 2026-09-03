using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Parses one of the two ID-schema task JSONL files (<c>id_probing_tasks.jsonl</c>,
/// <c>id_test_tasks.jsonl</c>) directly into <c>benchmark_id_tasks</c> rows, replacing any prior rows
/// for the same <c>split</c>. Each row keeps <c>task_id</c>/<c>split</c>/<c>dimension</c> for joins
/// against <c>benchmark_id_results</c> plus the verbatim <c>raw_json</c> - see the schema in
/// docs/router/coderouterbench-sqlite-migration-plan.md.
/// </summary>
public static class BenchmarkIdTasksJsonlImporter
{
    /// <summary>
    /// Deletes existing <paramref name="split"/> rows and inserts every line of <paramref name="reader"/>,
    /// all on <paramref name="transaction"/> so the replace is atomic.
    /// </summary>
    /// <param name="reader">An open reader positioned at the start of the JSONL file.</param>
    /// <param name="split">The <c>split</c> value written for every imported row (<c>probing</c> or <c>id_test</c>).</param>
    /// <param name="connection">The open database connection to import into.</param>
    /// <param name="transaction">The transaction the delete and every insert run on.</param>
    /// <returns>The number of rows imported.</returns>
    /// <exception cref="FormatException">A line is not a JSON object, or is missing <c>task_id</c> or <c>dimension</c>.</exception>
    public static int Import(TextReader reader, string split, SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM benchmark_id_tasks WHERE split = $split;";
            delete.Parameters.AddWithValue(parameterName: "$split", value: split);
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
                             INSERT INTO benchmark_id_tasks (task_id, split, source_split, dimension, raw_json)
                             VALUES ($taskId, $split, $sourceSplit, $dimension, $rawJson)
                             ON CONFLICT(task_id) DO UPDATE SET
                                 split         = excluded.split,
                                 source_split  = excluded.source_split,
                                 dimension     = excluded.dimension,
                                 raw_json      = excluded.raw_json;
                             """;
        var taskIdParam = insert.Parameters.Add(parameterName: "$taskId", type: SqliteType.Text);
        insert.Parameters.AddWithValue(parameterName: "$split", value: split);
        var sourceSplitParam = insert.Parameters.Add(parameterName: "$sourceSplit", type: SqliteType.Text);
        var dimensionParam = insert.Parameters.Add(parameterName: "$dimension", type: SqliteType.Text);
        var rawJsonParam = insert.Parameters.Add(parameterName: "$rawJson", type: SqliteType.Text);

        var rowCount = 0;
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException($"Tasks JSONL line {lineNumber} is not a JSON object.");

            if (!root.TryGetProperty(propertyName: "task_id", value: out var taskIdElement) ||
                taskIdElement.ValueKind != JsonValueKind.String)
                throw new FormatException($"Tasks JSONL line {lineNumber} is missing a string 'task_id'.");

            if (!root.TryGetProperty(propertyName: "dimension", value: out var dimensionElement) ||
                dimensionElement.ValueKind != JsonValueKind.String)
                throw new FormatException($"Tasks JSONL line {lineNumber} is missing a string 'dimension'.");

            // Upstream's "source_split" carries the train/val/test distinction this column is documented to
            // hold; "split" is the probing/id_test discriminator already captured by the split column. Only
            // fall back to the split argument when source_split is genuinely absent from the row.
            var sourceSplit = root.TryGetProperty(propertyName: "source_split", value: out var sourceSplitElement) &&
                              sourceSplitElement.ValueKind == JsonValueKind.String
                ? sourceSplitElement.GetString()!
                : split;

            taskIdParam.Value = taskIdElement.GetString()!;
            sourceSplitParam.Value = sourceSplit;
            dimensionParam.Value = CodeRouterBenchCsvReader.NormalizeDimension(dimensionElement.GetString()!);
            rawJsonParam.Value = line;

            insert.ExecuteNonQuery();
            rowCount++;
        }

        return rowCount;
    }
}