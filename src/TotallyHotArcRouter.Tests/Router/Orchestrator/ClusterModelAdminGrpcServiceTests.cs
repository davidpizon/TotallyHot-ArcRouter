using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Transcripts;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="ClusterModelAdminGrpcService"/>: reading the "no artifact yet" and trained states,
/// and streaming a retrain's bootstrap progress plus its terminal outcome and fresh status. Unit-tested
/// directly against a <see cref="TestServerCallContext"/> and an in-memory <see cref="IServerStreamWriter{T}"/>
/// fake, the same style as <c>BenchmarkDataAdminGrpcServiceTests</c>.
/// </summary>
public class ClusterModelAdminGrpcServiceTests
{
    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTrainingService(ClusterTrainingOutcome outcome, int[]? progressTicks = null) : IClusterTrainingService
    {
        public Task<ClusterTrainingOutcome> RetrainAsync(IProgress<int>? bootstrapProgress = null, CancellationToken cancellationToken = default)
        {
            if (progressTicks is not null)
            {
                foreach (var tick in progressTicks)
                {
                    bootstrapProgress?.Report(tick);
                }
            }

            return Task.FromResult(outcome);
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

    private sealed class FakeTranscriptStore(int rowCount) : ITranscriptStore
    {
        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateOutcomeAsync(string correlationId, double? score, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(rowCount);

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static ServerCallContext CreateContext(CancellationToken cancellationToken) =>
        TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: cancellationToken,
            peer: "test-peer",
            authContext: null!,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });

    [Fact]
    public async Task GetClusterModelStatus_NoArtifactOnDisk_ReportsArtifactAbsentAndEveryLiveEntryAsSinceLastRetrain()
    {
        var modelPath = TempModelPath();
        var service = CreateService(modelPath, new FakeTrainingService(Declined()), entryCount: 7, rowCount: 3);

        var response = await service.GetClusterModelStatus(new Contract.GetClusterModelStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.False(response.ArtifactPresent);
        Assert.Equal(0, response.ChosenK);
        Assert.Equal(7, response.EntriesSinceLastRetrain);
        Assert.Empty(response.ClusterSizes);
        Assert.Empty(response.ClusterNames);
    }

    [Fact]
    public async Task GetClusterModelStatus_ArtifactOnDisk_ReportsClustersAndEntriesSinceLastRetrain()
    {
        var modelPath = TempModelPath();
        WriteArtifact(modelPath, chosenK: 2, memoryEntryCount: 5, bootstrapTaskCount: 20);
        var service = CreateService(modelPath, new FakeTrainingService(Declined()), entryCount: 9, rowCount: 3);

        var response = await service.GetClusterModelStatus(new Contract.GetClusterModelStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.True(response.ArtifactPresent);
        Assert.Equal(2, response.ChosenK);
        Assert.Equal(20, response.BootstrapTaskCount);
        Assert.Equal(5, response.MemoryEntryCount);
        Assert.Equal(4, response.EntriesSinceLastRetrain); // 9 live entries now, 5 at training time
        Assert.Equal(2, response.ClusterSizes.Count);
        Assert.Equal(2, response.ClusterNames.Count);
    }

    [Fact]
    public async Task GetClusterModelStatus_AlwaysReportsTranscriptRetentionContext()
    {
        var modelPath = TempModelPath();
        var service = CreateService(modelPath, new FakeTrainingService(Declined()), entryCount: 0, rowCount: 123, retentionDays: 14, maxRows: 9000);

        var response = await service.GetClusterModelStatus(new Contract.GetClusterModelStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(14, response.RetentionDays);
        Assert.Equal(9000, response.MaxTranscriptRows);
        Assert.Equal(123, response.CurrentTranscriptRowCount);
    }

    [Fact]
    public async Task RetrainClusterModel_Trained_StreamsBootstrapProgressThenTrainedResultWithFreshStatus()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(Trained("wrote 3 clusters"), progressTicks: [1, 2, 3]);
        var service = CreateService(modelPath, trainingService, entryCount: 0, rowCount: 0);
        var writer = new FakeServerStreamWriter<Contract.ClusterRetrainStreamEvent>();

        await service.RetrainClusterModel(new Contract.RetrainClusterModelRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var progressEvents = writer.Written
            .Where(e => e.EventCase == Contract.ClusterRetrainStreamEvent.EventOneofCase.BootstrapProgress)
            .Select(e => e.BootstrapProgress.TasksEmbedded)
            .ToList();
        Assert.Equal([1, 2, 3], progressEvents);

        var result = Assert.Single(writer.Written, e => e.EventCase == Contract.ClusterRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(Contract.ClusterRetrainResultKind.Trained, result.Kind);
        Assert.Equal("wrote 3 clusters", result.Message);
        Assert.Same(writer.Written[^1], writer.Written.Last());
    }

    [Fact]
    public async Task RetrainClusterModel_Declined_StreamsDeclinedResultRatherThanThrowing()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(Declined());
        var service = CreateService(modelPath, trainingService, entryCount: 1, rowCount: 0);
        var writer = new FakeServerStreamWriter<Contract.ClusterRetrainStreamEvent>();

        await service.RetrainClusterModel(new Contract.RetrainClusterModelRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(writer.Written, e => e.EventCase == Contract.ClusterRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(Contract.ClusterRetrainResultKind.Declined, result.Kind);
        Assert.False(result.Status.ArtifactPresent);
    }

    [Fact]
    public async Task RetrainClusterModel_AlreadyRunning_StreamsAlreadyRunningResult()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(new ClusterTrainingOutcome(ClusterTrainingResultKind.AlreadyRunning, "busy", 0, 0, 0, 0));
        var service = CreateService(modelPath, trainingService, entryCount: 0, rowCount: 0);
        var writer = new FakeServerStreamWriter<Contract.ClusterRetrainStreamEvent>();

        await service.RetrainClusterModel(new Contract.RetrainClusterModelRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(writer.Written, e => e.EventCase == Contract.ClusterRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(Contract.ClusterRetrainResultKind.AlreadyRunning, result.Kind);
    }

    private static ClusterTrainingOutcome Trained(string message) =>
        new(ClusterTrainingResultKind.Trained, message, 20, 5, 25, 2);

    private static ClusterTrainingOutcome Declined() =>
        new(ClusterTrainingResultKind.Declined, "Declined: not enough samples.", 0, 1, 1, 0);

    private static string TempModelPath() =>
        Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "cluster_model.json");

    private static void WriteArtifact(string path, int chosenK, int memoryEntryCount, int bootstrapTaskCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var artifact = new ClusterModelArtifact(
            EmbeddingDimension: 2,
            Centroids: [.. Enumerable.Range(0, chosenK).Select(_ => new float[] { 1, 0 })],
            ChosenK: chosenK,
            TrainedAtUtc: DateTimeOffset.UtcNow,
            ClusterSizes: [.. Enumerable.Range(0, chosenK).Select(i => i + 1)],
            ClusterDimensionHistograms: [.. Enumerable.Range(0, chosenK).Select(_ => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>())],
            ClusterTopTerms: [.. Enumerable.Range(0, chosenK).Select(_ => (IReadOnlyList<string>)Array.Empty<string>())],
            TrainedFrom: "bootstrap_tasks=20, memory_entries=5",
            BootstrapTaskCount: bootstrapTaskCount,
            MemoryEntryCount: memoryEntryCount);
        File.WriteAllText(path, ClusterModelArtifactSerializer.Serialize(artifact));
    }

    private static ClusterModelAdminGrpcService CreateService(
        string modelPath,
        IClusterTrainingService trainingService,
        int entryCount,
        int rowCount,
        int retentionDays = 30,
        int maxRows = 50_000) =>
        new(
            trainingService,
            new FakeMemoryEntryStore(entryCount),
            new FakeTranscriptStore(rowCount),
            Options.Create(new TranscriptOptions { Enabled = true, RetentionDays = retentionDays, MaxRows = maxRows }),
            Options.Create(new StorageOptions { ClusterModelPath = modelPath }),
            NullLogger<ClusterModelAdminGrpcService>.Instance);
}
