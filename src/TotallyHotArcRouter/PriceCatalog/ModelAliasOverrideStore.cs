namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One operator-authored override mapping an aggregator's own model naming onto a configured
/// <c>ModelRouting:ModelList</c> entry - the top rung of the §5.7 resolution ladder
/// (<see cref="ResolutionRung.OperatorOverride"/>), used when no automatic rung can resolve a model.
/// </summary>
/// <param name="SourceName">The aggregator source this override applies to (e.g. <c>LiteLLM</c>).</param>
/// <param name="AggregatorModelKey">The source's own model key this override matches, verbatim.</param>
/// <param name="ModelName">The client-facing <c>ModelRouting:ModelList[].ModelName</c> to resolve to.</param>
public sealed record ModelAliasOverride(string SourceName, string AggregatorModelKey, string ModelName);

/// <summary>
/// Persists and reads operator-authored price-override mappings (<see cref="ModelAliasOverride"/>), backing
/// <c>PUT/DELETE /admin/price-overrides</c>. Runtime-editable with no restart required, per
/// <c>docs/router/token-tracking-implementation-plan.md</c> Phase 3 §5.7.
/// </summary>
public sealed class ModelAliasOverrideStore
{
    private readonly PriceCatalogDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="ModelAliasOverrideStore"/> class.</summary>
    /// <param name="database">The catalog database the overrides are persisted in.</param>
    public ModelAliasOverrideStore(PriceCatalogDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Returns every configured override, ordered by source then aggregator model key.</summary>
    public IReadOnlyList<ModelAliasOverride> GetAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_name, aggregator_model_key, model_name
            FROM model_alias_overrides
            ORDER BY source_name, aggregator_model_key;
            """;

        var overrides = new List<ModelAliasOverride>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            overrides.Add(new ModelAliasOverride(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return overrides;
    }

    /// <summary>
    /// Returns the override for <paramref name="sourceName"/>/<paramref name="aggregatorModelKey"/>, or
    /// <see langword="null"/> when none is configured. Matching is case-insensitive on both key parts, since
    /// aggregator naming and source names elsewhere in this repository are compared the same way.
    /// </summary>
    public string? TryGetModelName(string sourceName, string aggregatorModelKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregatorModelKey);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model_name
            FROM model_alias_overrides
            WHERE source_name = $source COLLATE NOCASE AND aggregator_model_key = $key COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$key", aggregatorModelKey);

        return command.ExecuteScalar() as string;
    }

    /// <summary>Adds or replaces the override for <paramref name="sourceName"/>/<paramref name="aggregatorModelKey"/>.</summary>
    public void Upsert(string sourceName, string aggregatorModelKey, string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregatorModelKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO model_alias_overrides (source_name, aggregator_model_key, model_name)
            VALUES ($source, $key, $model)
            ON CONFLICT(source_name, aggregator_model_key) DO UPDATE SET model_name = excluded.model_name;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$key", aggregatorModelKey);
        command.Parameters.AddWithValue("$model", modelName);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes the override for <paramref name="sourceName"/>/<paramref name="aggregatorModelKey"/>.
    /// </summary>
    /// <returns><see langword="true"/> when a row was removed; <see langword="false"/> if none matched.</returns>
    public bool Remove(string sourceName, string aggregatorModelKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregatorModelKey);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM model_alias_overrides
            WHERE source_name = $source COLLATE NOCASE AND aggregator_model_key = $key COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$key", aggregatorModelKey);
        return command.ExecuteNonQuery() > 0;
    }
}
