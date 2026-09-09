using System.Globalization;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A SQLite-backed <see cref="IGraderScoreStore"/> over <see cref="RouterMemoryDatabase"/>'s
/// <c>grader_scores</c> table (docs/router/grader-reliability-plan.md, Phase Q4). Shares the router-memory
/// database file, mirroring <see cref="SqliteJudgeShadowScoreStore"/>'s own reasoning: no raw text is ever
/// stored here, so this table needs none of <c>TranscriptDatabase</c>'s opt-in-creation treatment.
/// </summary>
public sealed class SqliteGraderScoreStore : IGraderScoreStore
{
    private readonly RouterMemoryDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="SqliteGraderScoreStore"/> class.</summary>
    /// <param name="database">The router-memory database to persist rows in. Its schema must already be created.</param>
    public SqliteGraderScoreStore(RouterMemoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc/>
    public Task InsertAsync(GraderScoreRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO grader_scores (
                                  correlation_id, created_at_utc, dimension, model, grader_key, score,
                                  grader_backbone_model, response_length_chars)
                              VALUES (
                                  $correlationId, $createdAtUtc, $dimension, $model, $graderKey, $score,
                                  $graderBackboneModel, $responseLengthChars);
                              """;
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: record.CorrelationId);
        command.Parameters.AddWithValue(parameterName: "$createdAtUtc", value: record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(parameterName: "$dimension", value: record.Dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: record.Model);
        command.Parameters.AddWithValue(parameterName: "$graderKey", value: record.GraderKey);
        command.Parameters.AddWithValue(parameterName: "$score", value: record.Score);
        command.Parameters.AddWithValue(parameterName: "$graderBackboneModel",
            value: (object?)record.GraderBackboneModel ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$responseLengthChars",
            value: (object?)record.ResponseLengthChars ?? DBNull.Value);

        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM grader_scores;";
        return Task.FromResult(Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture));
    }

    /// <inheritdoc/>
    public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (count <= 0) return Task.FromResult(0);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              DELETE FROM grader_scores
                              WHERE id IN (SELECT id FROM grader_scores ORDER BY id ASC LIMIT $count);
                              """;
        command.Parameters.AddWithValue(parameterName: "$count", value: count);
        return Task.FromResult(command.ExecuteNonQuery());
    }

    /// <inheritdoc/>
    public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM grader_scores WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue(parameterName: "$cutoff", value: cutoff.ToString("O"));
        return Task.FromResult(command.ExecuteNonQuery());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GraderScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, correlation_id, created_at_utc, dimension, model, grader_key, score,
                                     grader_backbone_model, response_length_chars
                              FROM grader_scores;
                              """;

        List<GraderScoreRecord> rows = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new GraderScoreRecord(
                Id: reader.GetInt64(0),
                CorrelationId: reader.GetString(1),
                CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                Dimension: reader.GetString(3),
                Model: reader.GetString(4),
                GraderKey: reader.GetString(5),
                Score: reader.GetDouble(6),
                GraderBackboneModel: reader.IsDBNull(7) ? null : reader.GetString(7),
                ResponseLengthChars: reader.IsDBNull(8) ? null : reader.GetInt32(8)));

        return Task.FromResult<IReadOnlyList<GraderScoreRecord>>(rows);
    }
}
