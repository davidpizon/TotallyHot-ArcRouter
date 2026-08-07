using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Owns the SQLite connection string and schema for the price catalog. Creates the file, its directory,
/// and every table on first run (idempotently): the six price-catalog tables plus the per-provider
/// <c>provider_budgets</c> / <c>provider_spend</c> tables backing the Governance budget bars. The tables
/// share <c>agent_telemetry.db</c> with the (future) usage ledger, so naming stays <c>snake_case</c> to
/// match <c>docs/router/agent-cost-tracking.md</c> §2.
/// </summary>
/// <remarks>
/// This is the first database in the repository - nothing used <c>Microsoft.Data.Sqlite</c> before. The
/// price columns are keyed <c>(model, provider)</c> per D7: the same model served by two providers is
/// two rows, and whether a caching/batch discount exists at all is a provider fact carried by a nullable
/// column. WAL journaling plus <c>Synchronous=Normal</c> lets Phase 4 reads run concurrently with
/// ingestion writes.
/// </remarks>
public sealed class PriceCatalogDatabase
{
    private readonly string _databasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceCatalogDatabase"/> class.
    /// </summary>
    public PriceCatalogDatabase(IOptions<StorageOptions> storageOptions)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        _databasePath = storageOptions.Value.ResolveDatabasePath();
    }

    /// <summary>Gets the resolved absolute path of the database file.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>Gets the connection string that opens (creating if needed) the database file.</summary>
    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    /// <summary>
    /// Opens a connection to the database. The caller owns disposal.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Ensures the database file, its directory, and all tables exist, applies any additive
    /// column migrations, and seeds a row for every source that has a client. Idempotent: a second call on
    /// an existing file changes nothing.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the file already existed before this call (so the caller can log "found
    /// existing" vs "created new" - startup check 1); <see langword="false"/> if it was just created.
    /// </returns>
    public bool EnsureCreated()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var alreadyExisted = File.Exists(_databasePath);

        using var connection = OpenConnection();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = SchemaSql;
            schema.ExecuteNonQuery();
        }

        MigrateEnabledColumn(connection);
        MigrateJsonSchemaResponseFormatColumn(connection);
        MigrateCacheWriteInputPriceColumn(connection);
        MigrateProviderSpendCacheColumns(connection);
        SeedKnownSources(connection);

        return alreadyExisted;
    }

    // CREATE TABLE IF NOT EXISTS silently does nothing when the table already exists, so it cannot add a
    // column to a database created before `enabled` was part of the schema. Without this, every install
    // predating the Governance price-source panel would fail on first read with "no such column". There is
    // no migration framework in this repo, so the check is an explicit PRAGMA. Defaulting to 1 lands
    // existing installs enabled, exactly how they behaved before the toggle existed.
    /// <summary>
    /// Adds the `enabled` column to `aggregator_sources` if it is missing, so databases created before the
    /// column existed pick it up on startup, defaulting existing rows to enabled.
    /// </summary>
    private static void MigrateEnabledColumn(SqliteConnection connection)
    {
        if (ColumnExists(connection, "aggregator_sources", "enabled"))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE aggregator_sources ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;";
        alter.ExecuteNonQuery();
    }

    // Same CREATE TABLE IF NOT EXISTS blind spot as MigrateEnabledColumn above: every install that ran a
    // build with tool-call capability scanning but without constrained decoding already has this table, so
    // the column has to be added explicitly or the first capability read fails with "no such column".
    //
    // The backfill matters as much as the column. A plain DEFAULT 0 is *technically* safe but practically
    // wrong: every existing install would read as "schema not supported" and keep using the unreliable
    // prompt-only path until somebody happened to press "Refresh from endpoint" - so the fix would ship
    // switched off for exactly the operators already hitting the bug. The UPDATE applies the same rule
    // ProviderEndpointScanner applies to a fresh scan, so it is a re-derivation from data already on disk
    // rather than a guess: these two flavors are the ones that back response_format with real
    // grammar-constrained sampling.
    /// <summary>
    /// Adds the `json_schema_response_format` column to `provider_endpoint_capabilities` if it is missing
    /// and backfills it from the already-recorded native flavors, so databases created before constrained
    /// tool calling existed pick the feature up on startup rather than after a manual rescan.
    /// </summary>
    private static void MigrateJsonSchemaResponseFormatColumn(SqliteConnection connection)
    {
        if (ColumnExists(connection, "provider_endpoint_capabilities", "json_schema_response_format"))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = """
            ALTER TABLE provider_endpoint_capabilities ADD COLUMN json_schema_response_format INTEGER NOT NULL DEFAULT 0;
            UPDATE provider_endpoint_capabilities
               SET json_schema_response_format = 1
             WHERE lmstudio_native = 1 OR ollama_native = 1;
            """;
        alter.ExecuteNonQuery();
    }

    // Same CREATE TABLE IF NOT EXISTS blind spot as the migrations above: a database created before cache-aware
    // pricing existed has model_prices without this column, so the first read/write after upgrade would fail
    // with "no such column". Existing rows read as NULL - "no published cache-write rate" - which is the correct
    // "absent, not zero" meaning (D7) until the next successful poll of a source that publishes one.
    /// <summary>
    /// Adds the `cache_write_input_price` column to `model_prices` if it is missing, so databases created
    /// before cache-aware pricing existed pick it up on startup with existing rows reading as an unpublished
    /// (<see langword="null"/>) rate.
    /// </summary>
    private static void MigrateCacheWriteInputPriceColumn(SqliteConnection connection)
    {
        if (ColumnExists(connection, "model_prices", "cache_write_input_price"))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE model_prices ADD COLUMN cache_write_input_price REAL;";
        alter.ExecuteNonQuery();
    }

    // Same blind spot again, applied to provider_spend: a database created before cache-aware usage tracking
    // existed has none of these three columns. Existing rows read as 0 tokens / NULL timestamp - "no cache
    // usage recorded yet" / "no usage recorded yet" - which is what an install with no cache-using traffic
    // since upgrade should show, not an error.
    /// <summary>
    /// Adds the `cache_creation_tokens`, `cache_read_tokens`, and `last_usage_at` columns to `provider_spend`
    /// if missing, so databases created before cache-aware usage tracking existed pick them up on startup with
    /// existing rows reading as zero cache tokens and no last-usage timestamp.
    /// </summary>
    private static void MigrateProviderSpendCacheColumns(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "provider_spend", "cache_creation_tokens"))
        {
            using var alterCreation = connection.CreateCommand();
            alterCreation.CommandText = "ALTER TABLE provider_spend ADD COLUMN cache_creation_tokens INTEGER NOT NULL DEFAULT 0;";
            alterCreation.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "provider_spend", "cache_read_tokens"))
        {
            using var alterRead = connection.CreateCommand();
            alterRead.CommandText = "ALTER TABLE provider_spend ADD COLUMN cache_read_tokens INTEGER NOT NULL DEFAULT 0;";
            alterRead.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "provider_spend", "last_usage_at"))
        {
            using var alterLastUsage = connection.CreateCommand();
            alterLastUsage.CommandText = "ALTER TABLE provider_spend ADD COLUMN last_usage_at TEXT;";
            alterLastUsage.ExecuteNonQuery();
        }
    }

    // Every source with a client gets a row up front, so the GUI has something to toggle before the first
    // poll has ever run. Without this, rows only appear via PriceCatalogRepository.UpsertPrices - i.e. only
    // after a *successful* fetch - which would leave a fresh install with an empty panel and no way to
    // disable a source before the startup pull fires.
    //
    // DO NOTHING (rather than DO UPDATE) is load-bearing: this runs on every startup, and an operator who
    // disabled a source must not find it silently re-enabled on the next launch.
    /// <summary>
    /// Inserts a row for every known aggregator source that does not already exist, so the GUI has rows to
    /// toggle before the first poll runs; existing rows are left untouched via DO NOTHING.
    /// </summary>
    private static void SeedKnownSources(SqliteConnection connection)
    {
        foreach (var source in PriceCatalogOptions.KnownSources)
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO aggregator_sources (source_name, priority_score, enabled)
                VALUES ($name, $priority, $enabled)
                ON CONFLICT(source_name) DO NOTHING;
                """;
            seed.Parameters.AddWithValue("$name", source.Name);
            seed.Parameters.AddWithValue("$priority", source.DefaultPriorityScore);
            seed.Parameters.AddWithValue("$enabled", source.DefaultEnabled ? 1 : 0);
            seed.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Checks whether the given column exists on the given table by querying SQLite's pragma_table_info.
    /// </summary>
    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        // PRAGMA does not accept a bound parameter for the table name. Every caller passes a compile-time
        // constant, so interpolation here is not an injection surface.
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    // The six Phase 1 catalog tables plus the two per-provider budget/spend tables. `IF NOT EXISTS`
    // throughout makes EnsureCreated idempotent. model_prices and
    // multimodal_prices are keyed (model, provider) via a UNIQUE constraint - the ON CONFLICT target the
    // repository upserts against (D7). aggregator_source_id is carried for lineage on every price row; its
    // priority_score column ships populated-and-ignored (ranking is [FUTURE: multi-source]).
    //
    // aggregator_sources.enabled is the source of truth for D6's per-source toggle - it is not mirrored in
    // configuration, and nothing reads a config key for it. Any change to this column must go through
    // PriceSourceToggleStore, which owns the in-memory cache and the in-flight fetch cancellation.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS providers (
            provider_id   INTEGER PRIMARY KEY AUTOINCREMENT,
            provider_name TEXT    NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS models (
            model_id         INTEGER PRIMARY KEY AUTOINCREMENT,
            model_identifier TEXT    NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS aggregator_sources (
            source_id      INTEGER PRIMARY KEY AUTOINCREMENT,
            source_name    TEXT    NOT NULL UNIQUE,
            priority_score INTEGER NOT NULL DEFAULT 0,
            enabled        INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS model_aliases (
            alias_id        INTEGER PRIMARY KEY AUTOINCREMENT,
            model_id        INTEGER NOT NULL REFERENCES models(model_id),
            source_id       INTEGER NOT NULL REFERENCES aggregator_sources(source_id),
            aggregator_name TEXT    NOT NULL,
            UNIQUE(source_id, aggregator_name)
        );

        CREATE TABLE IF NOT EXISTS model_prices (
            price_id             INTEGER PRIMARY KEY AUTOINCREMENT,
            model_id             INTEGER NOT NULL REFERENCES models(model_id),
            provider_id          INTEGER NOT NULL REFERENCES providers(provider_id),
            aggregator_source_id INTEGER NOT NULL REFERENCES aggregator_sources(source_id),
            standard_input_price  REAL,
            standard_output_price REAL,
            cached_input_price    REAL,
            cache_write_input_price REAL,
            batch_input_price     REAL,
            batch_output_price    REAL,
            last_updated_utc     TEXT NOT NULL,
            source_raw_payload   TEXT,
            UNIQUE(model_id, provider_id)
        );

        CREATE TABLE IF NOT EXISTS multimodal_prices (
            multimodal_price_id  INTEGER PRIMARY KEY AUTOINCREMENT,
            model_id             INTEGER NOT NULL REFERENCES models(model_id),
            provider_id          INTEGER NOT NULL REFERENCES providers(provider_id),
            aggregator_source_id INTEGER NOT NULL REFERENCES aggregator_sources(source_id),
            resolution_tier      TEXT NOT NULL,
            per_step_cost        REAL,
            base_image_cost      REAL,
            last_updated_utc     TEXT NOT NULL,
            UNIQUE(model_id, provider_id, resolution_tier)
        );

        -- Per-provider monthly budget caps set from the Governance > Providers panel. Keyed on the
        -- provider *key* (as used in model-routing.json / the /admin API), not the catalog's numeric
        -- provider_id, because a budget can exist for a provider the price catalog has never seen. A NULL
        -- cap means "no budget for that dimension" - distinct from a zero cap. Decimals are stored as text
        -- (invariant round-trip) exactly like the REAL/text money columns elsewhere are read back through
        -- the repository, so no binary-float rounding creeps into a dollar figure.
        CREATE TABLE IF NOT EXISTS provider_budgets (
            provider_key TEXT PRIMARY KEY,
            dollar_cap   TEXT,
            token_cap    INTEGER
        );

        -- Per-provider spend accumulated within a calendar month. `period` is "YYYY-MM"; the current
        -- month's row is the running total, and month rollover is implicit (a new period key = a fresh
        -- row starting at zero), so "monthly auto-reset" needs no scheduled job. Written on the telemetry
        -- side-effect path (ProviderBudgetStore.RecordUsageAsync), read by the budget bars and by routing
        -- enforcement.
        CREATE TABLE IF NOT EXISTS provider_spend (
            provider_key        TEXT    NOT NULL,
            period              TEXT    NOT NULL,
            cost_usd            TEXT    NOT NULL DEFAULT '0',
            prompt_tokens       INTEGER NOT NULL DEFAULT 0,
            completion_tokens   INTEGER NOT NULL DEFAULT 0,
            cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
            cache_read_tokens   INTEGER NOT NULL DEFAULT 0,
            last_usage_at       TEXT,
            PRIMARY KEY(provider_key, period)
        );

        -- Which API flavors a provider's endpoint actually answers, discovered by probing well-known
        -- paths when the provider is added or rescanned (docs/router/tool-call-normalization.md §3.3).
        -- Routing still speaks OpenAI-compatible only; the other flavors are recorded because they expose
        -- richer model metadata (Ollama's /api/show returns the literal chat template), which is what
        -- tier-1 dialect detection reads. Keyed on the provider *key* rather than the catalog's numeric
        -- provider_id, for the same reason provider_budgets is: a provider can be scanned long before the
        -- price catalog has ever seen it. COLLATE NOCASE matches the OrdinalIgnoreCase comparers used for
        -- provider keys everywhere in the routing path - without it "openai" and "OpenAI" would be two
        -- rows on disk but one entry in the in-memory cache.
        CREATE TABLE IF NOT EXISTS provider_endpoint_capabilities (
            provider_key         TEXT PRIMARY KEY COLLATE NOCASE,
            openai_compatible    INTEGER NOT NULL DEFAULT 0,
            lmstudio_native      INTEGER NOT NULL DEFAULT 0,
            ollama_native        INTEGER NOT NULL DEFAULT 0,
            anthropic_compatible INTEGER NOT NULL DEFAULT 0,
            -- Whether the endpoint honors response_format json_schema, which is what makes the
            -- `constrained` tool-call dialect usable. Defaults to 0 so a row written before this column
            -- existed reads as "not supported" - the safe direction, since it falls back to prompt-taught
            -- emulation rather than attaching a schema an unknown server might reject.
            json_schema_response_format INTEGER NOT NULL DEFAULT 0,
            scanned_at_utc       TEXT    NOT NULL,
            scan_error           TEXT
        );

        -- One row per (provider, model) recording how that model expresses a tool call, so the normalizing
        -- translator knows which dialect to arm - or that it must not arm at all. Keyed on (provider, model)
        -- because a dialect is a property of the loaded model's chat template, not of the server hosting it:
        -- one LM Studio process serves models that disagree. `confidence` gates overwrites (see
        -- ToolCallCapabilityStore.TryRecordModelCapability); `evidence` is a short pattern description, never
        -- raw model output. Same COLLATE NOCASE reasoning as above, applied to both key columns since model
        -- names are matched case-insensitively by ModelRouteResolver too.
        -- Latest observed value per (provider, header): the GUI's "as of" reported-usage view (Governance >
        -- Providers > Anthropic Usage). Upserted on every captured response, so a read always reflects the
        -- single most recent value per header regardless of how many responses have been captured.
        -- header_name is stored lowercase (headers are case-insensitive); header_value is stored verbatim -
        -- Anthropic rounds `remaining` to the nearest thousand and the unified family's values are opaque
        -- strings, so interpretation belongs at read time (see RateLimitSnapshotParser), not storage.
        CREATE TABLE IF NOT EXISTS provider_rate_limit_snapshot (
            provider_key TEXT NOT NULL,
            header_name  TEXT NOT NULL,
            header_value TEXT NOT NULL,
            observed_at  TEXT NOT NULL,
            PRIMARY KEY (provider_key, header_name)
        );

        -- Minute-bucketed history of the same headers, for future trend charts. At most one row per
        -- (provider, minute bucket, header) is kept - see PriceCatalogRepository.UpsertRateLimitHeaders -
        -- so a provider proxying steady traffic doesn't grow this table once per request.
        CREATE TABLE IF NOT EXISTS provider_rate_limit_history (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            provider_key  TEXT NOT NULL,
            minute_bucket TEXT NOT NULL,
            header_name   TEXT NOT NULL,
            header_value  TEXT NOT NULL
        );

        -- Backs the dedupe in UpsertRateLimitHeaders with a single atomic `INSERT OR IGNORE` instead of a
        -- check-then-insert, so concurrent captures for the same (provider, minute, header) can't both
        -- observe "no row" and both insert.
        CREATE UNIQUE INDEX IF NOT EXISTS ix_provider_rate_limit_history_dedupe
            ON provider_rate_limit_history (provider_key, minute_bucket, header_name);

        CREATE TABLE IF NOT EXISTS model_tool_capabilities (
            provider_key      TEXT    NOT NULL COLLATE NOCASE,
            model_name        TEXT    NOT NULL COLLATE NOCASE,
            dialect           TEXT    NOT NULL,
            confidence        TEXT    NOT NULL,
            evidence          TEXT,
            observation_count INTEGER NOT NULL DEFAULT 0,
            detected_at_utc   TEXT    NOT NULL,
            PRIMARY KEY(provider_key, model_name)
        );

        -- The durable usage ledger (docs/router/token-tracking-implementation-plan.md Phase 2, §5.2):
        -- every priced/unpriced request's usage, surviving process restarts (unlike the in-memory-only
        -- telemetry stream). dedup_key is either the upstream provider's own request id (preferred) or a
        -- composite hash over the request's identity, so replaying an already-recorded request (a restart
        -- racing an in-flight write, a retried publish) is a no-op via INSERT ... ON CONFLICT DO NOTHING
        -- rather than a double count. estimated_cost_usd is TEXT (decimal-as-string), matching every other
        -- money column in this database - see UsageLedger. cost_confidence is written "Unknown" until
        -- Phase 3's confidence enum lands.
        CREATE TABLE IF NOT EXISTS usage_ledger (
            entry_id              INTEGER PRIMARY KEY AUTOINCREMENT,
            dedup_key             TEXT    NOT NULL,
            session_id            TEXT    NOT NULL,
            turn_number           INTEGER NOT NULL,
            provider              TEXT    NOT NULL,
            requested_model       TEXT    NOT NULL,
            resolved_model        TEXT    NOT NULL,
            prompt_tokens         INTEGER,
            completion_tokens     INTEGER,
            cache_creation_tokens INTEGER,
            cache_read_tokens     INTEGER,
            estimated_cost_usd    TEXT,
            cost_confidence       TEXT    NOT NULL,
            occurred_at_utc       TEXT    NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_usage_ledger_dedup_key ON usage_ledger (dedup_key);
        CREATE INDEX IF NOT EXISTS ix_usage_ledger_occurred_at ON usage_ledger (occurred_at_utc);
        CREATE INDEX IF NOT EXISTS ix_usage_ledger_provider_model_time
            ON usage_ledger (provider, requested_model, occurred_at_utc);
        """;
}

