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
        MigrateIsApproximateColumn(connection);
        MigrateBudgetWindowColumns(connection);
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

    // Same blind spot again: a database created before the §5.7 resolution ladder existed has model_prices
    // without this column. Existing rows read as 0 ("not an approximate match") - the correct default,
    // since every price stored before the ladder existed was resolved via the old exact-only rule.
    /// <summary>
    /// Adds the `is_approximate` column to `model_prices` if it is missing, so databases created before the
    /// resolution ladder existed pick it up on startup with existing rows reading as an exact (non-approximate)
    /// match.
    /// </summary>
    private static void MigrateIsApproximateColumn(SqliteConnection connection)
    {
        if (ColumnExists(connection, "model_prices", "is_approximate"))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE model_prices ADD COLUMN is_approximate INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
    }

    // Same blind spot again: a database created before per-provider budget windows existed (Phase 4, §5.10)
    // has provider_budgets without these columns. Existing rows read as 'Monthly' with a null hour count -
    // exactly today's hardcoded CurrentPeriod() behavior - so an upgraded install's caps keep resetting on
    // the same calendar-month boundary they always did, until an operator opts a provider into a different
    // window.
    /// <summary>
    /// Adds the `window_kind` and `window_hours` columns to `provider_budgets` if missing, so databases
    /// created before per-provider budget windows existed pick them up on startup with existing rows
    /// reading as the monthly window that was the only behavior before this column existed.
    /// </summary>
    private static void MigrateBudgetWindowColumns(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "provider_budgets", "window_kind"))
        {
            using var alterKind = connection.CreateCommand();
            alterKind.CommandText = "ALTER TABLE provider_budgets ADD COLUMN window_kind TEXT NOT NULL DEFAULT 'Monthly';";
            alterKind.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "provider_budgets", "window_hours"))
        {
            using var alterHours = connection.CreateCommand();
            alterHours.CommandText = "ALTER TABLE provider_budgets ADD COLUMN window_hours INTEGER;";
            alterHours.ExecuteNonQuery();
        }
    }

    // Every source with a client gets a row up front, so the GUI has something to toggle before the first
    // poll has ever run. Without this, rows only appear via PriceRepository.UpsertPrices - i.e. only
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
            is_approximate       INTEGER NOT NULL DEFAULT 0,
            UNIQUE(model_id, provider_id)
        );

        -- Every enabled source's own last-known price per (model, provider) cell, independent of which source
        -- currently wins. model_prices stores only the winner; this table is what lets RecomputeWinners()
        -- re-derive that winner from a new priority order without a live pull - a losing source's price would
        -- otherwise never be retained anywhere. aggregator_source_id joins into the uniqueness key (unlike
        -- model_prices' UNIQUE(model_id, provider_id)) so every source keeps its own row per cell.
        CREATE TABLE IF NOT EXISTS model_price_observations (
            observation_id          INTEGER PRIMARY KEY AUTOINCREMENT,
            model_id                INTEGER NOT NULL REFERENCES models(model_id),
            provider_id             INTEGER NOT NULL REFERENCES providers(provider_id),
            aggregator_source_id    INTEGER NOT NULL REFERENCES aggregator_sources(source_id),
            standard_input_price    REAL,
            standard_output_price   REAL,
            cached_input_price      REAL,
            cache_write_input_price REAL,
            batch_input_price       REAL,
            batch_output_price      REAL,
            last_updated_utc        TEXT NOT NULL,
            is_approximate          INTEGER NOT NULL DEFAULT 0,
            UNIQUE(model_id, provider_id, aggregator_source_id)
        );

        -- Operator-authored overrides for the §5.7 resolution ladder's top rung (ResolutionRung.OperatorOverride):
        -- the recourse an operator has when no automatic rung resolves a model. Keyed on (source_name,
        -- aggregator_model_key) - the same aggregator naming UpsertPrices already resolves per row - mapping
        -- onto the client-facing ModelName from ModelRouting:ModelList; the entry's Provider is looked up from
        -- that ModelName at resolve time (ModelName is unique - see ConfigModelIdentityResolver.Build), so this
        -- table does not duplicate a Provider column that config edits could drift out of sync with. Managed at
        -- runtime via PUT/DELETE /admin/price-overrides, no restart required.
        CREATE TABLE IF NOT EXISTS model_alias_overrides (
            source_name          TEXT NOT NULL COLLATE NOCASE,
            aggregator_model_key TEXT NOT NULL COLLATE NOCASE,
            model_name           TEXT NOT NULL,
            PRIMARY KEY (source_name, aggregator_model_key)
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
        -- (provider, minute bucket, header) is kept - see RateLimitRepository.UpsertRateLimitHeaders -
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

        -- Anthropic's own reported per-model daily token usage (docs/router/secrets-at-rest-plan.md §8.1),
        -- fetched via GET /v1/organizations/usage_report/messages - a distinct, more privileged Admin API
        -- key than the provider's per-request inference key. One row per (provider, day, model); refreshed
        -- wholesale on every hourly reconciliation cycle (AnthropicUsageReportService), which re-fetches the
        -- full trailing 30-day window and upserts every row rather than tracking a checkpoint, so a value
        -- Anthropic revises after the fact (their docs note usage can settle over ~5 minutes, occasionally
        -- longer) is picked up automatically rather than frozen at first fetch. Raw reported token counts
        -- are stored; totals are derived at read time (ManagementFacade), never pre-summed here.
        CREATE TABLE IF NOT EXISTS provider_reported_usage_snapshot (
            provider_key          TEXT    NOT NULL,
            usage_day             TEXT    NOT NULL,
            model                 TEXT    NOT NULL,
            input_tokens          INTEGER NOT NULL,
            output_tokens         INTEGER NOT NULL,
            cache_creation_tokens INTEGER NOT NULL,
            cache_read_tokens     INTEGER NOT NULL,
            fetched_at_utc        TEXT    NOT NULL,
            PRIMARY KEY (provider_key, usage_day, model)
        );

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

        -- Probed per-model context windows, reported through Ollama's POST /api/show model_info
        -- (docs/router/ollama-show-capabilities-plan.md). Keyed identically to model_tool_capabilities
        -- above and filled by the same endpoint scan, which makes the separate table look redundant - it
        -- is not. The two have different write lifecycles: ToolCallObservationRecorder rewrites a
        -- capability row from the request path with no context length to supply, and ClearModelCapability
        -- DELETEs one on an operator dialect reset. Either would silently destroy a probed window if it
        -- shared the row. See docs/adr/0002-store-probed-model-context-windows-in-their-own-table.md.
        --
        -- No Migrate* helper accompanies this table, unlike most of its neighbours. Those exist only
        -- because CREATE TABLE IF NOT EXISTS cannot add a *column* to a table that already exists; a new
        -- table has no such blind spot, so this one statement covers fresh and upgraded databases alike.
        CREATE TABLE IF NOT EXISTS model_context_windows (
            provider_key    TEXT    NOT NULL COLLATE NOCASE,
            model_name      TEXT    NOT NULL COLLATE NOCASE,
            context_length  INTEGER NOT NULL,
            architecture    TEXT,
            evidence        TEXT,
            detected_at_utc TEXT    NOT NULL,
            PRIMARY KEY(provider_key, model_name)
        );

        -- The durable usage ledger (docs/router/token-tracking-implementation-plan.md Phase 2, §5.2):
        -- every priced/unpriced request's usage, surviving process restarts (unlike the in-memory-only
        -- telemetry stream). dedup_key is either the upstream provider's own request id (preferred) or a
        -- composite hash over the request's identity, so replaying an already-recorded request (a restart
        -- racing an in-flight write, a retried publish) is a no-op via INSERT ... ON CONFLICT DO NOTHING
        -- rather than a double count. estimated_cost_usd is TEXT (decimal-as-string), matching every other
        -- money column in this database - see UsageLedger. cost_confidence is TEXT holding a
        -- Telemetry.CostConfidence enum member's name (via Enum.ToString()), e.g. "Unknown" or "Catalog".
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

        -- Pre-aggregated time buckets over usage_ledger (Phase 4, §5.3): each ledger entry contributes one
        -- increment to the PT30M/PT1H/P1D row for its (provider, model), so the Model Distribution and Cost
        -- Analytics GUI tabs can read a bounded-size table instead of scanning the full ledger on every
        -- render. WITHOUT ROWID because the natural key is already unique and covers every query shape this
        -- table serves. cost_usd is TEXT for the same invariant-decimal reason as every other money column
        -- here; unpriced_requests tracks how many of `requests` had a null cost (§5.6), so a summary can
        -- render "≥ $4.10 · 3 unpriced" instead of silently treating unknown as zero.
        CREATE TABLE IF NOT EXISTS usage_rollup (
            bucket_start_utc      TEXT    NOT NULL,
            bucket_width          TEXT    NOT NULL,
            provider               TEXT    NOT NULL,
            model                  TEXT    NOT NULL,
            requests               INTEGER NOT NULL DEFAULT 0,
            unpriced_requests      INTEGER NOT NULL DEFAULT 0,
            prompt_tokens          INTEGER NOT NULL DEFAULT 0,
            completion_tokens      INTEGER NOT NULL DEFAULT 0,
            cache_creation_tokens  INTEGER NOT NULL DEFAULT 0,
            cache_read_tokens      INTEGER NOT NULL DEFAULT 0,
            cost_usd               TEXT    NOT NULL DEFAULT '0',
            PRIMARY KEY (bucket_start_utc, bucket_width, provider, model)
        ) WITHOUT ROWID;

        -- Single-row table (id is CHECK-constrained to 1) holding the rollup subsystem's two pieces of
        -- durable state: the write-once bucket timezone (tokscale's reproducible-bucket rule - pinning this
        -- once means two reports generated a month apart over the same past day still agree, since the day
        -- boundary itself never moves) and the backfill checkpoint (the highest usage_ledger.entry_id
        -- already folded into usage_rollup, so a startup backfill after downtime resumes from where it left
        -- off instead of re-scanning the whole ledger).
        CREATE TABLE IF NOT EXISTS rollup_metadata (
            id                             INTEGER PRIMARY KEY CHECK (id = 1),
            bucket_timezone_iana_id        TEXT    NOT NULL,
            bucket_timezone_pinned_at_utc  TEXT    NOT NULL,
            last_rolled_up_entry_id        INTEGER NOT NULL DEFAULT 0
        );

        -- Periodic snapshots comparing a provider's own reported spend against usage_ledger's local
        -- estimate for the same window (Phase 6, §5.8): the "did our per-request cost model actually
        -- match the provider's bill" check. One row per (provider, window) reconciliation run - never
        -- overwritten, so a history of drift over time is preserved rather than only the latest snapshot.
        -- Money columns are TEXT (decimal-as-string), matching every other money column in this database.
        -- scope_note records the org-vs-proxy caveat (agent-cost-tracking.md §3.5/§5.8): the provider's
        -- reported figure is org-wide (every key under that Admin API key), while local_estimated_cost_usd
        -- is only what this proxy instance routed - the two can legitimately differ even with a perfect
        -- price table if other traffic shares the same organization.
        CREATE TABLE IF NOT EXISTS provider_cost_reconciliation (
            id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            provider                  TEXT    NOT NULL,
            window_start_utc          TEXT    NOT NULL,
            window_end_utc            TEXT    NOT NULL,
            provider_reported_cost_usd TEXT   NOT NULL,
            local_estimated_cost_usd  TEXT    NOT NULL,
            scope_note                TEXT    NOT NULL,
            fetched_at_utc            TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_provider_cost_reconciliation_provider_window
            ON provider_cost_reconciliation (provider, window_start_utc);

        -- The reconciliation checkpoint cursor (§5.8's "checkpoint the cursor" honeycomb discipline): the
        -- most recent fully-closed day already reconciled per provider, so a restart resumes from there
        -- instead of re-reconciling (and re-calling the provider's billing API for) days already done.
        CREATE TABLE IF NOT EXISTS reconciliation_checkpoint (
            provider              TEXT PRIMARY KEY,
            last_reconciled_day   TEXT NOT NULL
        );
        """;
}

