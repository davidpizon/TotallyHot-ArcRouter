using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="LogRegRetrainHostedService.CheckAndRetrainAsync"/> (docs/router/live-feedback-
/// learning-plan.md Phase 4c's automatic threshold trigger), called directly rather than through
/// <see cref="LogRegRetrainHostedService.ExecuteAsync"/>'s <see cref="PeriodicTimer"/> loop - the same
/// "internal for direct test access" convention <c>Program.ExtractFlag</c> uses.
/// </summary>
public class LogRegRetrainHostedServiceTests
{
    [Fact]
    public async Task CheckAndRetrainAsync_EntryCountBelowThreshold_DoesNotRetrain()
    {
        var trainingService = new RecordingTrainingService();
        var memoryStore = new FakeMemoryEntryStore(entryCount: 10);
        var service = CreateService(trainingService, memoryStore, threshold: 500, enabled: true);

        await service.CheckAndRetrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, trainingService.CallCount);
    }

    [Fact]
    public async Task CheckAndRetrainAsync_EntryCountAtOrAboveThreshold_Retrains()
    {
        var trainingService = new RecordingTrainingService();
        var memoryStore = new FakeMemoryEntryStore(entryCount: 500);
        var service = CreateService(trainingService, memoryStore, threshold: 500, enabled: true);

        await service.CheckAndRetrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, trainingService.CallCount);
    }

    [Fact]
    public async Task CheckAndRetrainAsync_AutomaticRetrainDisabled_NeverRetrainsRegardlessOfCount()
    {
        var trainingService = new RecordingTrainingService();
        var memoryStore = new FakeMemoryEntryStore(entryCount: 10_000);
        var service = CreateService(trainingService, memoryStore, threshold: 500, enabled: false);

        await service.CheckAndRetrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, trainingService.CallCount);
    }

    private static LogRegRetrainHostedService CreateService(
        IEmbeddingLogRegTrainingService trainingService, IMemoryEntryStore memoryStore, int threshold, bool enabled)
    {
        var modelPath = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "logreg_voter_model.json");
        return new LogRegRetrainHostedService(
            NullLogger<LogRegRetrainHostedService>.Instance,
            trainingService,
            memoryStore,
            Options.Create(new RoutingOptions { LogRegRetrainThreshold = threshold, EnableAutomaticLogRegRetrain = enabled }),
            Options.Create(new StorageOptions { LogRegModelPath = modelPath }));
    }

    private sealed class RecordingTrainingService : IEmbeddingLogRegTrainingService
    {
        public int CallCount { get; private set; }

        public Task<LogRegTrainingOutcome> RetrainAsync(IProgress<int>? bootstrapProgress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new LogRegTrainingOutcome(LogRegTrainingResultKind.Trained, "test", 0, 0, 0, 0));
        }
    }

    private sealed class FakeMemoryEntryStore(int entryCount) : IMemoryEntryStore
    {
        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MemoryEntry> entries = [.. Enumerable.Range(0, entryCount)
                .Select(i => new MemoryEntry(i, [1, 0], "model-a", 1.0, 0.01, null, DateTimeOffset.UtcNow))];
            return Task.FromResult(entries);
        }

        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
