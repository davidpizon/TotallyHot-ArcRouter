using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Issue #304 phase 4 named dispute-delete tests from the plan §4 Done when.
/// </summary>
public sealed class GradeDisputePhase4Tests
{
    [Fact]
    public async Task Dispute_DeletesEmbeddingAndResponseVector_WhenBothExist()
    {
        using var harness = Harness.Create();
        const string correlationId = "sess:both";
        await harness.SeedCompleteReceiptAsync(correlationId, unifiedScore: 0.9, sessionId: "sess");
        await harness.SeedMemoryEntryAsync(correlationId);
        await harness.SeedResponseVectorAsync(correlationId);
        harness.SeedLiveTally(dimension: "code_generation", model: "model-a", count: 1);

        Assert.Equal(1, harness.CountMemoryByCorrelation(correlationId));
        Assert.Equal(1, harness.ReadLiveCount(dimension: "code_generation", model: "model-a"));

        var result = harness.Dispute.ApplySignal(
            correlationId: correlationId,
            userSignal: UserSignals.AgentSwitch,
            contradicts: true);

        Assert.True(result.Disputed);
        Assert.True(result.ResponseVectorDeleted);
        Assert.True(result.KnnDeleted);

        var receipt = await harness.Receipts.TryGetAsync(correlationId);
        Assert.NotNull(receipt);
        Assert.Equal(GradeStatus.Incomplete, receipt.GradeStatus);
        Assert.Equal(IncompleteReasons.Disputed, receipt.IncompleteReason);
        Assert.Equal(UserSignals.AgentSwitch, receipt.UserSignal);
        Assert.Null(harness.Vectors.ReadSealed(correlationId));
        Assert.Equal(0, harness.CountMemoryByCorrelation(correlationId));
        Assert.Equal(1, harness.ReadLiveCount(dimension: "code_generation", model: "model-a"));

        Assert.Contains(harness.Logger.Messages, m =>
            m.Contains("response_vector_deleted", StringComparison.OrdinalIgnoreCase) &&
            m.Contains("True", StringComparison.Ordinal) &&
            m.Contains("knn_deleted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dispute_DeletesResponseVectorOnly_WhenNoKnnRow()
    {
        using var harness = Harness.Create();
        const string correlationId = "sess:vector-only";
        await harness.SeedCompleteReceiptAsync(correlationId, unifiedScore: 0.2, sessionId: "sess");
        await harness.SeedResponseVectorAsync(correlationId);

        var result = harness.Dispute.ApplySignal(
            correlationId: correlationId,
            userSignal: UserSignals.Thanks,
            contradicts: true);

        Assert.True(result.Disputed);
        Assert.True(result.ResponseVectorDeleted);
        Assert.False(result.KnnDeleted);

        var receipt = await harness.Receipts.TryGetAsync(correlationId);
        Assert.NotNull(receipt);
        Assert.Equal(IncompleteReasons.Disputed, receipt.IncompleteReason);
        Assert.Null(harness.Vectors.ReadSealed(correlationId));
        Assert.Equal(0, harness.CountMemoryByCorrelation(correlationId));

        Assert.Contains(harness.Logger.Messages, m =>
            m.Contains("response_vector_deleted", StringComparison.OrdinalIgnoreCase) &&
            m.Contains("knn_deleted", StringComparison.OrdinalIgnoreCase) &&
            m.Contains("False", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("thanks")]
    [InlineData("Thank you!")]
    [InlineData("that worked.")]
    [InlineData("THAT WORKS")]
    public void ThanksDetector_MatchesShortVocabulary(string message) =>
        Assert.True(UserGradeFeedback.IsThanks(message));

    [Fact]
    public void ThanksDetector_RejectsLongOrUnrelated()
    {
        Assert.False(UserGradeFeedback.IsThanks("thanks for the detailed explanation of the algorithm"));
        Assert.False(UserGradeFeedback.IsThanks("please fix the bug"));
    }

    [Fact]
    public void AgentSwitch_RequiresExplicitPickOfDifferentModel()
    {
        Assert.True(UserGradeFeedback.IsAgentSwitch(
            isExplicitClientPick: true,
            isToolChainContinuation: false,
            requestedModel: "model-b",
            priorAnswerModel: "model-a"));
        Assert.False(UserGradeFeedback.IsAgentSwitch(
            isExplicitClientPick: false,
            isToolChainContinuation: false,
            requestedModel: "model-b",
            priorAnswerModel: "model-a"));
        Assert.False(UserGradeFeedback.IsAgentSwitch(
            isExplicitClientPick: true,
            isToolChainContinuation: true,
            requestedModel: "model-b",
            priorAnswerModel: "model-a"));
        Assert.False(UserGradeFeedback.IsAgentSwitch(
            isExplicitClientPick: true,
            isToolChainContinuation: false,
            requestedModel: "model-a",
            priorAnswerModel: "model-a"));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _directory;

        private Harness(
            string directory,
            RouterMemoryDatabase database,
            SqliteGradeReceiptStore receipts,
            SqliteResponseVectorStore vectors,
            EmbeddingMemory memory,
            GradeDisputeService dispute,
            CapturingLogger logger)
        {
            _directory = directory;
            Database = database;
            Receipts = receipts;
            Vectors = vectors;
            Memory = memory;
            Dispute = dispute;
            Logger = logger;
        }

        private RouterMemoryDatabase Database { get; }
        public SqliteGradeReceiptStore Receipts { get; }
        public SqliteResponseVectorStore Vectors { get; }
        private EmbeddingMemory Memory { get; }
        public GradeDisputeService Dispute { get; }
        public CapturingLogger Logger { get; }

        public static Harness Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "thr-304-p4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(directory, "router.db")
            }));
            database.EnsureCreated();
            var receipts = new SqliteGradeReceiptStore(database);
            var secrets = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var keys = new ResponseVectorKeyHolder(
                reader: secrets,
                writer: secrets,
                database: database,
                logger: NullLogger<ResponseVectorKeyHolder>.Instance);
            var vectors = new SqliteResponseVectorStore(
                database: database,
                keys: keys,
                logger: NullLogger<SqliteResponseVectorStore>.Instance);
            var entryStore = new SqliteMemoryEntryStore(database, NullLogger<SqliteMemoryEntryStore>.Instance);
            var memory = new EmbeddingMemory(
                store: entryStore,
                optionsMonitor: new StaticOptionsMonitor(new RoutingOptions { EmbeddingMemoryCapacity = 100 }),
                embeddingClient: new FixedEmbedder([0.1f, 0.2f]),
                logger: NullLogger<EmbeddingMemory>.Instance);
            memory.InitializeAsync().GetAwaiter().GetResult();
            var logger = new CapturingLogger();
            var dispute = new GradeDisputeService(database, memory, logger);
            return new Harness(directory, database, receipts, vectors, memory, dispute, logger);
        }

        public async Task SeedCompleteReceiptAsync(string correlationId, double unifiedScore, string sessionId)
        {
            await Receipts.UpsertAsync(new GradeReceiptRecord(
                CorrelationId: correlationId,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                Dimension: "code_generation",
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
                WeightsVersion: "3.0-judge-heavy",
                AbstainedGraders: string.Empty,
                AnswerModel: "model-a",
                SessionId: sessionId,
                SpotCheck: false,
                ResponseLengthChars: 42));
        }

        public Task SeedMemoryEntryAsync(string correlationId) =>
            Memory.AddEntryAsync(
                taskEmbedding: [0.1f, 0.2f],
                chosenModel: "model-a",
                score: 0.9,
                cost: 0.01,
                verifierTrace: null,
                correlationId: correlationId);

        public Task SeedResponseVectorAsync(string correlationId) =>
            Vectors.UpsertAsync(
                correlationId: correlationId,
                embeddingModel: "bge-test",
                vector: [0.3f, 0.4f],
                createdAtUtc: DateTimeOffset.UtcNow);

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
                // A pooled SQLite handle can keep the file for a moment.
            }
        }
    }

    private sealed class CapturingLogger : ILogger<GradeDisputeService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
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
