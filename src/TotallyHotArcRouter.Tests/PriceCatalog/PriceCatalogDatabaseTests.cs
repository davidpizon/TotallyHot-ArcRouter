using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="PriceCatalogDatabase.EnsureCreated"/>.</summary>
public class PriceCatalogDatabaseTests
{
    [Fact]
    public void EnsureCreated_FirstCall_CreatesFileAndReportsItDidNotExist()
    {
        using var temp = new TempDatabase();

        var alreadyExisted = temp.Database.EnsureCreated();

        Assert.False(alreadyExisted);
        Assert.True(File.Exists(temp.Path_));
    }

    [Fact]
    public void EnsureCreated_SecondCall_IsNoOpAndReportsItAlreadyExisted()
    {
        using var temp = new TempDatabase();

        temp.Database.EnsureCreated();
        var alreadyExisted = temp.Database.EnsureCreated();

        Assert.True(alreadyExisted);
    }

    [Fact]
    public void EnsureCreated_CreatesAllSixTables()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();

        var tables = ReadTableNames(temp.Database);

        Assert.Contains(expected: "providers", collection: tables);
        Assert.Contains(expected: "models", collection: tables);
        Assert.Contains(expected: "model_aliases", collection: tables);
        Assert.Contains(expected: "aggregator_sources", collection: tables);
        Assert.Contains(expected: "model_prices", collection: tables);
        Assert.Contains(expected: "multimodal_prices", collection: tables);
        Assert.Contains(expected: "model_price_observations", collection: tables);
    }

    [Fact]
    public void EnsureCreated_OnAPreExistingDatabase_AddsObservationsTable_WithoutDisturbingModelPrices()
    {
        // model_price_observations is a new, purely additive table (CREATE TABLE IF NOT EXISTS) - an install
        // upgrading from before this feature existed must gain it on the next startup without losing
        // whatever model_prices already held.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceRepository(temp.Database);
        repository.UpsertPrices(sourceName: "litellm", 0,
            prices:
            [
                new NormalizedPrice(ModelIdentifier: "gpt-4o", Provider: "openai", 2.50m, 10.00m, null, null, null)
            ], asOfUtc: DateTimeOffset.UtcNow);

        temp.Database.EnsureCreated();

        Assert.Contains(expected: "model_price_observations", collection: ReadTableNames(temp.Database));
        var price = repository.GetFreshPrice(key: new ModelKey(ModelName: "gpt-4o", Provider: "openai"),
            maxAge: TimeSpan.FromHours(24));
        Assert.NotNull(price);
        Assert.Equal(2.50m, actual: price!.InputPerMillionTokens);
    }

    [Fact]
    public void EnsureCreated_CreatesUsageLedgerTableWithUniqueDedupIndex()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();

        Assert.Contains(expected: "usage_ledger", collection: ReadTableNames(temp.Database));

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_index_list('usage_ledger') WHERE name = 'ix_usage_ledger_dedup_key' AND \"unique\" = 1;";
        Assert.Equal(1, actual: Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void EnsureCreated_CreatesProviderCostReconciliationAndCheckpointTables()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();

        var tables = ReadTableNames(temp.Database);

        Assert.Contains(expected: "provider_cost_reconciliation", collection: tables);
        Assert.Contains(expected: "reconciliation_checkpoint", collection: tables);
    }

    [Fact]
    public void EnsureCreated_SeedsBothKnownSourcesEnabled()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();

        // Rows previously appeared only after a successful poll, which left a fresh install with nothing to
        // toggle and no way to switch a source off before the startup pull fired.
        var states = new PriceSourceRepository(temp.Database).GetSourceStates();
        Assert.Equal(2, actual: states.Count);
        Assert.All(collection: states, action: s => Assert.Equal(0, actual: s.PriceCount));
        Assert.All(collection: states, action: s => Assert.True(s.Enabled));
    }

    [Fact]
    public void EnsureCreated_SeedsLiteLlmAboveOpenRouterByDefault()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();

        var states = new PriceSourceRepository(temp.Database).GetSourceStates();
        var liteLlm = states.Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName);
        var openRouter = states.Single(s => s.Name == PriceCatalogOptions.OpenRouterSourceName);

        Assert.True(liteLlm.PriorityScore > openRouter.PriorityScore);
    }

    [Fact]
    public void EnsureCreated_DoesNotReEnableADisabledSource()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();
        repository.SetSourceEnabled(sourceName: PriceCatalogOptions.LiteLlmSourceName, false);

        // EnsureCreated runs on every startup. If the seed used DO UPDATE instead of DO NOTHING, an operator
        // who switched a source off would find it quietly back on after the next restart.
        temp.Database.EnsureCreated();

        var liteLlm = repository.GetSourceStates().Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName);
        Assert.False(liteLlm.Enabled);
    }

    [Fact]
    public void EnsureCreated_DoesNotDisturbAnOperatorSetPriority()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();
        Assert.True(repository.ReorderSources([
            PriceCatalogOptions.OpenRouterSourceName, PriceCatalogOptions.LiteLlmSourceName
        ]));

        // EnsureCreated runs on every startup, and its seed step must not rewrite a rank the operator (or a
        // previous reorder) already set - only ON CONFLICT DO NOTHING protects that.
        temp.Database.EnsureCreated();

        var states = repository.GetSourceStates();
        var openRouter = states.Single(s => s.Name == PriceCatalogOptions.OpenRouterSourceName);
        var liteLlm = states.Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName);
        Assert.True(openRouter.PriorityScore > liteLlm.PriorityScore);
    }

    [Fact]
    public void EnsureCreated_AddsEnabledColumnToAPreExistingTable_AndSeedsTheMissingKnownSource()
    {
        using var temp = new TempDatabase();
        CreateSchemaWithoutEnabledColumn(temp.Database);

        // CREATE TABLE IF NOT EXISTS won't add a column to a table that already exists, so without the
        // explicit migration every install predating this feature would fail on first read with
        // "no such column: enabled".
        temp.Database.EnsureCreated();

        var states = new PriceSourceRepository(temp.Database).GetSourceStates();
        var liteLlm = states.Single(s => s.Name == "litellm");
        Assert.True(condition: liteLlm.Enabled,
            userMessage: "an existing source must migrate to enabled, matching its pre-toggle behavior");

        // openrouter didn't exist in this simulated legacy database (it predates OpenRouter's client
        // entirely) - EnsureCreated's seed step must still add it, exactly as it would for any install
        // upgrading straight to this version.
        Assert.Contains(collection: states, filter: s => s.Name == PriceCatalogOptions.OpenRouterSourceName);
    }

    [Fact]
    public void EnsureCreated_AddsJsonSchemaColumnToAPreExistingCapabilitiesTable_AndBackfillsItFromTheNativeFlavors()
    {
        using var temp = new TempDatabase();
        CreateCapabilitiesSchemaWithoutJsonSchemaColumn(temp.Database);

        // Same CREATE TABLE IF NOT EXISTS blind spot as the `enabled` case above: this table already exists
        // on every install that ran a build with capability scanning but without constrained decoding, so
        // without the explicit migration the first capability read fails with
        // "no such column: json_schema_response_format".
        temp.Database.EnsureCreated();

        var stored = new ToolCallCapabilityRepository(temp.Database).GetProviderCapabilities();

        // Backfilled rather than left at the column default. A plain DEFAULT 0 would ship the fix switched
        // off for every existing install until somebody pressed "Refresh from endpoint" - i.e. off for
        // exactly the operators already hitting the bug.
        var lmStudio = stored.Single(c => c.ProviderKey == "lmstudio");
        Assert.True(lmStudio.JsonSchemaResponseFormat);
        Assert.True(condition: lmStudio.LmStudioNative,
            userMessage: "the pre-existing row's other flags must survive the migration");

        // A cloud provider speaking only OpenAI is deliberately not backfilled: that flag is true for every
        // one of them, and constrained mode exists for local models.
        Assert.False(stored.Single(c => c.ProviderKey == "openai").JsonSchemaResponseFormat);
    }

    // Reproduces the pre-migration schema: provider_endpoint_capabilities without
    // `json_schema_response_format`, holding a row the way a real install would have (written by a past
    // "Refresh from endpoint" scan).
    private static void CreateCapabilitiesSchemaWithoutJsonSchemaColumn(PriceCatalogDatabase database)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE provider_endpoint_capabilities (
                                  provider_key         TEXT PRIMARY KEY COLLATE NOCASE,
                                  openai_compatible    INTEGER NOT NULL DEFAULT 0,
                                  lmstudio_native      INTEGER NOT NULL DEFAULT 0,
                                  ollama_native        INTEGER NOT NULL DEFAULT 0,
                                  anthropic_compatible INTEGER NOT NULL DEFAULT 0,
                                  scanned_at_utc       TEXT    NOT NULL,
                                  scan_error           TEXT
                              );
                              INSERT INTO provider_endpoint_capabilities
                                  (provider_key, openai_compatible, lmstudio_native, ollama_native, anthropic_compatible, scanned_at_utc)
                              VALUES ('lmstudio', 1, 1, 0, 0, '2026-07-31T00:00:00.0000000Z'),
                                     ('openai',   1, 0, 0, 0, '2026-07-31T00:00:00.0000000Z');
                              """;
        command.ExecuteNonQuery();
    }

    // Reproduces the pre-migration schema: aggregator_sources without `enabled`, holding a row the way a
    // real install would have (written by a past ingestion cycle).
    private static void CreateSchemaWithoutEnabledColumn(PriceCatalogDatabase database)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE aggregator_sources (
                                  source_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                                  source_name    TEXT    NOT NULL UNIQUE,
                                  priority_score INTEGER NOT NULL DEFAULT 0
                              );
                              INSERT INTO aggregator_sources (source_name, priority_score) VALUES ('litellm', 0);
                              """;
        command.ExecuteNonQuery();
    }

    private static List<string> ReadTableNames(PriceCatalogDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));

        return names;
    }
}