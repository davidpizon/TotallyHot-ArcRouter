using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private sealed class FakeTrainingService(LogRegTrainingOutcome outcome, int[]? progressTicks = null) : IEmbeddingLogRegTrainingService
    {
        public Task<LogRegTrainingOutcome> RetrainAsync(IProgress<int>? bootstrapProgress = null, CancellationToken cancellationToken = default)
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
    public async Task GetLogRegModelStatus_NoArtifactOnDisk_ReportsArtifactAbsentAndEveryLiveEntryAsSinceLastRetrain()
    {
        var modelPath = TempModelPath();
        var service = CreateService(modelPath, new FakeTrainingService(Declined()), entryCount: 7);

        var response = await service.GetLogRegModelStatus(new Contract.GetLogRegModelStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.False(response.ArtifactPresent);
        Assert.Equal(0, response.EmbeddingDimension);
        Assert.Equal(7, response.EntriesSinceLastRetrain);
        Assert.Equal(0, response.ModelsRepresented);
    }

    [Fact]
    public async Task GetLogRegModelStatus_ArtifactOnDisk_ReportsProvenanceAndEntriesSinceLastRetrain()
    {
        var modelPath = TempModelPath();
        WriteArtifact(modelPath, embeddingDimension: 2, memoryEntryCount: 5, bootstrapTaskCount: 20);
        var service = CreateService(modelPath, new FakeTrainingService(Declined()), entryCount: 9);

        var response = await service.GetLogRegModelStatus(new Contract.GetLogRegModelStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.True(response.ArtifactPresent);
        Assert.Equal(2, response.EmbeddingDimension);
        Assert.Equal(20, response.BootstrapTaskCount);
        Assert.Equal(5, response.MemoryEntryCount);
        Assert.Equal(4, response.EntriesSinceLastRetrain); // 9 live entries now, 5 at training time
        Assert.Equal(2, response.ModelsRepresented);
        Assert.NotNull(response.TrainedAtUtc);
    }

    [Fact]
    public async Task GetLogRegModelStatus_AlwaysReportsRetrainConfigurationContext()
    {
        var modelPath = TempModelPath();
        var service = CreateService(
            modelPath, new FakeTrainingService(Declined()), entryCount: 0,
            routingOptions: new RoutingOptions { LogRegRetrainThreshold = 250, LogRegLiveSampleWeight = 2.5 });

        var response = await service.GetLogRegModelStatus(new Contract.GetLogRegModelStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(250, response.RetrainThreshold);
        Assert.Equal(2.5, response.LiveSampleWeight);
    }

    [Fact]
    public async Task RetrainLogRegModel_Trained_StreamsBootstrapProgressThenTrainedResultWithFreshStatus()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(Trained("wrote 2 heads"), progressTicks: [1, 2, 3]);
        var service = CreateService(modelPath, trainingService, entryCount: 0);
        var writer = new FakeServerStreamWriter<Contract.LogRegRetrainStreamEvent>();

        await service.RetrainLogRegModel(new Contract.RetrainLogRegModelRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var progressEvents = writer.Written
            .Where(e => e.EventCase == Contract.LogRegRetrainStreamEvent.EventOneofCase.BootstrapProgress)
            .Select(e => e.BootstrapProgress.TasksEmbedded)
            .ToList();
        Assert.Equal([1, 2, 3], progressEvents);

        var result = Assert.Single(writer.Written, e => e.EventCase == Contract.LogRegRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(Contract.LogRegRetrainResultKind.Trained, result.Kind);
        Assert.Equal("wrote 2 heads", result.Message);
        Assert.Same(writer.Written[^1], writer.Written.Last());
    }

    [Fact]
    public async Task RetrainLogRegModel_Declined_StreamsDeclinedResultRatherThanThrowing()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(Declined());
        var service = CreateService(modelPath, trainingService, entryCount: 1);
        var writer = new FakeServerStreamWriter<Contract.LogRegRetrainStreamEvent>();

        await service.RetrainLogRegModel(new Contract.RetrainLogRegModelRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(writer.Written, e => e.EventCase == Contract.LogRegRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(Contract.LogRegRetrainResultKind.Declined, result.Kind);
        Assert.False(result.Status.ArtifactPresent);
    }

    [Fact]
    public async Task RetrainLogRegModel_AlreadyRunning_StreamsAlreadyRunningResult()
    {
        var modelPath = TempModelPath();
        var trainingService = new FakeTrainingService(new LogRegTrainingOutcome(LogRegTrainingResultKind.AlreadyRunning, "busy", 0, 0, 0, 0));
        var service = CreateService(modelPath, trainingService, entryCount: 0);
        var writer = new FakeServerStreamWriter<Contract.LogRegRetrainStreamEvent>();

        await service.RetrainLogRegModel(new Contract.RetrainLogRegModelRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(writer.Written, e => e.EventCase == Contract.LogRegRetrainStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(Contract.LogRegRetrainResultKind.AlreadyRunning, result.Kind);
    }

    private static LogRegTrainingOutcome Trained(string message) =>
        new(LogRegTrainingResultKind.Trained, message, 20, 5, 25, 2);

    private static LogRegTrainingOutcome Declined() =>
        new(LogRegTrainingResultKind.Declined, "Declined: not enough samples.", 0, 1, 1, 0);

    private static string TempModelPath() =>
        Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "logreg_voter_model.json");

    private static void WriteArtifact(string path, int embeddingDimension, int memoryEntryCount, int bootstrapTaskCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var artifact = new EmbeddingLogRegModelArtifact(
            EmbeddingDimension: embeddingDimension,
            ClassWeights: new Dictionary<string, double[]>
            {
                ["model-a"] = new double[embeddingDimension + 1],
                ["model-b"] = new double[embeddingDimension + 1],
            },
            TrainedFrom: "bootstrap_tasks=20, memory_entries=5",
            BootstrapTaskCount: bootstrapTaskCount,
            MemoryEntryCount: memoryEntryCount);
        File.WriteAllText(path, EmbeddingLogRegModelArtifactSerializer.Serialize(artifact));
    }

    private static LogRegModelAdminGrpcService CreateService(
        string modelPath,
        IEmbeddingLogRegTrainingService trainingService,
        int entryCount,
        RoutingOptions? routingOptions = null) =>
        new(
            trainingService,
            new FakeMemoryEntryStore(entryCount),
            Options.Create(routingOptions ?? new RoutingOptions()),
            Options.Create(new StorageOptions { LogRegModelPath = modelPath }),
            NullLogger<LogRegModelAdminGrpcService>.Instance);
}
