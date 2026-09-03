using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="ClusterRetrainHostedService.CheckAndRetrainAsync"/> (docs/router/self-organizing-
/// classification-plan.md Phase T2g's automatic threshold trigger), called directly rather than through
/// <see cref="ClusterRetrainHostedService.ExecuteAsync"/>'s <see cref="PeriodicTimer"/> loop, mirroring
/// <see cref="LogRegRetrainHostedServiceTests"/>'s convention.
/// </summary>
public class ClusterRetrainHostedServiceTests
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

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T6: automatic cluster retraining is now
    /// additionally gated on <see cref="RoutingOptions.EnableAdaptiveRouting"/>, even when
    /// <see cref="RoutingOptions.EnableAutomaticClusterRetrain"/> is on and the threshold is crossed.
    /// </summary>
    [Fact]
    public async Task CheckAndRetrainAsync_AdaptiveRoutingDisabled_NeverRetrainsRegardlessOfCount()
    {
        var trainingService = new RecordingTrainingService();
        var memoryStore = new FakeMemoryEntryStore(entryCount: 10_000);
        var service = CreateService(trainingService, memoryStore, threshold: 500, enabled: true, enableAdaptiveRouting: false);

        await service.CheckAndRetrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, trainingService.CallCount);
    }

    private static ClusterRetrainHostedService CreateService(
        IClusterTrainingService trainingService, IMemoryEntryStore memoryStore, int threshold, bool enabled, bool enableAdaptiveRouting = true)
    {
        var modelPath = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "cluster_model.json");
        return new ClusterRetrainHostedService(
            NullLogger<ClusterRetrainHostedService>.Instance,
            trainingService,
            memoryStore,
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            {
                ClusterRetrainThreshold = threshold,
                EnableAutomaticClusterRetrain = enabled,
                EnableAdaptiveRouting = enableAdaptiveRouting,
            }),
            Options.Create(new StorageOptions { ClusterModelPath = modelPath }));
    }

    private sealed class RecordingTrainingService : IClusterTrainingService
    {
        public int CallCount { get; private set; }

        public Task<ClusterTrainingOutcome> RetrainAsync(IProgress<int>? bootstrapProgress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ClusterTrainingOutcome(ClusterTrainingResultKind.Trained, "test", 0, 0, 0, 0));
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
