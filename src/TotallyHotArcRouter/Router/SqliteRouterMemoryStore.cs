using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// A SQLite-backed <see cref="IRouterMemoryStore"/> over <see cref="RouterMemoryDatabase"/>'s
/// <c>dimension_scores</c> table, holding one running <see cref="ScoreAggregate"/> row per
/// (dimension, model) pair.
/// </summary>
/// <remarks>
/// Replaces a JSON-file store that rewrote the whole memory on every observation and could not survive a
/// crash mid-write - a truncated file loaded as empty memory, silently discarding everything the router had
/// learned. Sharing <see cref="RouterMemoryDatabase"/> with <see cref="SqliteMemoryEntryStore"/> puts both
/// memory tables behind the same WAL-journaled file, so a write is atomic and durable rather than an
/// in-place overwrite.
/// </remarks>
public sealed class SqliteRouterMemoryStore : IRouterMemoryStore
{
    private readonly RouterMemoryDatabase _database;
    private readonly Lock _schemaLock = new();
    private bool _schemaEnsured;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteRouterMemoryStore"/> class.
    /// </summary>
    /// <param name="database">The database to persist aggregates in.</param>
    public SqliteRouterMemoryStore(RouterMemoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc/>
    public Task<ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();

        var memory = new ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dimension, model, sum, count FROM dimension_scores;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var dimension = reader.GetString(0);
            var model = reader.GetString(1);
            var aggregate = new ScoreAggregate(Sum: reader.GetDouble(2), Count: reader.GetInt32(3));

            var models = memory.GetOrAdd(key: dimension,
                valueFactory: static _ => new ConcurrentDictionary<string, ScoreAggregate>());
            models[model] = aggregate;
        }

        return Task.FromResult(memory);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The addition happens <em>inside</em> SQLite via <c>ON CONFLICT DO UPDATE</c> rather than by reading
    /// the current aggregate, adding to it in C#, and writing it back. That distinction is load-bearing: a
    /// read-modify-write lets two concurrent observations of the same (dimension, model) pair both read the
    /// same starting value and the later write silently discard the earlier score. Letting the database
    /// compute <c>sum + excluded.sum</c> makes the fold atomic, so no observation is lost regardless of
    /// interleaving.
    /// </remarks>
    public Task RecordScoreAsync(
        string dimension,
        string model,
        double score,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO dimension_scores (dimension, model, sum, count)
                              VALUES ($dimension, $model, $score, 1)
                              ON CONFLICT (dimension, model) DO UPDATE SET
                                  sum   = sum + excluded.sum,
                                  count = count + 1;
                              """;
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$score", value: score);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the schema on first use, once per instance.
    /// </summary>
    /// <remarks>
    /// This store does not assume startup already created the schema, unlike
    /// <see cref="SqliteMemoryEntryStore"/>. Two reasons. Scores arrive from the quality verification path
    /// for the life of the process, and <c>StartupHealthCheckHostedService</c> runs its
    /// <see cref="RouterMemoryDatabase.EnsureCreated"/> call best-effort inside a catch that only logs - so
    /// a startup failure there would otherwise turn every subsequent score write into a "no such table"
    /// throw rather than a degraded-but-working router. The JSON store this replaced created its own file on
    /// demand and had no such ordering dependency; keeping that property avoids trading a persistence
    /// upgrade for a new startup-order coupling. <see cref="RouterMemoryDatabase.EnsureCreated"/> is
    /// idempotent, so overlapping with the startup call costs nothing.
    /// </remarks>
    private void EnsureSchema()
    {
        lock (_schemaLock)
        {
            if (_schemaEnsured) return;

            _database.EnsureCreated();
            _schemaEnsured = true;
        }
    }
}