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

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="EmbeddingLogRegTrainingService.RetrainAsync"/> - the guarded gather/blend/validate/
/// write/reload entry point (docs/router/live-feedback-learning-plan.md Phase 4c). No real corpus and no
/// real ONNX embedding: the OOD bootstrap source degrades to "not synced" (an unsynced
/// <see cref="BenchmarkDatabase"/>, exercising the same degrade-to-live-only path
/// <see cref="OodBootstrapSampleSourceTests"/> covers directly), and every sample comes from a fake
/// <see cref="IMemoryEntryStore"/> instead.
/// </summary>
public class EmbeddingLogRegTrainingServiceTests
{
    [Fact]
    public async Task RetrainAsync_EnoughLiveSamplesAcrossTwoModels_WritesArtifactAndReloadsVoter()
    {
        var memoryStore = new FakeMemoryEntryStore();
        for (var i = 0; i < 15; i++)
        {
            memoryStore.Add(embedding: UnitVector(1, 0), chosenModel: "model-a", 1.0);
            memoryStore.Add(embedding: UnitVector(0, 1), chosenModel: "model-b", 1.0);
        }

        var modelPath = TempModelPath();
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance,
            storageOptions: Options.Create(new StorageOptions { LogRegModelPath = modelPath }),
            embeddingClient: new StubEmbeddingClient());
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated() - the "not synced" path.
        var service = CreateService(memoryStore: memoryStore, voter: voter, modelPath: modelPath, temp: temp);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: LogRegTrainingResultKind.Trained, actual: outcome.Kind);
            Assert.Equal(30, actual: outcome.MemoryEntryCount);
            Assert.Equal(2, actual: outcome.ModelsRepresented);
            Assert.True(File.Exists(modelPath));

            // Proves LogRegVoter.Reload() was actually signaled: the voter's first-ever GetModel() call
            // happens inside VoteAsync below, so a non-abstaining vote is only possible if it loads the
            // artifact this call just wrote, not a stale null cache from before training.
            var vote = await voter.VoteAsync(
                context: new VotingContext(Dimension: "dimension",
                    Candidates:
                    [
                        new RoutingCandidate(ModelName: "model-a", Provider: "provider", false),
                        new RoutingCandidate(ModelName: "model-b", Provider: "provider", false)
                    ], TaskEmbedding: UnitVector(1, 0)),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(vote.IsAbstain);
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
        memoryStore.Add(embedding: UnitVector(1, 0), chosenModel: "model-a", 1.0);

        var modelPath = TempModelPath();
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance,
            storageOptions: Options.Create(new StorageOptions { LogRegModelPath = modelPath }),
            embeddingClient: new StubEmbeddingClient());
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated() - the "not synced" path.
        var service = CreateService(memoryStore: memoryStore, voter: voter, modelPath: modelPath, temp: temp);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: LogRegTrainingResultKind.Declined, actual: outcome.Kind);
            Assert.False(File.Exists(modelPath));
        }
        finally
        {
            CleanupModelPath(modelPath);
        }
    }

    [Fact]
    public async Task RetrainAsync_TooFewDistinctModels_DeclinesEvenWithEnoughRows()
    {
        var memoryStore = new FakeMemoryEntryStore();
        for (var i = 0; i < 30; i++) memoryStore.Add(embedding: UnitVector(1, 0), chosenModel: "model-a", 1.0);

        var modelPath = TempModelPath();
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance,
            storageOptions: Options.Create(new StorageOptions { LogRegModelPath = modelPath }),
            embeddingClient: new StubEmbeddingClient());
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated() - the "not synced" path.
        var service = CreateService(memoryStore: memoryStore, voter: voter, modelPath: modelPath, temp: temp);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: LogRegTrainingResultKind.Declined, actual: outcome.Kind);
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
        for (var i = 0; i < 15; i++)
        {
            memoryStore.Add(embedding: UnitVector(1, 0), chosenModel: "model-a", 1.0);
            memoryStore.Add(embedding: UnitVector(0, 1), chosenModel: "model-b", 1.0);
        }

        memoryStore.Add(embedding: [1, 0, 0], chosenModel: "model-c",
            1.0); // 3-dim, doesn't match the 2-dim fixture below

        var modelPath = TempModelPath();
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance,
            storageOptions: Options.Create(new StorageOptions { LogRegModelPath = modelPath }),
            embeddingClient: new StubEmbeddingClient());
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated() - the "not synced" path.
        var service = CreateService(memoryStore: memoryStore, voter: voter, modelPath: modelPath, temp: temp);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: LogRegTrainingResultKind.Trained, actual: outcome.Kind);
            Assert.Equal(2, actual: outcome.ModelsRepresented);
        }
        finally
        {
            CleanupModelPath(modelPath);
        }
    }

    private static EmbeddingLogRegTrainingService CreateService(
        IMemoryEntryStore memoryStore, LogRegVoter voter, string modelPath, TempBenchmarkDatabase temp)
    {
        var bootstrapSource = new OodBootstrapSampleSource(
            database: temp.Database, embeddingClient: new FakeEmbeddingClient(_ => [1, 0]),
            logger: NullLogger<OodBootstrapSampleSource>.Instance);

        return new EmbeddingLogRegTrainingService(
            bootstrapSource: bootstrapSource,
            memoryEntryStore: memoryStore,
            embeddingClient: new StubEmbeddingClient(),
            voter: voter,
            routingOptions: Options.Create(new RoutingOptions
            { LogRegLiveSampleWeight = 1.0, LogRegMinTrainingRows = 20, LogRegMinModelsRepresented = 2 }),
            embeddingOptions: Options.Create(new EmbeddingOptions { EmbeddingDimension = 2 }),
            storageOptions: Options.Create(new StorageOptions { LogRegModelPath = modelPath }),
            logger: NullLogger<EmbeddingLogRegTrainingService>.Instance);
    }

    private static string TempModelPath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests", path3: Guid.NewGuid().ToString("N"),
            path4: "logreg_voter_model.json");
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

        public void Add(float[] embedding, string chosenModel, double score)
        {
            _entries.Add(new MemoryEntry(Id: _nextId++, TaskEmbedding: embedding, ChosenModel: chosenModel,
                Score: score, 0.01, null, CreatedAtUtc: DateTimeOffset.UtcNow));
        }
    }
}