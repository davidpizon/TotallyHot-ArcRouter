using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Issue #304 phase 5: non-code Complete rows enter kNN (not live tallies), disputed writes are
/// skipped at insert time, and unknown cost stays null through a nullable <c>memory_entries.cost</c>
/// rebuild.
/// </summary>
public sealed class GradeNonCodingPhase5Tests
{
    [Fact]
    public async Task CompleteNonCode_WritesKnnAndLeavesLiveTalliesUnchanged()
    {
        using var harness = Harness.Create();
        const string correlationId = "sess:prose";
        await harness.SeedCompleteReceiptAsync(
            correlationId: correlationId,
            unifiedScore: 0.88,
            sessionId: "sess",
            dimension: string.Empty,
            recipeWeightsVersion: GradingRecipes.Bootstrap);

        harness.SeedLiveTally(dimension: "live:code_generation", model: "model-a", count: 3);
        var tallyBefore = harness.ReadLiveCount(dimension: "live:code_generation", model: "model-a");
        Assert.Equal(3, tallyBefore);

        var pendingCache = CreatePendingCache();
        pendingCache.Set(correlationId, [0.1f, 0.2f]);
        var pendingCost = CreatePendingCostCache();
        pendingCost.Set(correlationId, 0.0042m);
        var observer = new EmbeddingMemoryScoreObserver(
            memory: harness.Memory,
            pendingCache: pendingCache,
            pendingCostCache: pendingCost,
            pendingProvenanceCache: CreatePendingProvenanceCache(),
            logger: NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var routerMemory = new RouterMemory();
        await routerMemory.AddScoreAsync(dimension: "live:code_generation", model: "model-a", score: 0.9);
        var scoreObserver = new RouterMemoryScoreObserver(
            memory: routerMemory,
            options: Options.Create(new QualityOptions()),
            logger: NullLogger<RouterMemoryScoreObserver>.Instance);

        var result = new QualityResult
        {
            RequestCorrelationId = correlationId,
            Model = "model-a",
            Dimension = string.Empty,
            UnifiedScore = 0.88,
            GradeStatus = GradeStatus.Complete,
            RecipeId = GradingRecipes.Bootstrap,
            JudgeScore = 0.88
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);
        await scoreObserver.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.CountMemoryByCorrelation(correlationId));
        Assert.Equal(3, harness.ReadLiveCount(dimension: "live:code_generation", model: "model-a"));
        // Bootstrap Complete must not touch dim_best / live tallies (carries PR #334 forward).
        Assert.Equal(0.9, routerMemory.GetAverageScore(dimension: "live:code_generation", model: "model-a"));
        Assert.Empty(routerMemory.GetModelsForDimension("live:"));
    }

    [Fact]
    public async Task DisputeThenAppend_WritesNoMemoryRow()
    {
        using var harness = Harness.Create();
        const string correlationId = "sess:disputed-write";
        await harness.SeedCompleteReceiptAsync(correlationId, unifiedScore: 0.9, sessionId: "sess");

        var dispute = harness.Dispute.ApplySignal(
            correlationId: correlationId,
            userSignal: UserSignals.AgentSwitch,
            contradicts: true);
        Assert.True(dispute.Disputed);

        var store = new SqliteMemoryEntryStore(harness.Database, NullLogger<SqliteMemoryEntryStore>.Instance);
        var written = await store.AppendAsync(
            entry: new MemoryEntry(
                0,
                TaskEmbedding: [0.1f, 0.2f],
                ChosenModel: "model-a",
                Score: 0.9,
                Cost: 0.01,
                VerifierTrace: null,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: correlationId),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(written);
        Assert.Equal(0, harness.CountMemoryByCorrelation(correlationId));
    }

    [Fact]
    public async Task CostNullableRebuild_PreservesRowCountAndValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "thr-304-p5-rebuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "router.db");

        try
        {
            SeedLegacyNotNullCostDatabase(dbPath);

            var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = dbPath
            }));
            database.EnsureCreated();

            var store = new SqliteMemoryEntryStore(database);
            var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(0.0, Assert.Single(loaded, e => e.ChosenModel == "zero-cost").Cost);
            Assert.Equal(0.0042, Assert.Single(loaded, e => e.ChosenModel == "priced").Cost!.Value, 6);
            Assert.Equal(new[] { 0.1f, 0.2f }, Assert.Single(loaded, e => e.ChosenModel == "priced").TaskEmbedding);

            // Second EnsureCreated is a no-op for the rebuild.
            database.EnsureCreated();
            var again = await store.LoadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, again.Count);
            Assert.Equal(0.0, Assert.Single(again, e => e.ChosenModel == "zero-cost").Cost);
        }
        finally
        {
            try
            {
                Directory.Delete(path: directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task AppendAsync_NullCost_ReadsBackBlankNotZero()
    {
        using var harness = Harness.Create();
        var store = new SqliteMemoryEntryStore(harness.Database);
        await store.AppendAsync(
            entry: new MemoryEntry(
                0,
                TaskEmbedding: [1f, 0f],
                ChosenModel: "unknown-cost",
                Score: 0.5,
                Cost: null,
                VerifierTrace: null,
                CreatedAtUtc: DateTimeOffset.UtcNow),
            cancellationToken: TestContext.Current.CancellationToken);

        var entry = Assert.Single(await store.LoadAllAsync(TestContext.Current.CancellationToken));
        Assert.Null(entry.Cost);
    }

    private static void SeedLegacyNotNullCostDatabase(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE memory_entries (
                                  id               INTEGER PRIMARY KEY AUTOINCREMENT,
                                  embedding        BLOB    NOT NULL,
                                  chosen_model     TEXT    NOT NULL,
                                  score            REAL    NOT NULL,
                                  cost             REAL    NOT NULL,
                                  verifier_trace   TEXT    NULL,
                                  created_at_utc   TEXT    NOT NULL,
                                  is_exploratory   INTEGER NOT NULL DEFAULT 0,
                                  propensity       REAL    NOT NULL DEFAULT 1.0,
                                  dimension        TEXT    NULL,
                                  is_judge_scored  INTEGER NOT NULL DEFAULT 0,
                                  embedding_model  TEXT    NULL,
                                  correlation_id   TEXT    NULL
                              );
                              CREATE TABLE schema_meta (
                                  key   TEXT PRIMARY KEY,
                                  value TEXT NOT NULL
                              );
                              INSERT INTO schema_meta (key, value) VALUES
                                  ('issue-293-live-learning-purge', 'done'),
                                  ('issue-319-untrusted-judge-provenance-purge', 'done');
                              """;
        command.ExecuteNonQuery();

        InsertLegacyRow(connection, model: "priced", cost: 0.0042, embedding: [0.1f, 0.2f]);
        InsertLegacyRow(connection, model: "zero-cost", cost: 0.0, embedding: [0.3f, 0.4f]);
    }

    private static void InsertLegacyRow(SqliteConnection connection, string model, double cost, float[] embedding)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO memory_entries
                                  (embedding, chosen_model, score, cost, created_at_utc, is_exploratory, propensity, is_judge_scored)
                              VALUES ($embedding, $model, 0.5, $cost, $created, 0, 1.0, 0);
                              """;
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(src: embedding, 0, dst: bytes, 0, count: bytes.Length);
        command.Parameters.AddWithValue("$embedding", bytes);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$cost", cost);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static PendingValueCache<float[]> CreatePendingCache() =>
        new(Options.Create(new RoutingOptions
        {
            PendingEmbeddingCacheCapacity = 16,
            PendingEmbeddingCacheTtlSeconds = 300
        }));

    private static PendingValueCache<decimal> CreatePendingCostCache() =>
        new(Options.Create(new RoutingOptions
        {
            PendingEmbeddingCacheCapacity = 16,
            PendingEmbeddingCacheTtlSeconds = 300
        }));

    private static PendingValueCache<PendingRequestProvenance> CreatePendingProvenanceCache() =>
        new(Options.Create(new RoutingOptions
        {
            PendingEmbeddingCacheCapacity = 16,
            PendingEmbeddingCacheTtlSeconds = 300
        }));

    private sealed class Harness : IDisposable
    {
        private readonly string _directory;

        private Harness(
            string directory,
            RouterMemoryDatabase database,
            SqliteGradeReceiptStore receipts,
            EmbeddingMemory memory,
            GradeDisputeService dispute)
        {
            _directory = directory;
            Database = database;
            Receipts = receipts;
            Memory = memory;
            Dispute = dispute;
        }

        public RouterMemoryDatabase Database { get; }
        private SqliteGradeReceiptStore Receipts { get; }
        public EmbeddingMemory Memory { get; }
        public GradeDisputeService Dispute { get; }

        public static Harness Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "thr-304-p5-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(directory, "router.db")
            }));
            database.EnsureCreated();
            var receipts = new SqliteGradeReceiptStore(database);
            var entryStore = new SqliteMemoryEntryStore(database, NullLogger<SqliteMemoryEntryStore>.Instance);
            var memory = new EmbeddingMemory(
                store: entryStore,
                optionsMonitor: new StaticOptionsMonitor(new RoutingOptions { EmbeddingMemoryCapacity = 100 }),
                embeddingClient: new FixedEmbedder([0.1f, 0.2f]),
                logger: NullLogger<EmbeddingMemory>.Instance);
            memory.InitializeAsync().GetAwaiter().GetResult();
            var dispute = new GradeDisputeService(
                database,
                memory,
                NullLogger<GradeDisputeService>.Instance);
            return new Harness(directory, database, receipts, memory, dispute);
        }

        public async Task SeedCompleteReceiptAsync(
            string correlationId,
            double unifiedScore,
            string sessionId,
            string dimension = "code_generation",
            string recipeWeightsVersion = "3.0-judge-heavy")
        {
            await Receipts.UpsertAsync(new GradeReceiptRecord(
                CorrelationId: correlationId,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                Dimension: dimension,
                Model: "model-a",
                GradeStatus: GradeStatus.Complete,
                UnifiedScore: unifiedScore,
                QuestionHmac: "abc",
                QuestionChars: 10,
                ContextMessages: 1,
                QuestionTruncated: false,
                FingerprintRef: null,
                ScorerVersion: "3.0",
                PromptVersion: "p1",
                WeightsVersion: recipeWeightsVersion,
                AbstainedGraders: string.Empty,
                AnswerModel: "model-a",
                SessionId: sessionId,
                SpotCheck: false,
                ResponseLengthChars: 42));
        }

        public void SeedLiveTally(string dimension, string model, int count)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO dimension_scores (dimension, model, sum, count)
                                  VALUES ($dimension, $model, $sum, $count);
                                  """;
            command.Parameters.AddWithValue("$dimension", dimension);
            command.Parameters.AddWithValue("$model", model);
            command.Parameters.AddWithValue("$sum", 0.9 * count);
            command.Parameters.AddWithValue("$count", count);
            command.ExecuteNonQuery();
        }

        public int ReadLiveCount(string dimension, string model)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  SELECT count FROM dimension_scores
                                  WHERE dimension = $dimension AND model = $model;
                                  """;
            command.Parameters.AddWithValue("$dimension", dimension);
            command.Parameters.AddWithValue("$model", model);
            var scalar = command.ExecuteScalar();
            return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar);
        }

        public int CountMemoryByCorrelation(string correlationId)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM memory_entries WHERE correlation_id = $id;";
            command.Parameters.AddWithValue("$id", correlationId);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public void Dispose()
        {
            Memory.Dispose();
            try
            {
                Directory.Delete(path: _directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FixedEmbedder(float[] vector) : IEmbeddingClient
    {
        public string ModelIdentity => "fixed";

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult(vector, TokenCount: 1));
    }

    private sealed class StaticOptionsMonitor(RoutingOptions value) : IOptionsMonitor<RoutingOptions>
    {
        public RoutingOptions CurrentValue => value;

        public RoutingOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RoutingOptions, string?> listener) => null;
    }
}
