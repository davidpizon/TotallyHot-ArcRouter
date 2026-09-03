using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Transcripts;
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
    private static ServerCallContext CreateContext(CancellationToken cancellationToken)
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: cancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    [Fact]
    public async Task GetClusterModelStatus_NoArtifactOnDisk_ReportsArtifactAbsentAndEveryLiveEntryAsSinceLastRetrain()
    {
        var modelPath = TempModelPath();
        var service = CreateService(modelPath: modelPath, trainingService: new FakeTrainingService(Declined()), 7, 3);

        var response = await service.GetClusterModelStatus(request: new Contract.GetClusterModelStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.False(response.ArtifactPresent);
        Assert.Equal(0, actual: response.ChosenK);
        Assert.Equal(7, actual: response.EntriesSinceLastRetrain);
        Assert.Empty(response.ClusterSizes);
        Assert.Empty(response.ClusterNames);
    }

    [Fact]
    public async Task GetClusterModelStatus_ArtifactOnDisk_ReportsClustersAndEntriesSinceLastRetrain()
    {
        var modelPath = TempModelPath();
        WriteArtifact(path: modelPath, 2, 5, 20);
        var service = CreateService(modelPath: modelPath, trainingService: new FakeTrainingService(Declined()), 9, 3);

        var response = await service.GetClusterModelStatus(request: new Contract.GetClusterModelStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.True(response.ArtifactPresent);
        Assert.Equal(2, actual: response.ChosenK);
        Assert.Equal(20, actual: response.BootstrapTaskCount);
        Assert.Equal(5, actual: response.MemoryEntryCount);
        Assert.Equal(4, actual: response.EntriesSinceLastRetrain); // 9 live entries now, 5 at training time
        Assert.Equal(2, actual: response.ClusterSizes.Count);
        Assert.Equal(2, actual: response.ClusterNames.Count);
    }

    [Fact]
    public async Task GetClusterModelStatus_AlwaysReportsTranscriptRetentionContext()
    {
        var modelPath = TempModelPath();
        var service = CreateService(modelPath: modelPath, trainingService: new FakeTrainingService(Declined()), 0, 123,
            14, 9000);

        var response = await service.GetClusterModelStatus(request: new Contract.GetClusterModelStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(14, actual: response.RetentionDays);
        Assert.Equal(9000, actual: response.MaxTranscriptRows);
        Assert.Equal(123, actual: response.CurrentTranscriptRowCount);
    }

    [Fact]
    public async Task RetrainClusterModel_Trained_StreamsBootstrapProgressThenTrainedResultWithFreshStatus()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(outcome: Trained("wrote 3 clusters"), progressTicks: [1, 2, 3]);
        var service = CreateService(modelPath: modelPath, trainingService: trainingService, 0, 0);
        var writer = new FakeServerStreamWriter<Contract.ClusterRetrainStreamEvent>();

        await service.RetrainClusterModel(request: new Contract.RetrainClusterModelRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var progressEvents = writer.Written
            .Where(e => e.EventCase == Contract.ClusterRetrainStreamEvent.EventOneofCase.BootstrapProgress)
            .Select(e => e.BootstrapProgress.TasksEmbedded)
            .ToList();
        Assert.Equal(expected: [1, 2, 3], actual: progressEvents);

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.ClusterRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.ClusterRetrainResultKind.Trained, actual: result.Kind);
        Assert.Equal(expected: "wrote 3 clusters", actual: result.Message);
        Assert.Same(expected: writer.Written[^1], actual: writer.Written.Last());
    }

    [Fact]
    public async Task RetrainClusterModel_Declined_StreamsDeclinedResultRatherThanThrowing()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(Declined());
        var service = CreateService(modelPath: modelPath, trainingService: trainingService, 1, 0);
        var writer = new FakeServerStreamWriter<Contract.ClusterRetrainStreamEvent>();

        await service.RetrainClusterModel(request: new Contract.RetrainClusterModelRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.ClusterRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.ClusterRetrainResultKind.Declined, actual: result.Kind);
        Assert.False(result.Status.ArtifactPresent);
    }

    [Fact]
    public async Task RetrainClusterModel_AlreadyRunning_StreamsAlreadyRunningResult()
    {
        var modelPath = TempModelPath();
        var trainingService =
            new FakeTrainingService(new ClusterTrainingOutcome(Kind: ClusterTrainingResultKind.AlreadyRunning,
                Message: "busy", 0, 0, 0, 0));
        var service = CreateService(modelPath: modelPath, trainingService: trainingService, 0, 0);
        var writer = new FakeServerStreamWriter<Contract.ClusterRetrainStreamEvent>();

        await service.RetrainClusterModel(request: new Contract.RetrainClusterModelRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.ClusterRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.ClusterRetrainResultKind.AlreadyRunning, actual: result.Kind);
    }

    private static ClusterTrainingOutcome Trained(string message)
    {
        return new ClusterTrainingOutcome(Kind: ClusterTrainingResultKind.Trained, Message: message, 20, 5, 25, 2);
    }

    private static ClusterTrainingOutcome Declined()
    {
        return new ClusterTrainingOutcome(Kind: ClusterTrainingResultKind.Declined,
            Message: "Declined: not enough samples.", 0, 1, 1, 0);
    }

    private static string TempModelPath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests", path3: Guid.NewGuid().ToString("N"),
            path4: "cluster_model.json");
    }

    private static void WriteArtifact(string path, int chosenK, int memoryEntryCount, int bootstrapTaskCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var artifact = new ClusterModelArtifact(
            2,
            Centroids: [.. Enumerable.Range(0, count: chosenK).Select(_ => new float[] { 1, 0 })],
            ChosenK: chosenK,
            TrainedAtUtc: DateTimeOffset.UtcNow,
            ClusterSizes: [.. Enumerable.Range(0, count: chosenK).Select(i => i + 1)],
            ClusterDimensionHistograms:
            [
                .. Enumerable.Range(0, count: chosenK)
                    .Select(_ => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>())
            ],
            ClusterTopTerms:
            [.. Enumerable.Range(0, count: chosenK).Select(_ => (IReadOnlyList<string>)Array.Empty<string>())],
            TrainedFrom: "bootstrap_tasks=20, memory_entries=5",
            BootstrapTaskCount: bootstrapTaskCount,
            MemoryEntryCount: memoryEntryCount);
        File.WriteAllText(path: path, contents: ClusterModelArtifactSerializer.Serialize(artifact));
    }

    private static ClusterModelAdminGrpcService CreateService(
        string modelPath,
        IClusterTrainingService trainingService,
        int entryCount,
        int rowCount,
        int retentionDays = 30,
        int maxRows = 50_000)
    {
        return new ClusterModelAdminGrpcService(
            trainingService: trainingService,
            memoryEntryStore: new FakeMemoryEntryStore(entryCount),
            transcriptStore: new FakeTranscriptStore(rowCount),
            transcriptOptions: Options.Create(new TranscriptOptions
                { Enabled = true, RetentionDays = retentionDays, MaxRows = maxRows }),
            storageOptions: Options.Create(new StorageOptions { ClusterModelPath = modelPath }),
            logger: NullLogger<ClusterModelAdminGrpcService>.Instance);
    }

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

    private sealed class FakeTrainingService(ClusterTrainingOutcome outcome, int[]? progressTicks = null)
        : IClusterTrainingService
    {
        public Task<ClusterTrainingOutcome> RetrainAsync(IProgress<int>? bootstrapProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (progressTicks is not null)
                foreach (var tick in progressTicks)
                    bootstrapProgress?.Report(tick);

            return Task.FromResult(outcome);
        }
    }

    private sealed class FakeMemoryEntryStore(int entryCount) : IMemoryEntryStore
    {
        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MemoryEntry> entries =
            [
                .. Enumerable.Range(0, count: entryCount)
                    .Select(i => new MemoryEntry(Id: i, TaskEmbedding: [1, 0], ChosenModel: "model-a", 1.0, 0.01, null,
                        CreatedAtUtc: DateTimeOffset.UtcNow))
            ];
            return Task.FromResult(entries);
        }

        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeTranscriptStore(int rowCount) : ITranscriptStore
    {
        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateOutcomeAsync(string correlationId, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(rowCount);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}