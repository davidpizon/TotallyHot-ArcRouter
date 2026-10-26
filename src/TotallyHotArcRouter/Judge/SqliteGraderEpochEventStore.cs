using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// SQLite <c>grader_epoch_events</c> in <see cref="RouterMemoryDatabase"/>. <see cref="Insert"/> is the
/// only write. Rows are not registered with score retention, so a restart still has every epoch.
/// </summary>
public sealed class SqliteGraderEpochEventStore : IGraderEpochEventStore
{
    /// <summary>The table and its time index. Additive on an existing install. No migration framework.</summary>
    internal const string TableSql = """
                                     CREATE TABLE IF NOT EXISTS grader_epoch_events (
                                         id INTEGER PRIMARY KEY AUTOINCREMENT,
                                         at_utc TEXT NOT NULL,
                                         kind TEXT NOT NULL,
                                         epoch_type TEXT NOT NULL,
                                         cause TEXT NOT NULL,
                                         artifact_id TEXT NULL,
                                         previous_artifact_id TEXT NULL,
                                         window_size INTEGER NULL,
                                         mean_miss REAL NULL,
                                         previous_teacher_model TEXT NULL,
                                         teacher_model TEXT NULL,
                                         previous_prompt_version TEXT NULL,
                                         prompt_version TEXT NULL,
                                         rollback_mark_present INTEGER NULL,
                                         previous_bge_model TEXT NULL,
                                         bge_model TEXT NULL,
                                         previous_feature_schema_version INTEGER NULL,
                                         feature_schema_version INTEGER NULL
                                     );
                                     CREATE INDEX IF NOT EXISTS ix_grader_epoch_events_at ON grader_epoch_events (at_utc, id);
                                     """;

    private readonly RouterMemoryDatabase _database;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;

    /// <summary>Initializes a new instance of the <see cref="SqliteGraderEpochEventStore"/> class.</summary>
    /// <param name="database">The router-memory database, the same file as <c>grade_receipts</c>.</param>
    public SqliteGraderEpochEventStore(RouterMemoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc/>
    public void Insert(GraderEpochEventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_connection is null || _transaction is null)
            throw new InvalidOperationException("An epoch event is inserted only inside Execute.");

        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
                              INSERT INTO grader_epoch_events (
                                  at_utc, kind, epoch_type, cause, artifact_id, previous_artifact_id,
                                  window_size, mean_miss, previous_teacher_model, teacher_model,
                                  previous_prompt_version, prompt_version, rollback_mark_present,
                                  previous_bge_model, bge_model, previous_feature_schema_version,
                                  feature_schema_version)
                              VALUES (
                                  $at, $kind, $type, $cause, $artifact, $previous,
                                  $window, $miss, $prevTeacher, $teacher, $prevPrompt, $prompt, $mark,
                                  $prevBge, $bge, $prevSchema, $schema);
                              """;
        command.Parameters.AddWithValue("$at", row.AtUtc);
        command.Parameters.AddWithValue("$kind", row.Kind);
        command.Parameters.AddWithValue("$type", row.EpochType);
        command.Parameters.AddWithValue("$cause", row.Cause);
        command.Parameters.AddWithValue("$artifact", (object?)row.ArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous", (object?)row.PreviousArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$window", (object?)row.WindowSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$miss", (object?)row.MeanMiss ?? DBNull.Value);
        command.Parameters.AddWithValue("$prevTeacher", (object?)row.PreviousTeacherModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$teacher", (object?)row.TeacherModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$prevPrompt", (object?)row.PreviousPromptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$prompt", (object?)row.PromptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$mark", (object?)row.RollbackMarkPresent ?? DBNull.Value);
        command.Parameters.AddWithValue("$prevBge", (object?)row.PreviousBgeModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$bge", (object?)row.BgeModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$prevSchema", (object?)row.PreviousFeatureSchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$schema", (object?)row.FeatureSchemaVersion ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public void Execute(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_transaction is not null)
        {
            work();
            return;
        }

        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        _connection = connection;
        _transaction = transaction;
        try
        {
            work();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction = null;
            _connection = null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<GraderEpochEventRow> ListForPlot()
    {
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, at_utc, kind, epoch_type, cause, artifact_id, previous_artifact_id, window_size, mean_miss,
                                     previous_teacher_model, teacher_model, previous_prompt_version, prompt_version, rollback_mark_present,
                                     previous_bge_model, bge_model, previous_feature_schema_version, feature_schema_version
                              FROM grader_epoch_events
                              ORDER BY at_utc, id;
                              """;
        return ReadAll(command);
    }

    /// <inheritdoc/>
    public GraderEpochEventRow? LatestAuthority(string kind)
    {
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, at_utc, kind, epoch_type, cause, artifact_id, previous_artifact_id, window_size, mean_miss,
                                     previous_teacher_model, teacher_model, previous_prompt_version, prompt_version, rollback_mark_present,
                                     previous_bge_model, bge_model, previous_feature_schema_version, feature_schema_version
                              FROM grader_epoch_events
                              WHERE kind = $kind AND epoch_type IN ($takeover, $resumed)
                              ORDER BY id DESC
                              LIMIT 1;
                              """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$takeover", IGraderEpochEventStore.LocalTakeover);
        command.Parameters.AddWithValue("$resumed", IGraderEpochEventStore.TeacherResumed);
        return ReadAll(command).FirstOrDefault();
    }

    /// <inheritdoc/>
    public GraderEpochEventRow? LatestOfType(string kind, string epochType)
    {
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, at_utc, kind, epoch_type, cause, artifact_id, previous_artifact_id, window_size, mean_miss,
                                     previous_teacher_model, teacher_model, previous_prompt_version, prompt_version, rollback_mark_present,
                                     previous_bge_model, bge_model, previous_feature_schema_version, feature_schema_version
                              FROM grader_epoch_events
                              WHERE kind = $kind AND epoch_type = $type
                              ORDER BY id DESC
                              LIMIT 1;
                              """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$type", epochType);
        return ReadAll(command).FirstOrDefault();
    }

    private static List<GraderEpochEventRow> ReadAll(SqliteCommand command)
    {
        var rows = new List<GraderEpochEventRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new GraderEpochEventRow
            {
                Id = reader.GetInt64(0),
                AtUtc = reader.GetString(1),
                Kind = reader.GetString(2),
                EpochType = reader.GetString(3),
                Cause = reader.GetString(4),
                ArtifactId = reader.IsDBNull(5) ? null : reader.GetString(5),
                PreviousArtifactId = reader.IsDBNull(6) ? null : reader.GetString(6),
                WindowSize = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                MeanMiss = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                PreviousTeacherModel = reader.IsDBNull(9) ? null : reader.GetString(9),
                TeacherModel = reader.IsDBNull(10) ? null : reader.GetString(10),
                PreviousPromptVersion = reader.IsDBNull(11) ? null : reader.GetString(11),
                PromptVersion = reader.IsDBNull(12) ? null : reader.GetString(12),
                RollbackMarkPresent = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                PreviousBgeModel = reader.IsDBNull(14) ? null : reader.GetString(14),
                BgeModel = reader.IsDBNull(15) ? null : reader.GetString(15),
                PreviousFeatureSchemaVersion = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                FeatureSchemaVersion = reader.IsDBNull(17) ? null : reader.GetInt32(17)
            });
        }

        return rows;
    }
}
