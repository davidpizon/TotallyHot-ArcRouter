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

        var taskIdIndex = BenchmarkCsvColumns.RequireColumn(columns, "task_id");
        var sourceSplitIndex = BenchmarkCsvColumns.RequireColumn(columns, "source_split");
        var benchIndex = BenchmarkCsvColumns.RequireColumn(columns, "bench");
        var dimensionIndex = BenchmarkCsvColumns.RequireColumn(columns, "dimension");
        var modelIndex = BenchmarkCsvColumns.RequireColumn(columns, "model");
        var originalTaskIdIndex = BenchmarkCsvColumns.FindColumn(columns, "original_task_id");
        var sourceModelIndex = BenchmarkCsvColumns.FindColumn(columns, "source_model");
        var resolvedIndex = BenchmarkCsvColumns.FindColumn(columns, "resolved");
        var applyOkIndex = BenchmarkCsvColumns.FindColumn(columns, "apply_ok");
        var gradedIndex = BenchmarkCsvColumns.FindColumn(columns, "graded");
        var inTokIndex = BenchmarkCsvColumns.FindColumn(columns, "in_tok");
        var outTokIndex = BenchmarkCsvColumns.FindColumn(columns, "out_tok");
        var callsIndex = BenchmarkCsvColumns.FindColumn(columns, "calls");
        var costUsdIndex = BenchmarkCsvColumns.FindColumn(columns, "cost_usd");
        var sourceStatusIndex = BenchmarkCsvColumns.FindColumn(columns, "source_status");
        var costSourceIndex = BenchmarkCsvColumns.FindColumn(columns, "cost_source");
        var requiredFieldCount = new[] { taskIdIndex, sourceSplitIndex, benchIndex, dimensionIndex, modelIndex }.Max() + 1;

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
        var taskIdParam = insert.Parameters.Add("$taskId", SqliteType.Text);
        var sourceSplitParam = insert.Parameters.Add("$sourceSplit", SqliteType.Text);
        var benchParam = insert.Parameters.Add("$bench", SqliteType.Text);
        var originalTaskIdParam = insert.Parameters.Add("$originalTaskId", SqliteType.Text);
        var dimensionParam = insert.Parameters.Add("$dimension", SqliteType.Text);
        var modelParam = insert.Parameters.Add("$model", SqliteType.Text);
        var sourceModelParam = insert.Parameters.Add("$sourceModel", SqliteType.Text);
        var resolvedParam = insert.Parameters.Add("$resolved", SqliteType.Integer);
        var applyOkParam = insert.Parameters.Add("$applyOk", SqliteType.Integer);
        var gradedParam = insert.Parameters.Add("$graded", SqliteType.Integer);
        var inTokParam = insert.Parameters.Add("$inTok", SqliteType.Integer);
        var outTokParam = insert.Parameters.Add("$outTok", SqliteType.Integer);
        var callsParam = insert.Parameters.Add("$calls", SqliteType.Integer);
        var costUsdParam = insert.Parameters.Add("$costUsd", SqliteType.Real);
        var sourceStatusParam = insert.Parameters.Add("$sourceStatus", SqliteType.Text);
        var costSourceParam = insert.Parameters.Add("$costSource", SqliteType.Text);

        var rowCount = 0;
        string? line;
        var rowNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = CodeRouterBenchCsvReader.SplitLine(line);
            if (fields.Count < requiredFieldCount)
            {
                throw new FormatException(
                    $"OOD results CSV row {rowNumber} has {fields.Count} column(s) but requires at least {requiredFieldCount}.");
            }

            var model = fields[modelIndex];
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new FormatException($"OOD results CSV row {rowNumber} has an empty model.");
            }

            taskIdParam.Value = fields[taskIdIndex];
            sourceSplitParam.Value = fields[sourceSplitIndex];
            benchParam.Value = fields[benchIndex];
            originalTaskIdParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalString(fields, originalTaskIdIndex) ?? DBNull.Value;
            dimensionParam.Value = CodeRouterBenchCsvReader.NormalizeDimension(fields[dimensionIndex]);
            modelParam.Value = ModelNameCanonicalizer.Canonicalize(model);
            sourceModelParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalString(fields, sourceModelIndex) ?? DBNull.Value;
            resolvedParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalBoolAsInt(fields, resolvedIndex) ?? DBNull.Value;
            applyOkParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalBoolAsInt(fields, applyOkIndex) ?? DBNull.Value;
            gradedParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalBoolAsInt(fields, gradedIndex) ?? DBNull.Value;
            inTokParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalInt(fields, inTokIndex) ?? DBNull.Value;
            outTokParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalInt(fields, outTokIndex) ?? DBNull.Value;
            callsParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalInt(fields, callsIndex) ?? DBNull.Value;
            costUsdParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalDecimal(fields, costUsdIndex) ?? DBNull.Value;
            sourceStatusParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalString(fields, sourceStatusIndex) ?? DBNull.Value;
            costSourceParam.Value = (object?)BenchmarkCsvColumns.ReadOptionalString(fields, costSourceIndex) ?? DBNull.Value;

            insert.ExecuteNonQuery();
            rowCount++;
        }

        return rowCount;
    }
}
