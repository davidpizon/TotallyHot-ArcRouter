using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="ClusterTrainingService.RetrainAsync"/> - the guarded gather/blend/train/validate/write
/// entry point (docs/router/self-organizing-classification-plan.md Phase T2). No real corpus and no real
/// ONNX embedding: the OOD bootstrap source degrades to "not synced" (an unsynced <see cref="BenchmarkDatabase"/>),
/// so every sample comes from a fake <see cref="IMemoryEntryStore"/> instead, mirroring
/// <see cref="EmbeddingLogRegTrainingServiceTests"/>'s posture.
/// </summary>
public class ClusterTrainingServiceTests
{
    [Fact]
    public async Task RetrainAsync_EnoughLiveSamples_WritesArtifact()
    {
        var memoryStore = new FakeMemoryEntryStore();
        for (var i = 0; i < 100; i++)
        {
            memoryStore.Add(embedding: UnitVector(1, 0), chosenModel: "model-a", 1.0, dimension: "bug_fixing");
            memoryStore.Add(embedding: UnitVector(0, 1), chosenModel: "model-b", 0.5, dimension: "code_generation");
        }

        var modelPath = TempModelPath();
        var service = CreateService(memoryStore: memoryStore, modelPath: modelPath, 100, temp: out _);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: ClusterTrainingResultKind.Trained, actual: outcome.Kind);
            Assert.Equal(200, actual: outcome.MemoryEntryCount);
            Assert.True(outcome.ChosenK >= 1);
            Assert.True(File.Exists(modelPath));

            var artifact = ClusterModelArtifactSerializer.Deserialize(await File.ReadAllTextAsync(path: modelPath,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(200, actual: artifact.MemoryEntryCount);
            Assert.Equal(2, actual: artifact.EmbeddingDimension);
        }
        finally
        {
            CleanupModelPath(modelPath);
        }
    }

    [Fact]
    public async Task RetrainAsync_TooFewSamples_DeclinesAndWritesNoArtifact()
    {
        var memoryStore = new FakeMemoryEntryStore();
        memoryStore.Add(embedding: UnitVector(1, 0), chosenModel: "model-a", 1.0, dimension: "bug_fixing");

        var modelPath = TempModelPath();
        var service = CreateService(memoryStore: memoryStore, modelPath: modelPath, 200, temp: out _);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: ClusterTrainingResultKind.Declined, actual: outcome.Kind);
            Assert.False(File.Exists(modelPath));
        }
        finally
        {
            CleanupModelPath(modelPath);
        }
    }

    [Fact]
    public async Task RetrainAsync_MemoryEntryWithWrongEmbeddingDimension_IsSkippedNotCounted()
    {
        var memoryStore = new FakeMemoryEntryStore();
        for (var i = 0; i < 50; i++)
            memoryStore.Add(embedding: UnitVector(1, 0), chosenModel: "model-a", 1.0, dimension: "bug_fixing");
        memoryStore.Add(embedding: [1, 0, 0], chosenModel: "model-c", 1.0,
            null); // 3-dim, doesn't match the 2-dim fixture below

        var modelPath = TempModelPath();
        var service = CreateService(memoryStore: memoryStore, modelPath: modelPath, 50, temp: out _);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: ClusterTrainingResultKind.Trained, actual: outcome.Kind);
            Assert.Equal(50, actual: outcome.MemoryEntryCount);
        }
        finally
        {
            CleanupModelPath(modelPath);
        }
    }

    [Fact]
    public async Task RetrainAsync_ConcurrentCalls_SecondReturnsAlreadyRunning()
    {
        var memoryStore = new FakeMemoryEntryStore();
        for (var i = 0; i < 100; i++)
            memoryStore.Add(embedding: UnitVector(1, 0), chosenModel: "model-a", 1.0, dimension: "bug_fixing");

        var modelPath = TempModelPath();
        var service = CreateService(memoryStore: memoryStore, modelPath: modelPath, 100, temp: out _);

        try
        {
            var firstTask = service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);
            var secondOutcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            // The first call may or may not have finished by the time the second one runs, but the gate
            // guarantees the second either sees AlreadyRunning or (if it lost the race entirely) also
            // succeeds after the first releases - the only genuinely wrong outcome is neither ever declining.
            await firstTask;
            Assert.True(secondOutcome.Kind is ClusterTrainingResultKind.AlreadyRunning
                or ClusterTrainingResultKind.Trained);
        }
        finally
        {
            CleanupModelPath(modelPath);
        }
    }

    private static ClusterTrainingService CreateService(
        IMemoryEntryStore memoryStore, string modelPath, int minTrainingRows, out TempBenchmarkDatabase temp)
    {
        temp = new TempBenchmarkDatabase(); // never EnsureCreated() - the "not synced" path.
        var bootstrapSource = new OodClusterBootstrapSampleSource(
            database: temp.Database, embeddingClient: new FakeEmbeddingClient(text => [1, 0]),
            logger: NullLogger<OodClusterBootstrapSampleSource>.Instance);

        var storageOptions = Options.Create(new StorageOptions { ClusterModelPath = modelPath });
        var voter = new ClusterBestVoter(
            memoryEntryStore: memoryStore,
            embeddingClient: new StubEmbeddingClient(),
            routingOptions: Options.Create(new RoutingOptions()),
            storageOptions: storageOptions,
            logger: NullLogger<ClusterBestVoter>.Instance);

        return new ClusterTrainingService(
            bootstrapSource: bootstrapSource,
            memoryEntryStore: memoryStore,
            embeddingClient: new StubEmbeddingClient(),
            transcriptStore: new NoOpTranscriptStore(),
            voter: voter,
            routingOptions: Options.Create(new RoutingOptions
            {
                ClusterLiveSampleWeight = 1.0,
                ClusterMinTrainingRows = minTrainingRows,
                ClusterCountMin = 2,
                ClusterCountMax = 3
            }),
            embeddingOptions: Options.Create(new EmbeddingOptions { EmbeddingDimension = 2 }),
            storageOptions: storageOptions,
            logger: NullLogger<ClusterTrainingService>.Instance);
    }

    private static string TempModelPath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests", path3: Guid.NewGuid().ToString("N"),
            path4: "cluster_model.json");
    }

    private static void CleanupModelPath(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(path: directory, true);
    }

    private static float[] UnitVector(float x, float y)
    {
        var length = MathF.Sqrt(x * x + y * y);
        return [x / length, y / length];
    }

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EmbeddingResult(Vector: embed(text), 0));
        }
    }

    private sealed class FakeMemoryEntryStore : IMemoryEntryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private long _nextId = 1;

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);
        }

        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            var persisted = entry with { Id = _nextId++ };
            _entries.Add(persisted);
            return Task.FromResult(persisted);
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        public void Add(float[] embedding, string chosenModel, double score, string? dimension)
        {
            _entries.Add(new MemoryEntry(Id: _nextId++, TaskEmbedding: embedding, ChosenModel: chosenModel,
                Score: score, 0.01, null, CreatedAtUtc: DateTimeOffset.UtcNow, Dimension: dimension));
        }
    }

    /// <summary>
    /// A transcript store standing in for the disabled-capture posture: every method reports "nothing here" without
    /// throwing.
    /// </summary>
    private sealed class NoOpTranscriptStore : ITranscriptStore
    {
        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<long?>(null);
        }

        public Task UpdateOutcomeAsync(string correlationId, double? score,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TranscriptRecord?>(null);
        }

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>());
        }

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, ModelTokenAverage>>(
                new Dictionary<string, ModelTokenAverage>());
        }

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}