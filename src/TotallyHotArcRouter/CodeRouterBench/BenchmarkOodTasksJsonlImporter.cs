using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Parses <c>ood176_tasks.jsonl</c> directly into <c>benchmark_ood_tasks</c> rows, replacing every prior
/// row (one file, no split column - see the schema in
/// docs/router/coderouterbench-sqlite-migration-plan.md).
/// </summary>
public static class BenchmarkOodTasksJsonlImporter
{
    /// <summary>
    /// Deletes every existing row of <c>benchmark_ood_tasks</c> and inserts every line of
    /// <paramref name="reader"/>, all on <paramref name="transaction"/> so the replace is atomic.
    /// </summary>
    /// <param name="reader">An open reader positioned at the start of the JSONL file.</param>
    /// <param name="connection">The open database connection to import into.</param>
    /// <param name="transaction">The transaction the delete and every insert run on.</param>
    /// <returns>The number of rows imported.</returns>
    /// <exception cref="FormatException">A line is not a JSON object, or is missing <c>task_id</c>, <c>bench</c>, or <c>dimension</c>.</exception>
    public static int Import(TextReader reader, SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM benchmark_ood_tasks;";
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, language, difficulty, raw_json)
            VALUES ($taskId, $sourceSplit, $bench, $dimension, $language, $difficulty, $rawJson)
            ON CONFLICT(task_id) DO UPDATE SET
                source_split = excluded.source_split,
                bench        = excluded.bench,
                dimension    = excluded.dimension,
                language     = excluded.language,
                difficulty   = excluded.difficulty,
                raw_json     = excluded.raw_json;
            """;
        var taskIdParam = insert.Parameters.Add("$taskId", SqliteType.Text);
        var sourceSplitParam = insert.Parameters.Add("$sourceSplit", SqliteType.Text);
        var benchParam = insert.Parameters.Add("$bench", SqliteType.Text);
        var dimensionParam = insert.Parameters.Add("$dimension", SqliteType.Text);
        var languageParam = insert.Parameters.Add("$language", SqliteType.Text);
        var difficultyParam = insert.Parameters.Add("$difficulty", SqliteType.Text);
        var rawJsonParam = insert.Parameters.Add("$rawJson", SqliteType.Text);

        var rowCount = 0;
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"OOD tasks JSONL line {lineNumber} is not a JSON object.");
            }

            if (!root.TryGetProperty("task_id", out var taskIdElement) || taskIdElement.ValueKind != JsonValueKind.String)
            {
                throw new FormatException($"OOD tasks JSONL line {lineNumber} is missing a string 'task_id'.");
            }

            if (!root.TryGetProperty("bench", out var benchElement) || benchElement.ValueKind != JsonValueKind.String)
            {
                throw new FormatException($"OOD tasks JSONL line {lineNumber} is missing a string 'bench'.");
            }

            if (!root.TryGetProperty("dimension", out var dimensionElement) || dimensionElement.ValueKind != JsonValueKind.String)
            {
                throw new FormatException($"OOD tasks JSONL line {lineNumber} is missing a string 'dimension'.");
            }

            // The OOD corpus is one split (there is no probing/id_test distinction here), so an absent
            // source_split column defaults to the fixed "ood" value rather than guessing at bench.
            var sourceSplit = root.TryGetProperty("source_split", out var splitElement) && splitElement.ValueKind == JsonValueKind.String
                ? splitElement.GetString()!
                : "ood";

            taskIdParam.Value = taskIdElement.GetString()!;
            sourceSplitParam.Value = sourceSplit;
            benchParam.Value = benchElement.GetString()!;
            dimensionParam.Value = CodeRouterBenchCsvReader.NormalizeDimension(dimensionElement.GetString()!);
            languageParam.Value = (object?)ReadOptionalString(root, "language") ?? DBNull.Value;
            difficultyParam.Value = (object?)ReadOptionalString(root, "difficulty") ?? DBNull.Value;
            rawJsonParam.Value = line;

            insert.ExecuteNonQuery();
            rowCount++;
        }

        return rowCount;
    }

    /// <summary>Reads a string property from a task's JSON object, returning <see langword="null"/> when absent or not a string.</summary>
    /// <param name="root">The task's root JSON element.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The property's string value, or <see langword="null"/>.</returns>
    private static string? ReadOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
