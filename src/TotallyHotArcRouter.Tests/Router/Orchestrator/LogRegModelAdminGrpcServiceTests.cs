using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="LogRegModelAdminGrpcService"/>: reading the "no artifact yet" and trained states, and
/// streaming a retrain's bootstrap progress plus its terminal outcome and fresh status. Unit-tested
/// directly against a <see cref="TestServerCallContext"/> and an in-memory <see cref="IServerStreamWriter{T}"/>
/// fake, the same style as <c>ClusterModelAdminGrpcServiceTests</c>.
/// </summary>
public class LogRegModelAdminGrpcServiceTests
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
    public async Task GetLogRegModelStatus_NoArtifactOnDisk_ReportsArtifactAbsentAndEveryLiveEntryAsSinceLastRetrain()
    {
        var modelPath = TempModelPath();
        var service = CreateService(modelPath: modelPath, trainingService: new FakeTrainingService(Declined()), 7);

        var response = await service.GetLogRegModelStatus(request: new Contract.GetLogRegModelStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.False(response.ArtifactPresent);
        Assert.Equal(0, actual: response.EmbeddingDimension);
        Assert.Equal(7, actual: response.EntriesSinceLastRetrain);
        Assert.Equal(0, actual: response.ModelsRepresented);
    }

    [Fact]
    public async Task GetLogRegModelStatus_ArtifactOnDisk_ReportsProvenanceAndEntriesSinceLastRetrain()
    {
        var modelPath = TempModelPath();
        WriteArtifact(path: modelPath, 2, 5, 20);
        var service = CreateService(modelPath: modelPath, trainingService: new FakeTrainingService(Declined()), 9);

        var response = await service.GetLogRegModelStatus(request: new Contract.GetLogRegModelStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.True(response.ArtifactPresent);
        Assert.Equal(2, actual: response.EmbeddingDimension);
        Assert.Equal(20, actual: response.BootstrapTaskCount);
        Assert.Equal(5, actual: response.MemoryEntryCount);
        Assert.Equal(4, actual: response.EntriesSinceLastRetrain); // 9 live entries now, 5 at training time
        Assert.Equal(2, actual: response.ModelsRepresented);
        Assert.NotNull(response.TrainedAtUtc);
    }

    [Fact]
    public async Task GetLogRegModelStatus_AlwaysReportsRetrainConfigurationContext()
    {
        var modelPath = TempModelPath();
        var service = CreateService(
            modelPath: modelPath, trainingService: new FakeTrainingService(Declined()), 0,
            routingOptions: new RoutingOptions { LogRegRetrainThreshold = 250, LogRegLiveSampleWeight = 2.5 });

        var response = await service.GetLogRegModelStatus(request: new Contract.GetLogRegModelStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(250, actual: response.RetrainThreshold);
        Assert.Equal(2.5, actual: response.LiveSampleWeight);
    }

    [Fact]
    public async Task RetrainLogRegModel_Trained_StreamsBootstrapProgressThenTrainedResultWithFreshStatus()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(outcome: Trained("wrote 2 heads"), progressTicks: [1, 2, 3]);
        var service = CreateService(modelPath: modelPath, trainingService: trainingService, 0);
        var writer = new FakeServerStreamWriter<Contract.LogRegRetrainStreamEvent>();

        await service.RetrainLogRegModel(request: new Contract.RetrainLogRegModelRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var progressEvents = writer.Written
            .Where(e => e.EventCase == Contract.LogRegRetrainStreamEvent.EventOneofCase.BootstrapProgress)
            .Select(e => e.BootstrapProgress.TasksEmbedded)
            .ToList();
        Assert.Equal(expected: [1, 2, 3], actual: progressEvents);

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.LogRegRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.LogRegRetrainResultKind.Trained, actual: result.Kind);
        Assert.Equal(expected: "wrote 2 heads", actual: result.Message);
        Assert.Same(expected: writer.Written[^1], actual: writer.Written.Last());
    }

    [Fact]
    public async Task RetrainLogRegModel_Declined_StreamsDeclinedResultRatherThanThrowing()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(Declined());
        var service = CreateService(modelPath: modelPath, trainingService: trainingService, 1);
        var writer = new FakeServerStreamWriter<Contract.LogRegRetrainStreamEvent>();

        await service.RetrainLogRegModel(request: new Contract.RetrainLogRegModelRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.LogRegRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.LogRegRetrainResultKind.Declined, actual: result.Kind);
        Assert.False(result.Status.ArtifactPresent);
    }

    [Fact]
    public async Task RetrainLogRegModel_AlreadyRunning_StreamsAlreadyRunningResult()
    {
        var modelPath = TempModelPath();
        var trainingService =
            new FakeTrainingService(new LogRegTrainingOutcome(Kind: LogRegTrainingResultKind.AlreadyRunning,
                Message: "busy", 0, 0, 0, 0));
        var service = CreateService(modelPath: modelPath, trainingService: trainingService, 0);
        var writer = new FakeServerStreamWriter<Contract.LogRegRetrainStreamEvent>();

        await service.RetrainLogRegModel(request: new Contract.RetrainLogRegModelRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.LogRegRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.LogRegRetrainResultKind.AlreadyRunning, actual: result.Kind);
    }

    private static LogRegTrainingOutcome Trained(string message)
    {
        return new LogRegTrainingOutcome(Kind: LogRegTrainingResultKind.Trained, Message: message, 20, 5, 25, 2);
    }

    private static LogRegTrainingOutcome Declined()
    {
        return new LogRegTrainingOutcome(Kind: LogRegTrainingResultKind.Declined,
            Message: "Declined: not enough samples.", 0, 1, 1, 0);
    }

    private static string TempModelPath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests", path3: Guid.NewGuid().ToString("N"),
            path4: "logreg_voter_model.json");
    }

    private static void WriteArtifact(string path, int embeddingDimension, int memoryEntryCount, int bootstrapTaskCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var artifact = new EmbeddingLogRegModelArtifact(
            EmbeddingDimension: embeddingDimension,
            ClassWeights: new Dictionary<string, double[]>
            {
                ["model-a"] = new double[embeddingDimension + 1],
                ["model-b"] = new double[embeddingDimension + 1]
            },
            TrainedFrom: "bootstrap_tasks=20, memory_entries=5",
            BootstrapTaskCount: bootstrapTaskCount,
            MemoryEntryCount: memoryEntryCount);
        File.WriteAllText(path: path, contents: EmbeddingLogRegModelArtifactSerializer.Serialize(artifact));
    }

    private static LogRegModelAdminGrpcService CreateService(
        string modelPath,
        IEmbeddingLogRegTrainingService trainingService,
        int entryCount,
        RoutingOptions? routingOptions = null)
    {
        return new LogRegModelAdminGrpcService(
            trainingService: trainingService,
            memoryEntryStore: new FakeMemoryEntryStore(entryCount),
            routingOptions: Options.Create(routingOptions ?? new RoutingOptions()),
            storageOptions: Options.Create(new StorageOptions { LogRegModelPath = modelPath }),
            logger: NullLogger<LogRegModelAdminGrpcService>.Instance);
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

    private sealed class FakeTrainingService(LogRegTrainingOutcome outcome, int[]? progressTicks = null)
        : IEmbeddingLogRegTrainingService
    {
        public Task<LogRegTrainingOutcome> RetrainAsync(IProgress<int>? bootstrapProgress = null,
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
}