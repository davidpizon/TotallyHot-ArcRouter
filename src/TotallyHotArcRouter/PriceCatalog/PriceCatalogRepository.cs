using System.Globalization;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One source's row in <c>aggregator_sources</c>, joined with a summary of the price rows it owns. Feed
/// metadata only - no price values, per D5.
/// </summary>
/// <param name="Name">The source's registry name.</param>
/// <param name="Enabled">Whether the source is polled and served (D6).</param>
/// <param name="PriorityScore">Rank used to arbitrate contested cells. [FUTURE: multi-source]</param>
/// <param name="PriceCount">How many price rows this source owns; 0 for a seeded source that never polled.</param>
public sealed record PriceSourceState(
    string Name,
    bool Enabled,
    int PriorityScore,
    int PriceCount);

/// <summary>
/// A provider's persisted monthly budget caps, keyed on the provider key used in model-routing.json / the
/// <c>/admin</c> API. A <see langword="null"/> cap means "no budget for that dimension" - distinct from a
/// zero cap.
/// </summary>
/// <param name="ProviderKey">The provider key (e.g. <c>openai</c>).</param>
/// <param name="DollarCap">The monthly USD cap, or <see langword="null"/> for no dollar budget.</param>
/// <param name="TokenCap">The monthly total-token cap, or <see langword="null"/> for no token budget.</param>
public sealed record ProviderBudgetRow(string ProviderKey, decimal? DollarCap, long? TokenCap);

/// <summary>
/// A provider's spend accumulated within one <c>YYYY-MM</c> period. Tokens are the sum of prompt and
/// completion tokens billed to the provider that actually served each request.
/// </summary>
/// <param name="ProviderKey">The provider key.</param>
/// <param name="CostUsd">Total estimated USD spent in the period.</param>
/// <param name="PromptTokens">Total prompt tokens (raw, post-cache-breakpoint <c>input_tokens</c>) in the period.</param>
/// <param name="CompletionTokens">Total completion tokens in the period.</param>
/// <param name="CacheCreationTokens">Total cache-creation (cache-write) input tokens in the period.</param>
/// <param name="CacheReadTokens">Total cache-read input tokens in the period.</param>
/// <param name="LastUsageAtUtc">
/// The UTC instant the most recent usage in this period was recorded, or <see langword="null"/> if no
/// usage has been recorded for this period yet.
/// </param>
public sealed record ProviderSpendRow(
    string ProviderKey,
    decimal CostUsd,
    long PromptTokens,
    long CompletionTokens,
    long CacheCreationTokens = 0,
    long CacheReadTokens = 0,
    DateTimeOffset? LastUsageAtUtc = null);

/// <summary>
/// Thin ADO.NET wrapper over the price catalog tables. Reads and writes are keyed <c>(model, provider)</c>
/// per D7. Prices are stored as USD per 1,000,000 tokens (D2); timestamps as round-trip UTC ISO 8601, so
/// the 24h freshness floor (D1) is an ordinary lexicographic comparison.
/// </summary>
public sealed class PriceCatalogRepository
{
    // Round-trip UTC ISO 8601 (e.g. 2026-07-16T12:34:56.7890000Z). Fixed length and lexicographically
    // ordered, so freshness comparisons need no date parsing in SQL.
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private readonly PriceCatalogDatabase _database;
    private readonly IModelIdentityResolver? _identityResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceCatalogRepository"/> class.
    /// </summary>
    /// <param name="database">The catalog database.</param>
    /// <param name="identityResolver">
    /// Optional D3 alias resolver mapping each source's own model/provider naming onto the configured router
    /// identity at ingest (see <c>docs/router/d3-alias-resolution.md</c>). When <see langword="null"/>, prices
    /// are stored under each source's own keys verbatim - the pre-D3 behavior - so existing callers and tests
    /// are unaffected.
    /// </param>
    public PriceCatalogRepository(PriceCatalogDatabase database, IModelIdentityResolver? identityResolver = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _identityResolver = identityResolver;
    }

    /// <summary>
    /// Upserts a batch of normalized prices from one source in a single transaction. The conflict target
    /// is the <c>(model, provider)</c> cell (D7). Whether a given row actually overwrites an existing one is
    /// gated by <c>priority_score</c>: the incoming source's rank must be greater than or equal to the
    /// incumbent's, or the write is silently skipped rather than clobbering a better-ranked source's number
    /// based on nothing but poll timing. Missing providers, models, and the source itself are created on
    /// demand.
    /// </summary>
    /// <returns>
    /// The number of price rows actually written - i.e. that passed the priority gate - not the number
    /// attempted. With one enabled source this is always every row in <paramref name="prices"/>: a source
    /// compared against its own previous row always satisfies "greater than or equal".
    /// </returns>
    public int UpsertPrices(
        string sourceName,
        int priorityScore,
        IReadOnlyList<NormalizedPrice> prices,
        DateTimeOffset asOfUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(prices);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var sourceId = GetOrCreateSourceId(connection, transaction, sourceName, priorityScore);
        var timestamp = asOfUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        var written = 0;
        foreach (var price in prices)
        {
            // D3 alias resolution: map the source's own (model, provider) naming onto the configured router
            // identity, so two sources naming one real model differently (LiteLLM "gpt-4o", OpenRouter
            // "openai/gpt-4o") collide into a single (ModelName, provider) cell - which is what exercises the
            // priority gate below and lets the runtime cost lookup resolve on ModelName. A miss falls back to
            // the source's own keys verbatim: an unmatched model is left unresolved, never mis-mapped. See
            // docs/router/d3-alias-resolution.md.
            var identity = _identityResolver?.Resolve(price.ModelIdentifier, price.Provider);
            var providerName = identity?.Provider ?? price.Provider;
            var modelIdentifier = identity?.ModelName ?? price.ModelIdentifier;

            var providerId = GetOrCreateProviderId(connection, transaction, providerName);
            var modelId = GetOrCreateModelId(connection, transaction, modelIdentifier);

            // The alias records the source's own name against whatever internal model id it resolved to (the
            // configured ModelName on a hit, the raw key on a miss). Written unconditionally, even when the
            // priority gate below rejects the price row - a losing source's naming of a model is still a fact
            // worth keeping, and the alias table has no priority concept of its own.
            UpsertAlias(connection, transaction, sourceId, modelId, price.ModelIdentifier);
            written += UpsertPriceRow(connection, transaction, modelId, providerId, sourceId, price, timestamp);
        }

        transaction.Commit();
        return written;
    }

    /// <summary>
    /// Counts price rows whose newest update is within <paramref name="maxAge"/> of now, ignoring rows
    /// owned by a disabled source. Used by the startup check's zero-fresh-prices condition (D4).
    /// </summary>
    /// <remarks>
    /// The <c>enabled</c> join is what keeps this honest with D6: a disabled source's rows are excluded from
    /// the resolved catalog, so counting them here would let a source the operator switched off suppress the
    /// zero-fresh-prices Error - reporting a healthy feed while nothing usable is being served.
    /// </remarks>
    public int CountFreshPrices(TimeSpan maxAge)
    {
        var cutoff = FormatCutoff(maxAge);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM model_prices mp
            JOIN aggregator_sources s ON s.source_id = mp.aggregator_source_id
            WHERE mp.last_updated_utc >= $cutoff
              AND s.enabled = 1;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns one row per source in <c>aggregator_sources</c>, with its toggle state, rank, and how many
    /// price rows it owns. Backs the Governance → Price Sources panel.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> filtered by <c>enabled</c>: this describes the sources themselves, and a
    /// disabled source still has to be listed - otherwise it could never be switched back on. That is the
    /// opposite of <see cref="GetFreshPrice"/>, which describes the resolved catalog and must exclude it.
    /// <para>
    /// Carries no price values, only a count - see D5. The GUI renders this straight onto the wire, so a
    /// price column added here would leave the machine.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PriceSourceState> GetSourceStates()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        // LEFT JOIN, not JOIN: a seeded source that has never polled owns no price rows, and it must still
        // appear (with a count of 0) so the operator can see it exists and toggle it.
        command.CommandText = """
            SELECT s.source_name, s.enabled, s.priority_score,
                   COUNT(mp.price_id)
            FROM aggregator_sources s
            LEFT JOIN model_prices mp ON mp.aggregator_source_id = s.source_id
            GROUP BY s.source_id, s.source_name, s.enabled, s.priority_score
            ORDER BY s.priority_score DESC, s.source_name;
            """;

        var states = new List<PriceSourceState>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            states.Add(new PriceSourceState(
                Name: reader.GetString(0),
                Enabled: reader.GetInt32(1) != 0,
                PriorityScore: reader.GetInt32(2),
                PriceCount: reader.GetInt32(3)));
        }

        return states;
    }

    /// <summary>
    /// Sets a source's <c>enabled</c> flag.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when no source of that name exists, so a caller can answer NotFound rather
    /// than silently reporting success for a toggle that changed nothing.
    /// </returns>
    public bool SetSourceEnabled(string sourceName, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE aggregator_sources SET enabled = $enabled WHERE source_name = $name;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$name", sourceName);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Rewrites every source's <c>priority_score</c> from <paramref name="namesInPriorityOrder"/>'s position:
    /// the first name gets the highest score, the last gets the lowest, contiguously. Backs the Governance
    /// panel's reorder control.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> without writing anything when <paramref name="namesInPriorityOrder"/> does not
    /// name every existing source exactly once - a partial or padded reorder would leave an unlisted source's
    /// rank stale relative to ranks that just moved, which is a worse failure than simply rejecting the call.
    /// </returns>
    public bool ReorderSources(IReadOnlyList<string> namesInPriorityOrder)
    {
        ArgumentNullException.ThrowIfNull(namesInPriorityOrder);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = "SELECT source_name FROM aggregator_sources;";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = existingCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    existing.Add(reader.GetString(0));
                }
            }

            var requested = new HashSet<string>(namesInPriorityOrder, StringComparer.OrdinalIgnoreCase);
            if (requested.Count != namesInPriorityOrder.Count || !requested.SetEquals(existing))
            {
                // Rolling back an otherwise-empty transaction is a no-op; explicit for readability at the
                // point every other return path here does write.
                transaction.Rollback();
                return false;
            }
        }

        // Highest priority first in the list, so the first entry gets the largest score. count-1 down to 0
        // keeps scores contiguous and non-negative regardless of what the previous ranking happened to be -
        // a fresh, deterministic ordering rather than shuffling old values.
        for (var index = 0; index < namesInPriorityOrder.Count; index++)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE aggregator_sources SET priority_score = $priority WHERE source_name = $name;";
            update.Parameters.AddWithValue("$priority", namesInPriorityOrder.Count - index - 1);
            update.Parameters.AddWithValue("$name", namesInPriorityOrder[index]);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Reads every provider that has a persisted budget row. Providers with no row (the default) are absent
    /// from the result rather than returned with null caps, so the caller can treat "no row" and "row with
    /// both caps null" identically as unbudgeted.
    /// </summary>
    public IReadOnlyList<ProviderBudgetRow> GetProviderBudgets()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider_key, dollar_cap, token_cap FROM provider_budgets;";

        var rows = new List<ProviderBudgetRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ProviderBudgetRow(
                ProviderKey: reader.GetString(0),
                DollarCap: reader.IsDBNull(1) ? null : decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                TokenCap: reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return rows;
    }

    /// <summary>
    /// Persists a provider's monthly budget caps. A <see langword="null"/> cap clears that dimension; when
    /// both are null the row is deleted, so an unbudgeted provider leaves no stale row behind. Decimals are
    /// written as invariant text (matching how money round-trips elsewhere) so no binary-float rounding
    /// enters a dollar cap.
    /// </summary>
    public void SetProviderBudget(string providerKey, decimal? dollarCap, long? tokenCap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        if (dollarCap is null && tokenCap is null)
        {
            command.CommandText = "DELETE FROM provider_budgets WHERE provider_key = $key;";
            command.Parameters.AddWithValue("$key", providerKey);
            command.ExecuteNonQuery();
            return;
        }

        command.CommandText = """
            INSERT INTO provider_budgets (provider_key, dollar_cap, token_cap)
            VALUES ($key, $dollar, $token)
            ON CONFLICT(provider_key) DO UPDATE SET
                dollar_cap = excluded.dollar_cap,
                token_cap  = excluded.token_cap;
            """;
        command.Parameters.AddWithValue("$key", providerKey);
        command.Parameters.AddWithValue("$dollar", dollarCap?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$token", tokenCap ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns every provider's accumulated spend for the given <c>YYYY-MM</c> <paramref name="period"/>.
    /// Providers with no usage in the period are absent.
    /// </summary>
    public IReadOnlyList<ProviderSpendRow> GetProviderSpend(string period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(period);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_key, cost_usd, prompt_tokens, completion_tokens,
                   cache_creation_tokens, cache_read_tokens, last_usage_at
            FROM provider_spend
            WHERE period = $period;
            """;
        command.Parameters.AddWithValue("$period", period);

        var rows = new List<ProviderSpendRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ProviderSpendRow(
                ProviderKey: reader.GetString(0),
                CostUsd: decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                PromptTokens: reader.GetInt64(2),
                CompletionTokens: reader.GetInt64(3),
                CacheCreationTokens: reader.GetInt64(4),
                CacheReadTokens: reader.GetInt64(5),
                LastUsageAtUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6))));
        }

        return rows;
    }

    /// <summary>
    /// Adds one request's usage to a provider's spend for the given <c>YYYY-MM</c> <paramref name="period"/>,
    /// creating the period row on first use. The cost is accumulated as a true decimal via read-modify-write
    /// inside a transaction (not SQL float arithmetic) so a month of small per-request costs doesn't drift.
    /// Cache token columns accumulate via SQL <c>+</c>, like <paramref name="promptTokens"/>/
    /// <paramref name="completionTokens"/>. <paramref name="usageAtUtc"/> only ever advances the stored
    /// <c>last_usage_at</c> (a SQL <c>MAX</c> against the existing value) rather than overwriting it
    /// unconditionally, so an out-of-order call - concurrent requests completing in a different order than
    /// they're recorded, a clock adjustment, delayed telemetry - can't move it backwards. Relies on
    /// <see cref="TimestampFormat"/> being fixed-width, so lexicographic <c>TEXT</c> comparison agrees with
    /// chronological order.
    /// </summary>
    public void AddProviderSpend(
        string providerKey,
        string period,
        decimal costUsd,
        long promptTokens,
        long completionTokens,
        long cacheCreationTokens,
        long cacheReadTokens,
        DateTimeOffset usageAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(period);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        decimal existingCost = 0m;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT cost_usd FROM provider_spend WHERE provider_key = $key AND period = $period;";
            read.Parameters.AddWithValue("$key", providerKey);
            read.Parameters.AddWithValue("$period", period);
            if (read.ExecuteScalar() is string existing)
            {
                existingCost = decimal.Parse(existing, CultureInfo.InvariantCulture);
            }
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO provider_spend (
                    provider_key, period, cost_usd, prompt_tokens, completion_tokens,
                    cache_creation_tokens, cache_read_tokens, last_usage_at)
                VALUES ($key, $period, $cost, $prompt, $completion, $cacheCreation, $cacheRead, $usageAt)
                ON CONFLICT(provider_key, period) DO UPDATE SET
                    cost_usd              = $cost,
                    prompt_tokens         = prompt_tokens + excluded.prompt_tokens,
                    completion_tokens     = completion_tokens + excluded.completion_tokens,
                    cache_creation_tokens = cache_creation_tokens + excluded.cache_creation_tokens,
                    cache_read_tokens     = cache_read_tokens + excluded.cache_read_tokens,
                    last_usage_at         = MAX(COALESCE(last_usage_at, ''), excluded.last_usage_at);
                """;
            upsert.Parameters.AddWithValue("$key", providerKey);
            upsert.Parameters.AddWithValue("$period", period);
            upsert.Parameters.AddWithValue("$cost", (existingCost + costUsd).ToString(CultureInfo.InvariantCulture));
            upsert.Parameters.AddWithValue("$prompt", promptTokens);
            upsert.Parameters.AddWithValue("$completion", completionTokens);
            upsert.Parameters.AddWithValue("$cacheCreation", cacheCreationTokens);
            upsert.Parameters.AddWithValue("$cacheRead", cacheReadTokens);
            upsert.Parameters.AddWithValue("$usageAt", usageAtUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Returns the standard-rate price for a <c>(model, provider)</c> key only when it was fetched within
    /// <paramref name="maxAge"/> - the query D1's 24h routing floor reads. Returns <see langword="null"/>
    /// when the row is stale, absent, or has no standard rates; all three mean <em>unpriced</em> and a
    /// caller must treat them identically.
    /// </summary>
    /// <remarks>
    /// Not called by anything in this slice yet - it exists now so a future <c>UtilityRoutingPolicy</c>
    /// can honor D1 without another schema or repository pass. A free provider's zero does not come from
    /// here (see <c>ProviderOptions.IsFree</c>); this returns <see langword="null"/> for such a model.
    /// <para>
    /// A row owned by a <em>disabled</em> source is excluded outright, which is D6's "neither polled nor
    /// served" half: a model priced only by a disabled source becomes unpriced the moment the operator
    /// switches it off, rather than continuing to steer routing until its rows age out 24h later. Note this
    /// is not the same as a source that merely <em>failed</em> - stale rows from a failing source are still
    /// trusted for display, because "we couldn't refresh this" is a different claim from "stop using this".
    /// </para>
    /// </remarks>
    public ModelPrice? GetFreshPrice(ModelKey key, TimeSpan maxAge)
    {
        var cutoff = FormatCutoff(maxAge);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mp.standard_input_price, mp.standard_output_price,
                   mp.cached_input_price, mp.cache_write_input_price
            FROM model_prices mp
            JOIN models            m ON m.model_id    = mp.model_id
            JOIN providers         p ON p.provider_id = mp.provider_id
            JOIN aggregator_sources s ON s.source_id  = mp.aggregator_source_id
            WHERE m.model_identifier = $model
              AND p.provider_name    = $provider
              AND mp.last_updated_utc >= $cutoff
              AND s.enabled = 1;
            """;
        command.Parameters.AddWithValue("$model", key.ModelName);
        command.Parameters.AddWithValue("$provider", key.Provider);
        command.Parameters.AddWithValue("$cutoff", cutoff);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }

        return new ModelPrice(
            reader.GetDecimal(0),
            reader.GetDecimal(1),
            reader.IsDBNull(2) ? null : reader.GetDecimal(2),
            reader.IsDBNull(3) ? null : reader.GetDecimal(3));
    }

    /// <summary>
    /// Formats the UTC timestamp that is <paramref name="maxAge"/> before now, for use as a freshness cutoff
    /// in a SQL comparison.
    /// </summary>
    private static string FormatCutoff(TimeSpan maxAge) =>
        (DateTimeOffset.UtcNow - maxAge).UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a round-trip UTC timestamp written in <see cref="TimestampFormat"/> back into a
    /// <see cref="DateTimeOffset"/>.
    /// </summary>
    private static DateTimeOffset ParseTimestamp(string value) =>
        new(
            DateTime.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            TimeSpan.Zero);

    /// <summary>
    /// Returns the id of the aggregator source row for <paramref name="sourceName"/>, inserting it with
    /// <paramref name="priorityScore"/> if it does not already exist. An existing row's priority_score and
    /// enabled flag are never overwritten, since those are operator-owned.
    /// </summary>
    private static int GetOrCreateSourceId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceName,
        int priorityScore)
    {
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        // The conflict branch is a deliberate no-op write (the same trick GetOrCreateProviderId uses to make
        // RETURNING fire on an existing row) rather than an update of priority_score. Both priority_score and
        // enabled are operator-owned: they are seeded once by PriceCatalogDatabase.EnsureCreated and changed
        // only through the Governance panel. Writing them back here would mean every poll silently reset the
        // operator's choice to whatever default the ingestion loop happened to pass in.
        upsert.CommandText = """
            INSERT INTO aggregator_sources (source_name, priority_score)
            VALUES ($name, $priority)
            ON CONFLICT(source_name) DO UPDATE SET source_name = excluded.source_name
            RETURNING source_id;
            """;
        upsert.Parameters.AddWithValue("$name", sourceName);
        upsert.Parameters.AddWithValue("$priority", priorityScore);
        return Convert.ToInt32(upsert.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the id of the provider row for <paramref name="providerName"/>, inserting it if it does not
    /// already exist.
    /// </summary>
    private static int GetOrCreateProviderId(SqliteConnection connection, SqliteTransaction transaction, string providerName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO providers (provider_name) VALUES ($name)
            ON CONFLICT(provider_name) DO UPDATE SET provider_name = excluded.provider_name
            RETURNING provider_id;
            """;
        command.Parameters.AddWithValue("$name", providerName);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the id of the model row for <paramref name="modelIdentifier"/>, inserting it if it does not
    /// already exist.
    /// </summary>
    private static int GetOrCreateModelId(SqliteConnection connection, SqliteTransaction transaction, string modelIdentifier)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO models (model_identifier) VALUES ($id)
            ON CONFLICT(model_identifier) DO UPDATE SET model_identifier = excluded.model_identifier
            RETURNING model_id;
            """;
        command.Parameters.AddWithValue("$id", modelIdentifier);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Inserts or updates the aggregator alias mapping (source, aggregator name) to <paramref name="modelId"/>,
    /// so a later lookup by the aggregator's own name for that source resolves to this model.
    /// </summary>
    private static void UpsertAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int sourceId,
        int modelId,
        string aggregatorName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO model_aliases (model_id, source_id, aggregator_name)
            VALUES ($model, $source, $name)
            ON CONFLICT(source_id, aggregator_name) DO UPDATE SET model_id = excluded.model_id;
            """;
        command.Parameters.AddWithValue("$model", modelId);
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$name", aggregatorName);
        command.ExecuteNonQuery();
    }

    // Returns 1 when the row was inserted or the priority gate let it overwrite the incumbent, 0 when a
    // conflicting row exists and the gate rejected the write (SQLite's ON CONFLICT ... WHERE leaves the row
    // untouched rather than erroring, so a rejection is silent unless the caller checks affected rows - which
    // is exactly what the return value here is for).
    /// <summary>
    /// Inserts or updates the price row for (model, provider), gated by source priority: a conflicting
    /// existing row is only overwritten when the incoming source's priority score is greater than or equal
    /// to the incumbent's. Returns 1 if the row was written, 0 if the priority gate rejected the write.
    /// </summary>
    private static int UpsertPriceRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int modelId,
        int providerId,
        int sourceId,
        NormalizedPrice price,
        string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The WHERE clause is the priority gate: it compares the incoming row's source rank against the
        // CURRENT incumbent's (model_prices.aggregator_source_id, read before this statement's own update -
        // SQLite evaluates ON CONFLICT's WHERE against the pre-update row), both resolved by a subquery into
        // aggregator_sources since priority_score lives there, not on model_prices itself. ">=" rather than
        // ">" deliberately: equal ranks must still let a source refresh its own previous row, which is the
        // ordinary "no other source involved" case this whole gate has to keep working correctly.
        //
        // On a fresh INSERT (no conflict) this WHERE is never evaluated - there is no "incumbent" to compare
        // against - so a first-ever row for a cell is always written regardless of rank.
        command.CommandText = """
            INSERT INTO model_prices (
                model_id, provider_id, aggregator_source_id,
                standard_input_price, standard_output_price, cached_input_price, cache_write_input_price,
                batch_input_price, batch_output_price, last_updated_utc)
            VALUES (
                $model, $provider, $source,
                $stdIn, $stdOut, $cachedIn, $cacheWrite,
                $batchIn, $batchOut, $updated)
            ON CONFLICT(model_id, provider_id) DO UPDATE SET
                aggregator_source_id    = excluded.aggregator_source_id,
                standard_input_price    = excluded.standard_input_price,
                standard_output_price   = excluded.standard_output_price,
                cached_input_price      = excluded.cached_input_price,
                cache_write_input_price = excluded.cache_write_input_price,
                batch_input_price       = excluded.batch_input_price,
                batch_output_price      = excluded.batch_output_price,
                last_updated_utc        = excluded.last_updated_utc
            WHERE (SELECT priority_score FROM aggregator_sources WHERE source_id = excluded.aggregator_source_id)
                  >= (SELECT priority_score FROM aggregator_sources WHERE source_id = model_prices.aggregator_source_id);
            """;
        command.Parameters.AddWithValue("$model", modelId);
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$stdIn", (object?)price.StandardInputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("$stdOut", (object?)price.StandardOutputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("$cachedIn", (object?)price.CachedInputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("$cacheWrite", (object?)price.CacheWriteInputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("$batchIn", (object?)price.BatchInputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("$batchOut", (object?)price.BatchOutputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", timestamp);
        return command.ExecuteNonQuery();
    }

    // Minute-bucketed history is pruned back to this window on every write (TokenTracker's bounded-growth
    // lesson from the plan) - opportunistic, not a scheduled job, so an install that stops receiving
    // rate-limit headers simply stops growing the table rather than needing a cleanup task.
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// Upserts one provider's captured <c>anthropic-ratelimit-*</c> response headers into the latest-value
    /// snapshot, and records at most one row per (provider, minute bucket, header) into the history table -
    /// a second capture within the same minute for a header already recorded this minute is a no-op there,
    /// enforced atomically by a unique index plus <c>INSERT ... ON CONFLICT DO NOTHING</c> so concurrent
    /// captures can't race past a check-then-insert and both land a row. A no-op when
    /// <paramref name="headers"/> is empty. Also prunes history rows older than 30 days.
    /// </summary>
    public void UpsertRateLimitHeaders(string providerKey, IReadOnlyList<RateLimitHeaderRow> headers, DateTimeOffset observedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.Count == 0)
        {
            return;
        }

        var observedAt = observedAtUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var minuteBucket = observedAtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        var historyCutoff = (observedAtUtc - HistoryRetention).UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var header in headers)
        {
            using (var snapshot = connection.CreateCommand())
            {
                snapshot.Transaction = transaction;
                snapshot.CommandText = """
                    INSERT INTO provider_rate_limit_snapshot (provider_key, header_name, header_value, observed_at)
                    VALUES ($key, $name, $value, $observed)
                    ON CONFLICT(provider_key, header_name) DO UPDATE SET
                        header_value = excluded.header_value,
                        observed_at  = excluded.observed_at;
                    """;
                snapshot.Parameters.AddWithValue("$key", providerKey);
                snapshot.Parameters.AddWithValue("$name", header.HeaderName.ToLowerInvariant());
                snapshot.Parameters.AddWithValue("$value", header.HeaderValue);
                snapshot.Parameters.AddWithValue("$observed", observedAt);
                snapshot.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO provider_rate_limit_history (provider_key, minute_bucket, header_name, header_value)
                    VALUES ($key, $bucket, $name, $value)
                    ON CONFLICT(provider_key, minute_bucket, header_name) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$key", providerKey);
                insert.Parameters.AddWithValue("$bucket", minuteBucket);
                insert.Parameters.AddWithValue("$name", header.HeaderName.ToLowerInvariant());
                insert.Parameters.AddWithValue("$value", header.HeaderValue);
                insert.ExecuteNonQuery();
            }
        }

        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM provider_rate_limit_history WHERE minute_bucket < $cutoff;";
            prune.Parameters.AddWithValue("$cutoff", historyCutoff);
            prune.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Returns a provider's latest captured rate-limit header snapshot, along with the most recent
    /// <c>observed_at</c> among its rows. Returns an empty header list and a <see langword="null"/> instant
    /// when no header has ever been captured for this provider.
    /// </summary>
    public (IReadOnlyList<RateLimitHeaderRow> Headers, DateTimeOffset? ObservedAtUtc) GetRateLimitSnapshot(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT header_name, header_value, observed_at
            FROM provider_rate_limit_snapshot
            WHERE provider_key = $key;
            """;
        command.Parameters.AddWithValue("$key", providerKey);

        var rows = new List<RateLimitHeaderRow>();
        DateTimeOffset? latest = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RateLimitHeaderRow(reader.GetString(0), reader.GetString(1)));
            var observedAt = ParseTimestamp(reader.GetString(2));
            if (latest is null || observedAt > latest)
            {
                latest = observedAt;
            }
        }

        return (rows, latest);
    }
}

