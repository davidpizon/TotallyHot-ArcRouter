using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Issue #304 phase 3: peek the pending question vector before the kNN take, stamp
/// <c>question_vector_missing</c>, and seal a response vector only when a grader scored the reply.
/// </summary>
public class ResponseVectorPhase3Tests
{
    [Fact]
    public void QuestionVectorPeek_HitLeavesTheVectorForTheKnnTake_AndStampsZero()
    {
        var cache = new PendingValueCache<float[]>(Options.Create(new RoutingOptions()));
        var vector = new[] { 0.25f, 0.5f };
        cache.Set(correlationId: "sess:1", value: vector);
        var reader = new PendingQuestionVectorGradeReader(pendingQuestionVectors: cache,
            logger: NullLogger<PendingQuestionVectorGradeReader>.Instance);
        var result = new QualityResult { RequestCorrelationId = "sess:1", JudgeScore = 0.8 };

        reader.ObserveAsync(result: result, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
        var taken = cache.TryTake(correlationId: "sess:1", value: out var stillThere);

        Assert.False(result.QuestionVectorMissing);
        Assert.True(taken);
        Assert.Equal(vector, stillThere);
    }

    [Fact]
    public void QuestionVectorPeek_MissStampsOne_AndDoesNotWriteAZero()
    {
        var cache = new PendingValueCache<float[]>(Options.Create(new RoutingOptions()));
        var reader = new PendingQuestionVectorGradeReader(pendingQuestionVectors: cache,
            logger: NullLogger<PendingQuestionVectorGradeReader>.Instance);
        var result = new QualityResult { RequestCorrelationId = "sess:2", JudgeScore = 0.8 };

        reader.ObserveAsync(result: result, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.QuestionVectorMissing);
        Assert.False(cache.TryTake(correlationId: "sess:2", value: out _));
    }

    [Fact]
    public async Task ScoredReply_StoresASealedVector_ThatOpensBack()
    {
        using var harness = Harness.Create();
        var embedder = new FixedEmbedder([0.1f, 0.2f, 0.3f]);
        harness.Text.Set(correlationId: "sess:3", text: "a prose answer");
        var observer = harness.Observer(embedder);
        var result = new QualityResult { RequestCorrelationId = "sess:3", JudgeScore = 0.7 };

        await observer.ObserveAsync(result: result, cancellationToken: CancellationToken.None);

        Assert.Equal(1, embedder.Calls);
        Assert.True(harness.Store.TryOpen(correlationId: "sess:3", vector: out var opened));
        Assert.Equal(embedder.Vector, opened);
        var blob = harness.Store.ReadSealed("sess:3");
        Assert.NotNull(blob);
        Assert.NotEqual(Plain(embedder.Vector), blob);
    }

    [Fact]
    public async Task LocalScoreOnly_StoresAVector_UnscoredReceiptsDoNot()
    {
        using var harness = Harness.Create();
        var embedder = new FixedEmbedder([1f]);
        harness.Text.Set(correlationId: "shadow", text: "shadowed");
        harness.Text.Set(correlationId: "pin", text: "the judge wrote this");
        harness.Text.Set(correlationId: "empty-score", text: "no score");
        var observer = harness.Observer(embedder);

        await observer.ObserveAsync(
            result: new QualityResult { RequestCorrelationId = "shadow", LocalScore = 0.4 },
            cancellationToken: CancellationToken.None);
        await observer.ObserveAsync(
            result: new QualityResult
            {
                RequestCorrelationId = "pin",
                GradeStatus = GradeStatus.Incomplete,
                IncompleteReason = "judge_is_answerer"
            },
            cancellationToken: CancellationToken.None);
        await observer.ObserveAsync(
            result: new QualityResult
            {
                RequestCorrelationId = "empty-score",
                GradeStatus = GradeStatus.StructureOnly
            },
            cancellationToken: CancellationToken.None);

        Assert.NotNull(harness.Store.ReadSealed("shadow"));
        Assert.Null(harness.Store.ReadSealed("pin"));
        Assert.Null(harness.Store.ReadSealed("empty-score"));
        Assert.Equal(1, embedder.Calls);
    }

    [Fact]
    public async Task LoopReceiptsWithoutScores_WriteNoRow_EvenWhenTextIsCached()
    {
        using var harness = Harness.Create();
        var embedder = new FixedEmbedder([1f]);
        harness.Text.Set(correlationId: "tool-only", text: " ");
        harness.Text.Set(correlationId: "unlinked", text: "It is 72F.");
        var observer = harness.Observer(embedder);

        await observer.ObserveAsync(
            result: new QualityResult
            {
                RequestCorrelationId = "tool-only",
                GradeStatus = GradeStatus.Incomplete,
                IncompleteReason = IncompleteReasons.DeferredToLoop
            },
            cancellationToken: CancellationToken.None);
        await observer.ObserveAsync(
            result: new QualityResult
            {
                RequestCorrelationId = "unlinked",
                GradeStatus = GradeStatus.Incomplete,
                IncompleteReason = IncompleteReasons.LoopUnlinked
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, embedder.Calls);
        Assert.Null(harness.Store.ReadSealed("tool-only"));
        Assert.Null(harness.Store.ReadSealed("unlinked"));
    }

    [Fact]
    public async Task MissingResponseText_WritesNoRow_AndDoesNotEmbed()
    {
        using var harness = Harness.Create();
        var embedder = new FixedEmbedder([1f]);
        var observer = harness.Observer(embedder);

        await observer.ObserveAsync(
            result: new QualityResult { RequestCorrelationId = "gone", JudgeScore = 0.5 },
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, embedder.Calls);
        Assert.Null(harness.Store.ReadSealed("gone"));
    }

    [Fact]
    public async Task TwoSealsOfTheSameVector_Differ_AndACopiedBlobFailsAuthentication()
    {
        using var harness = Harness.Create();
        var embedder = new FixedEmbedder([0.5f, 0.5f]);
        harness.Text.Set(correlationId: "a", text: "same");
        var observer = harness.Observer(embedder);
        await observer.ObserveAsync(
            result: new QualityResult { RequestCorrelationId = "a", JudgeScore = 1 },
            cancellationToken: CancellationToken.None);
        var first = harness.Store.ReadSealed("a");
        await observer.ObserveAsync(
            result: new QualityResult { RequestCorrelationId = "a", JudgeScore = 1 },
            cancellationToken: CancellationToken.None);
        var second = harness.Store.ReadSealed("a");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);

        harness.CopySealed(fromCorrelationId: "a", toCorrelationId: "b");
        Assert.False(harness.Store.TryOpen(correlationId: "b", vector: out _));
        Assert.Null(harness.Store.ReadSealed("b"));
        Assert.True(harness.Store.TryOpen(correlationId: "a", vector: out _));
    }

    [Fact]
    public void SealAndOpen_TenThousandRows_StayInsideFiveSeconds()
    {
        var key = new byte[32];
        key[0] = 7;
        var vector = new[] { 0.2f, 0.4f, 0.6f, 0.8f };
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < 10_000; i++)
        {
            var id = "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var blob = ResponseVectorSeal.Seal(key: key, correlationId: id, modelId: "bge", vector: vector);
            Assert.True(ResponseVectorSeal.TryOpen(key: key, correlationId: id, modelId: "bge", blob: blob,
                vector: out var opened));
            Assert.Equal(vector, opened);
        }

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MissingKey_DeletesExistingRows_AndStartsANewKey()
    {
        using var harness = Harness.Create();
        var embedder = new FixedEmbedder([0.3f]);
        harness.Text.Set(correlationId: "old", text: "before shred");
        harness.Observer(embedder).ObserveAsync(
            result: new QualityResult { RequestCorrelationId = "old", JudgeScore = 0.2 },
            cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
        Assert.NotNull(harness.Store.ReadSealed("old"));

        var master = new SecretStoreSessionMasterKeyStore(harness.Secrets);
        master.DestroyAll();
        var restarted = new ResponseVectorKeyHolder(
            reader: harness.Secrets,
            writer: harness.Secrets,
            database: harness.Database,
            logger: NullLogger<ResponseVectorKeyHolder>.Instance);
        restarted.GetOrCreate();

        Assert.Null(harness.Store.ReadSealed("old"));
        Assert.True(harness.Secrets.TryRead(name: ResponseVectorSeal.SecretName, value: out var stored));
        Assert.Equal(32, Convert.FromBase64String(stored).Length);
    }

    [Fact]
    public async Task Receipt_PersistsThePeekFlag_AndLeavesFingerprintNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "router.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = path }));
        database.EnsureCreated();
        var store = new SqliteGradeReceiptStore(database);
        var window = new PendingConversationWindowCache(Options.Create(new JudgeOptions()));
        var observer = new GradeReceiptRecordObserver(
            store: store,
            pendingWindowCache: window,
            questionHmacHasher: new QuestionHmacHasher(new ProtectedSecretStore(path + ".secrets")),
            qualityOptions: Options.Create(new QualityOptions()),
            judgeOptions: Options.Create(new JudgeOptions()),
            logger: NullLogger<GradeReceiptRecordObserver>.Instance);
        var result = new QualityResult
        {
            RequestCorrelationId = "sess:9",
            Model = "answerer",
            JudgeScore = 0.6,
            GradeStatus = GradeStatus.Complete,
            QuestionVectorMissing = true
        };

        await observer.ObserveAsync(result: result, cancellationToken: CancellationToken.None);
        var receipt = await store.TryGetAsync(correlationId: "sess:9", cancellationToken: CancellationToken.None);

        Assert.NotNull(receipt);
        Assert.True(receipt.QuestionVectorMissing);
        Assert.Null(receipt.FingerprintRef);
    }

    private static byte[] Plain(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(src: vector, srcOffset: 0, dst: bytes, dstOffset: 0, count: bytes.Length);
        return bytes;
    }

    private sealed class FixedEmbedder(float[] vector) : IEmbeddingClient
    {
        public float[] Vector { get; } = vector;

        public int Calls { get; private set; }

        public string ModelIdentity => "bge-test";

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new EmbeddingResult(Vector, TokenCount: 1));
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _directory;

        private Harness(string directory, RouterMemoryDatabase database, ProtectedSecretStore secrets,
            SqliteResponseVectorStore store, PendingResponseTextCache text)
        {
            _directory = directory;
            Database = database;
            Secrets = secrets;
            Store = store;
            Text = text;
        }

        public RouterMemoryDatabase Database { get; }

        public ProtectedSecretStore Secrets { get; }

        public SqliteResponseVectorStore Store { get; }

        public PendingResponseTextCache Text { get; }

        public static Harness Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(directory, "router.db")
            }));
            database.EnsureCreated();
            var secrets = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var keys = new ResponseVectorKeyHolder(
                reader: secrets,
                writer: secrets,
                database: database,
                logger: NullLogger<ResponseVectorKeyHolder>.Instance);
            var store = new SqliteResponseVectorStore(
                database: database,
                keys: keys,
                logger: NullLogger<SqliteResponseVectorStore>.Instance);
            var text = new PendingResponseTextCache(Options.Create(new JudgeOptions()));
            return new Harness(directory, database, secrets, store, text);
        }

        public ResponseVectorGradeObserver Observer(IEmbeddingClient embeddings) =>
            new(embeddings: embeddings, responseText: Text, store: Store,
                logger: NullLogger<ResponseVectorGradeObserver>.Instance);

        public void CopySealed(string fromCorrelationId, string toCorrelationId)
        {
            var blob = Store.ReadSealed(fromCorrelationId);
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO response_vectors (correlation_id, created_at_utc, embedding_model, vector)
                                  VALUES ($id, $at, $model, $vector);
                                  """;
            command.Parameters.AddWithValue("$id", toCorrelationId);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$model", "bge-test");
            command.Parameters.AddWithValue("$vector", blob);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(path: _directory, recursive: true);
            }
            catch (IOException)
            {
                // A pooled SQLite handle can keep the file for a moment. The temp directory is unique.
            }
        }
    }
}
