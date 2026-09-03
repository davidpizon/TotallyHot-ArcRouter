using Microsoft.Data.Sqlite;
using System.Globalization;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Parses one of the two ID-schema results CSVs (<c>id_probing_results_long.csv</c>,
/// <c>id_test_results_long.csv</c>) directly into <c>benchmark_id_results</c> rows, replacing any prior
/// rows for the same <c>split</c> - see the schema in
/// docs/router/coderouterbench-sqlite-migration-plan.md.
/// </summary>
public static class BenchmarkIdResultsCsvImporter
{
    /// <summary>
    /// Deletes existing <paramref name="split"/> rows and inserts every data row of
    /// <paramref name="reader"/>, all on <paramref name="transaction"/> so the replace is atomic.
    /// </summary>
    /// <param name="reader">An open reader positioned at the start of the CSV, header row included.</param>
    /// <param name="split">The <c>split</c> value written for every imported row (<c>probing</c> or <c>id_test</c>).</param>
    /// <param name="connection">The open database connection to import into.</param>
    /// <param name="transaction">The transaction the delete and every insert run on.</param>
    /// <returns>The number of rows imported.</returns>
    /// <exception cref="FormatException">A required column is missing, or a data row is malformed.</exception>
    public static int Import(TextReader reader, string split, SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var headerLine = reader.ReadLine() ?? throw new FormatException("The results CSV has no header row.");
        var columns = CodeRouterBenchCsvReader.SplitLine(headerLine);

        var taskIdIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "task_id");
        var dimensionIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "dimension");
        var modelIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "model");
        var scoreIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "score");
        var sourceSplitIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "source_split");
        var costUsdIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "cost_usd");
        var inputTokensIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "input_tokens");
        var outputTokensIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "output_tokens");
        var totalTokensIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "total_tokens");
        var latencyMsIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "latency_ms");
        var costSourceIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "cost_source");
        var requiredFieldCount = new[] { taskIdIndex, dimensionIndex, modelIndex, scoreIndex }.Max() + 1;

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM benchmark_id_results WHERE split = $split;";
            delete.Parameters.AddWithValue(parameterName: "$split", value: split);
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
                             INSERT INTO benchmark_id_results
                                 (task_id, split, source_split, dimension, model, score, cost_usd, input_tokens, output_tokens, total_tokens, latency_ms, cost_source)
                             VALUES
                                 ($taskId, $split, $sourceSplit, $dimension, $model, $score, $costUsd, $inputTokens, $outputTokens, $totalTokens, $latencyMs, $costSource);
                             """;
        var taskIdParam = insert.Parameters.Add(parameterName: "$taskId", type: SqliteType.Text);
        insert.Parameters.AddWithValue(parameterName: "$split", value: split);
        var sourceSplitParam = insert.Parameters.Add(parameterName: "$sourceSplit", type: SqliteType.Text);
        var dimensionParam = insert.Parameters.Add(parameterName: "$dimension", type: SqliteType.Text);
        var modelParam = insert.Parameters.Add(parameterName: "$model", type: SqliteType.Text);
        var scoreParam = insert.Parameters.Add(parameterName: "$score", type: SqliteType.Real);
        var costUsdParam = insert.Parameters.Add(parameterName: "$costUsd", type: SqliteType.Real);
        var inputTokensParam = insert.Parameters.Add(parameterName: "$inputTokens", type: SqliteType.Integer);
        var outputTokensParam = insert.Parameters.Add(parameterName: "$outputTokens", type: SqliteType.Integer);
        var totalTokensParam = insert.Parameters.Add(parameterName: "$totalTokens", type: SqliteType.Integer);
        var latencyMsParam = insert.Parameters.Add(parameterName: "$latencyMs", type: SqliteType.Integer);
        var costSourceParam = insert.Parameters.Add(parameterName: "$costSource", type: SqliteType.Text);

        var rowCount = 0;
        string? line;
        var rowNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = CodeRouterBenchCsvReader.SplitLine(line);
            if (fields.Count < requiredFieldCount)
                throw new FormatException(
                    $"Results CSV row {rowNumber} has {fields.Count} column(s) but requires at least {requiredFieldCount}.");

            if (!double.TryParse(s: fields[scoreIndex], style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture, result: out var score))
                throw new FormatException(
                    $"Results CSV row {rowNumber} has a non-numeric score '{fields[scoreIndex]}'.");

            var model = fields[modelIndex];
            if (string.IsNullOrWhiteSpace(model))
                throw new FormatException($"Results CSV row {rowNumber} has an empty model.");

            taskIdParam.Value = fields[taskIdIndex];
            sourceSplitParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalString(fields: fields, index: sourceSplitIndex) ?? split;
            dimensionParam.Value = CodeRouterBenchCsvReader.NormalizeDimension(fields[dimensionIndex]);
            modelParam.Value = ModelNameCanonicalizer.Canonicalize(model);
            scoreParam.Value = score;
            costUsdParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalDecimal(fields: fields, index: costUsdIndex) ?? DBNull.Value;
            inputTokensParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalInt(fields: fields, index: inputTokensIndex) ?? DBNull.Value;
            outputTokensParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalInt(fields: fields, index: outputTokensIndex) ?? DBNull.Value;
            totalTokensParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalInt(fields: fields, index: totalTokensIndex) ?? DBNull.Value;
            latencyMsParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalInt(fields: fields, index: latencyMsIndex) ?? DBNull.Value;
            costSourceParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalString(fields: fields, index: costSourceIndex) ?? DBNull.Value;

            insert.ExecuteNonQuery();
            rowCount++;
        }

        return rowCount;
    }
}