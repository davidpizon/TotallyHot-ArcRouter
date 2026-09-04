using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Parses <c>ood176_results_long.csv</c> directly into <c>benchmark_ood_results</c> rows, replacing
/// every prior row (the OOD table carries no split column - it is one file, not two, see the schema in
/// docs/router/coderouterbench-sqlite-migration-plan.md).
/// </summary>
public static class BenchmarkOodResultsCsvImporter
{
    /// <summary>
    /// Deletes every existing row of <c>benchmark_ood_results</c> and inserts every data row of
    /// <paramref name="reader"/>, all on <paramref name="transaction"/> so the replace is atomic.
    /// </summary>
    /// <param name="reader">An open reader positioned at the start of the CSV, header row included.</param>
    /// <param name="connection">The open database connection to import into.</param>
    /// <param name="transaction">The transaction the delete and every insert run on.</param>
    /// <returns>The number of rows imported.</returns>
    /// <exception cref="FormatException">A required column is missing, or a data row is malformed.</exception>
    public static int Import(TextReader reader, SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var headerLine = reader.ReadLine() ?? throw new FormatException("The OOD results CSV has no header row.");
        var columns = CodeRouterBenchCsvReader.SplitLine(headerLine);

        var taskIdIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "task_id");
        var sourceSplitIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "source_split");
        var benchIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "bench");
        var dimensionIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "dimension");
        var modelIndex = BenchmarkCsvColumns.RequireColumn(columns: columns, name: "model");
        var originalTaskIdIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "original_task_id");
        var sourceModelIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "source_model");
        var resolvedIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "resolved");
        var applyOkIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "apply_ok");
        var gradedIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "graded");
        var inTokIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "in_tok");
        var outTokIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "out_tok");
        var callsIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "calls");
        var costUsdIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "cost_usd");
        var sourceStatusIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "source_status");
        var costSourceIndex = BenchmarkCsvColumns.FindColumn(columns: columns, name: "cost_source");
        var requiredFieldCount =
            new[] { taskIdIndex, sourceSplitIndex, benchIndex, dimensionIndex, modelIndex }.Max() + 1;

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM benchmark_ood_results;";
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
                             INSERT INTO benchmark_ood_results
                                 (task_id, source_split, bench, original_task_id, dimension, model, source_model, resolved, apply_ok,
                                  graded, in_tok, out_tok, calls, cost_usd, source_status, cost_source)
                             VALUES
                                 ($taskId, $sourceSplit, $bench, $originalTaskId, $dimension, $model, $sourceModel, $resolved, $applyOk,
                                  $graded, $inTok, $outTok, $calls, $costUsd, $sourceStatus, $costSource);
                             """;
        var taskIdParam = insert.Parameters.Add(parameterName: "$taskId", type: SqliteType.Text);
        var sourceSplitParam = insert.Parameters.Add(parameterName: "$sourceSplit", type: SqliteType.Text);
        var benchParam = insert.Parameters.Add(parameterName: "$bench", type: SqliteType.Text);
        var originalTaskIdParam = insert.Parameters.Add(parameterName: "$originalTaskId", type: SqliteType.Text);
        var dimensionParam = insert.Parameters.Add(parameterName: "$dimension", type: SqliteType.Text);
        var modelParam = insert.Parameters.Add(parameterName: "$model", type: SqliteType.Text);
        var sourceModelParam = insert.Parameters.Add(parameterName: "$sourceModel", type: SqliteType.Text);
        var resolvedParam = insert.Parameters.Add(parameterName: "$resolved", type: SqliteType.Integer);
        var applyOkParam = insert.Parameters.Add(parameterName: "$applyOk", type: SqliteType.Integer);
        var gradedParam = insert.Parameters.Add(parameterName: "$graded", type: SqliteType.Integer);
        var inTokParam = insert.Parameters.Add(parameterName: "$inTok", type: SqliteType.Integer);
        var outTokParam = insert.Parameters.Add(parameterName: "$outTok", type: SqliteType.Integer);
        var callsParam = insert.Parameters.Add(parameterName: "$calls", type: SqliteType.Integer);
        var costUsdParam = insert.Parameters.Add(parameterName: "$costUsd", type: SqliteType.Real);
        var sourceStatusParam = insert.Parameters.Add(parameterName: "$sourceStatus", type: SqliteType.Text);
        var costSourceParam = insert.Parameters.Add(parameterName: "$costSource", type: SqliteType.Text);

        var rowCount = 0;
        var rowNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = CodeRouterBenchCsvReader.SplitLine(line);
            if (fields.Count < requiredFieldCount)
                throw new FormatException(
                    $"OOD results CSV row {rowNumber} has {fields.Count} column(s) but requires at least {requiredFieldCount}.");

            var model = fields[modelIndex];
            if (string.IsNullOrWhiteSpace(model))
                throw new FormatException($"OOD results CSV row {rowNumber} has an empty model.");

            taskIdParam.Value = fields[taskIdIndex];
            sourceSplitParam.Value = fields[sourceSplitIndex];
            benchParam.Value = fields[benchIndex];
            originalTaskIdParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalString(fields: fields, index: originalTaskIdIndex) ??
                DBNull.Value;
            dimensionParam.Value = CodeRouterBenchCsvReader.NormalizeDimension(fields[dimensionIndex]);
            modelParam.Value = ModelNameCanonicalizer.Canonicalize(model);
            sourceModelParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalString(fields: fields, index: sourceModelIndex) ??
                DBNull.Value;
            resolvedParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalBoolAsInt(fields: fields, index: resolvedIndex) ??
                DBNull.Value;
            applyOkParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalBoolAsInt(fields: fields, index: applyOkIndex) ?? DBNull.Value;
            gradedParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalBoolAsInt(fields: fields, index: gradedIndex) ?? DBNull.Value;
            inTokParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalInt(fields: fields, index: inTokIndex) ??
                               DBNull.Value;
            outTokParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalInt(fields: fields, index: outTokIndex) ??
                                DBNull.Value;
            callsParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalInt(fields: fields, index: callsIndex) ??
                               DBNull.Value;
            costUsdParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalDecimal(fields: fields, index: costUsdIndex) ?? DBNull.Value;
            sourceStatusParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalString(fields: fields, index: sourceStatusIndex) ??
                DBNull.Value;
            costSourceParam.Value =
                (object?)BenchmarkCsvColumns.ReadOptionalString(fields: fields, index: costSourceIndex) ?? DBNull.Value;

            insert.ExecuteNonQuery();
            rowCount++;
        }

        return rowCount;
    }
}