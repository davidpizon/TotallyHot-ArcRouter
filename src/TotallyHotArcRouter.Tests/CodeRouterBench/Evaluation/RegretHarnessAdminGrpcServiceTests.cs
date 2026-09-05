using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="RegretHarnessAdminGrpcService"/>: reading the "no run yet" and completed states, and
/// streaming a run's coarse stage progress plus its terminal outcome. Unit-tested directly against a
/// <see cref="TestServerCallContext"/> and an in-memory <see cref="IServerStreamWriter{T}"/> fake, the
/// same style as <c>LogRegModelAdminGrpcServiceTests</c>.
/// </summary>
public class RegretHarnessAdminGrpcServiceTests
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
    public async Task GetRegretHarnessStatus_NoRunYet_ReportsHasRunFalse()
    {
        var service = new RegretHarnessAdminGrpcService(new FakeRunner(outcome: null));

        var response = await service.GetRegretHarnessStatus(request: new Contract.GetRegretHarnessStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.False(response.HasRun);
    }

    [Fact]
    public async Task GetRegretHarnessStatus_RunCompletedPreviously_ReportsItsSplitsAndMessage()
    {
        var lastResult = Completed(splitName: "ID test", markdown: "| Router | CumReg |");
        var service = new RegretHarnessAdminGrpcService(new FakeRunner(lastResult));

        var response = await service.GetRegretHarnessStatus(request: new Contract.GetRegretHarnessStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.True(response.HasRun);
        Assert.Equal(expected: lastResult.Message, actual: response.Message);
        var split = Assert.Single(response.Splits);
        Assert.Equal(expected: "ID test", actual: split.SplitName);
        Assert.Equal(expected: "| Router | CumReg |", actual: split.MarkdownTable);
    }

    [Fact]
    public async Task RunRegretHarness_Completed_StreamsStageProgressThenCompletedResult()
    {
        var stages = new[]
        {
            RegretHarnessStage.LoadingCorpus, RegretHarnessStage.TrainingLogReg,
            RegretHarnessStage.BuildingKnnIndex, RegretHarnessStage.BuildingOrchestratorArm,
            RegretHarnessStage.BuildingReports
        };
        var outcome = Completed(splitName: "OOD", markdown: "| Router | CumReg |");
        var runner = new FakeRunner(outcome, stages);
        var service = new RegretHarnessAdminGrpcService(runner);
        var writer = new FakeServerStreamWriter<Contract.RegretHarnessStreamEvent>();

        await service.RunRegretHarness(request: new Contract.RunRegretHarnessRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var progressEvents = writer.Written
            .Where(e => e.EventCase == Contract.RegretHarnessStreamEvent.EventOneofCase.StageProgress)
            .Select(e => e.StageProgress.Stage)
            .ToList();
        Assert.Equal(expected:
        [
            Contract.RegretHarnessStage.LoadingCorpus, Contract.RegretHarnessStage.TrainingLogReg,
            Contract.RegretHarnessStage.BuildingKnnIndex, Contract.RegretHarnessStage.BuildingOrchestratorArm,
            Contract.RegretHarnessStage.BuildingReports
        ], actual: progressEvents);

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.RegretHarnessStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.RegretHarnessRunResultKind.Completed, actual: result.Kind);
        Assert.Equal(expected: outcome.Message, actual: result.Message);
        var split = Assert.Single(result.Splits);
        Assert.Equal(expected: "OOD", actual: split.SplitName);
        Assert.Same(expected: writer.Written[^1], actual: writer.Written.Last());
    }

    [Fact]
    public async Task RunRegretHarness_Declined_StreamsDeclinedResultRatherThanThrowing()
    {
        var outcome = new RegretHarnessRunResult(RegretHarnessRunResultKind.Declined, Message: "corpus not synced",
            null, Splits: []);
        var service = new RegretHarnessAdminGrpcService(new FakeRunner(outcome));
        var writer = new FakeServerStreamWriter<Contract.RegretHarnessStreamEvent>();

        await service.RunRegretHarness(request: new Contract.RunRegretHarnessRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.RegretHarnessStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.RegretHarnessRunResultKind.Declined, actual: result.Kind);
        Assert.Empty(result.Splits);
    }

    [Fact]
    public async Task RunRegretHarness_AlreadyRunning_StreamsAlreadyRunningResult()
    {
        var outcome = new RegretHarnessRunResult(RegretHarnessRunResultKind.AlreadyRunning, Message: "busy", null,
            Splits: []);
        var service = new RegretHarnessAdminGrpcService(new FakeRunner(outcome));
        var writer = new FakeServerStreamWriter<Contract.RegretHarnessStreamEvent>();

        await service.RunRegretHarness(request: new Contract.RunRegretHarnessRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var result = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.RegretHarnessStreamEvent.EventOneofCase.Result).Result;
        Assert.Equal(expected: Contract.RegretHarnessRunResultKind.AlreadyRunning, actual: result.Kind);
    }

    private static RegretHarnessRunResult Completed(string splitName, string markdown)
    {
        return new RegretHarnessRunResult(RegretHarnessRunResultKind.Completed, Message: "Completed: 1 task(s).",
            RanAtUtc: DateTimeOffset.UtcNow, Splits: [new RegretHarnessSplitReport(splitName, markdown)]);
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

    private sealed class FakeRunner(RegretHarnessRunResult? outcome, RegretHarnessStage[]? stageTicks = null)
        : IRegretHarnessRunner
    {
        public RegretHarnessRunResult? LastResult => outcome;

        public Task<RegretHarnessRunResult> RunAsync(
            IProgress<RegretHarnessStage>? stageProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (stageTicks is not null)
                foreach (var stage in stageTicks)
                    stageProgress?.Report(stage);

            return Task.FromResult(outcome ?? throw new InvalidOperationException("No outcome configured."));
        }
    }
}
