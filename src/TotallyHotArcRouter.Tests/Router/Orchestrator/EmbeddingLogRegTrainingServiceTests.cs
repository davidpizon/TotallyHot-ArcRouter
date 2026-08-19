using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
            memoryStore.Add(UnitVector(1, 0), "model-a", 1.0);
            memoryStore.Add(UnitVector(0, 1), "model-b", 1.0);
        }

        var modelPath = TempModelPath();
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, Options.Create(new StorageOptions { LogRegModelPath = modelPath }));
        var service = CreateService(memoryStore, voter, modelPath, out _);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(LogRegTrainingResultKind.Trained, outcome.Kind);
            Assert.Equal(30, outcome.MemoryEntryCount);
            Assert.Equal(2, outcome.ModelsRepresented);
            Assert.True(File.Exists(modelPath));

            // Proves LogRegVoter.Reload() was actually signaled: the voter's first-ever GetModel() call
            // happens inside VoteAsync below, so a non-abstaining vote is only possible if it loads the
            // artifact this call just wrote, not a stale null cache from before training.
            var vote = await voter.VoteAsync(
                new VotingContext("dimension", [new RoutingCandidate("model-a", "provider", false), new RoutingCandidate("model-b", "provider", false)], UnitVector(1, 0), null),
                TestContext.Current.CancellationToken);
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
        memoryStore.Add(UnitVector(1, 0), "model-a", 1.0);

        var modelPath = TempModelPath();
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, Options.Create(new StorageOptions { LogRegModelPath = modelPath }));
        var service = CreateService(memoryStore, voter, modelPath, out _);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(LogRegTrainingResultKind.Declined, outcome.Kind);
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
        for (var i = 0; i < 30; i++)
        {
            memoryStore.Add(UnitVector(1, 0), "model-a", 1.0);
        }

        var modelPath = TempModelPath();
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, Options.Create(new StorageOptions { LogRegModelPath = modelPath }));
        var service = CreateService(memoryStore, voter, modelPath, out _);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(LogRegTrainingResultKind.Declined, outcome.Kind);
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
            memoryStore.Add(UnitVector(1, 0), "model-a", 1.0);
            memoryStore.Add(UnitVector(0, 1), "model-b", 1.0);
        }
        memoryStore.Add([1, 0, 0], "model-c", 1.0); // 3-dim, doesn't match the 2-dim fixture below

        var modelPath = TempModelPath();
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, Options.Create(new StorageOptions { LogRegModelPath = modelPath }));
        var service = CreateService(memoryStore, voter, modelPath, out _);

        try
        {
            var outcome = await service.RetrainAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(LogRegTrainingResultKind.Trained, outcome.Kind);
            Assert.Equal(2, outcome.ModelsRepresented);
        }
        finally
        {
            CleanupModelPath(modelPath);
        }
    }

    private static EmbeddingLogRegTrainingService CreateService(
        IMemoryEntryStore memoryStore, LogRegVoter voter, string modelPath, out TempBenchmarkDatabase temp)
    {
        temp = new TempBenchmarkDatabase(); // never EnsureCreated() - the "not synced" path.
        var bootstrapSource = new OodBootstrapSampleSource(
            temp.Database, new FakeEmbeddingClient(text => [1, 0]), NullLogger<OodBootstrapSampleSource>.Instance);

        return new EmbeddingLogRegTrainingService(
            bootstrapSource,
            memoryStore,
            voter,
            Options.Create(new RoutingOptions { LogRegLiveSampleWeight = 1.0, LogRegMinTrainingRows = 20, LogRegMinModelsRepresented = 2 }),
            Options.Create(new EmbeddingOptions { EmbeddingDimension = 2 }),
            Options.Create(new StorageOptions { LogRegModelPath = modelPath }),
            NullLogger<EmbeddingLogRegTrainingService>.Instance);
    }

    private static string TempModelPath() =>
        Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "logreg_voter_model.json");

    private static void CleanupModelPath(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static float[] UnitVector(float x, float y)
    {
        var length = MathF.Sqrt((x * x) + (y * y));
        return [x / length, y / length];
    }

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult(embed(text), TokenCount: 0));
    }

    private sealed class FakeMemoryEntryStore : IMemoryEntryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private long _nextId = 1;

        public void Add(float[] embedding, string chosenModel, double score) =>
            _entries.Add(new MemoryEntry(_nextId++, embedding, chosenModel, score, Cost: 0.01, VerifierTrace: null, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);

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
    }
}
