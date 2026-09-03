using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.CodeRouterBench;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Covers <see cref="BenchmarkDataAdminGrpcService"/>: reading the cached and freshly-probed status,
/// listing every <see cref="BenchmarkFileSpec.All"/> file regardless of sync history, and streaming a
/// sync's per-file progress plus its terminal aggregate status. Unit-tested directly against a
/// <see cref="TestServerCallContext"/> and an in-memory <see cref="IServerStreamWriter{T}"/> fake, the
/// same style as <c>TelemetryGrpcServiceTests</c>.
/// </summary>
public class BenchmarkDataAdminGrpcServiceTests
{
    private static readonly IReadOnlyList<BenchmarkFileSpec> TestFileSpecs =
    [
        new(FileName: "id_probing_results_long.csv", Kind: BenchmarkFileKind.IdResultsCsv, Split: "probing", 1),
        new(FileName: "id_test_results_long.csv", Kind: BenchmarkFileKind.IdResultsCsv, Split: "id_test", 1),
        new(FileName: "ood176_results_long.csv", Kind: BenchmarkFileKind.OodResultsCsv, null, 1),
        new(FileName: "id_probing_tasks.jsonl", Kind: BenchmarkFileKind.IdTasksJsonl, Split: "probing", 1),
        new(FileName: "id_test_tasks.jsonl", Kind: BenchmarkFileKind.IdTasksJsonl, Split: "id_test", 1),
        new(FileName: "ood176_tasks.jsonl", Kind: BenchmarkFileKind.OodTasksJsonl, null, 1),
        new(FileName: "models.json", Kind: BenchmarkFileKind.ModelsJson, null, null),
        new(FileName: "summary.json", Kind: BenchmarkFileKind.SummaryJson, null, null)
    ];

    private static readonly Dictionary<string, string> Fixtures = new()
    {
        ["id_probing_results_long.csv"] = "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\n",
        ["id_test_results_long.csv"] = "task_id,dimension,model,score\nt3,code_generation,claude-opus-4-6,1.0\n",
        ["ood176_results_long.csv"] =
            "task_id,source_split,bench,dimension,model\nt4,ood,swebench,code_generation,claude-opus-4-6\n",
        ["id_probing_tasks.jsonl"] = """{"task_id":"t1","dimension":"code_generation"}""" + "\n",
        ["id_test_tasks.jsonl"] = """{"task_id":"t3","dimension":"code_generation"}""" + "\n",
        ["ood176_tasks.jsonl"] = """{"task_id":"t4","bench":"swebench","dimension":"code_generation"}""" + "\n",
        ["models.json"] = """{ "claude-opus-4-6": { "provider": "anthropic" } }""",
        ["summary.json"] = """{ "total_tasks": 4 }"""
    };

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
    public async Task GetBenchmarkStatus_BeforeAnyRecheck_RunsOneAndReturnsEveryFile()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger(); // schema only, no rows synced yet
        var (service, _) = CreateService(temp: temp, handler: FakeHttpMessageHandler.AlwaysFails());

        var response = await service.GetBenchmarkStatus(request: new Contract.GetBenchmarkStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(expected: Contract.BenchmarkDataState.CheckFailed, actual: response.State);
        Assert.True(response.HasReason);
        Assert.Equal(expected: BenchmarkFileSpec.All.Count, actual: response.Files.Count);
        Assert.All(collection: response.Files, action: f => Assert.False(f.Synced));
    }

    [Fact]
    public async Task RecheckBenchmarkData_SomeFilesSynced_ReportsUpdateAndProjectsEveryFile()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        var syncedAt = DateTimeOffset.UtcNow;
        ledger.Upsert(new BenchmarkFileLedgerEntry(FileName: "models.json", PublishedOid: "oid-models", 42, 3,
            RepoCommit: "commit1", SyncedAtUtc: syncedAt));

        var (service, _) = CreateService(temp: temp, handler: FakeHttpMessageHandler.AlwaysFails());

        var response = await service.RecheckBenchmarkData(request: new Contract.RecheckBenchmarkDataRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(expected: Contract.BenchmarkDataState.CheckFailed, actual: response.State);
        var modelsFile = Assert.Single(collection: response.Files, predicate: f => f.FileName == "models.json");
        Assert.True(modelsFile.Synced);
        Assert.Equal(42, actual: modelsFile.SizeBytes);
        Assert.Equal(3, actual: modelsFile.RowCount);

        var neverSynced = Assert.Single(collection: response.Files, predicate: f => f.FileName == "summary.json");
        Assert.False(neverSynced.Synced);
        Assert.Equal(0, actual: neverSynced.RowCount);
    }

    [Fact]
    public async Task SyncBenchmarkData_AllFilesValid_StreamsCompletedProgressThenCurrentFinalStatus()
    {
        using var temp = new TempBenchmarkDatabase();
        var (service, _) = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123");
        var writer = new FakeServerStreamWriter<Contract.BenchmarkSyncStreamEvent>();

        await service.SyncBenchmarkData(request: new Contract.SyncBenchmarkDataRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var completedEvents = writer.Written
            .Where(e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.Progress)
            .Select(e => e.Progress)
            .Where(p => p.Stage == Contract.BenchmarkSyncStage.Completed)
            .ToList();
        Assert.Equal(expected: TestFileSpecs.Count, actual: completedEvents.Count);

        var finalEvent = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.FinalStatus);
        Assert.Equal(expected: Contract.BenchmarkDataState.Current, actual: finalEvent.FinalStatus.State);
        Assert.All(collection: finalEvent.FinalStatus.Files, action: f => Assert.True(f.Synced));

        var ledger = temp.CreateLedger();
        Assert.Equal(expected: TestFileSpecs.Count, actual: ledger.GetAll().Count);
    }

    [Fact]
    public async Task SyncBenchmarkData_ChecksumMismatch_StreamsFailedProgressWithError()
    {
        using var temp = new TempBenchmarkDatabase();
        var servedBodies = new Dictionary<string, string>(Fixtures)
        {
            ["models.json"] = """{ "tampered-after-checksum-was-published": {} }"""
        };
        var (service, _) = CreateService(temp: temp, servedBodies: servedBodies, repoCommit: "commit123",
            publishedFixtures: Fixtures);
        var writer = new FakeServerStreamWriter<Contract.BenchmarkSyncStreamEvent>();

        await service.SyncBenchmarkData(request: new Contract.SyncBenchmarkDataRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var failedWithError = writer.Written
            .Where(e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.Progress)
            .Select(e => e.Progress)
            .Single(p => p.FileName == "models.json" && p.Stage == Contract.BenchmarkSyncStage.Failed && p.HasError);
        Assert.Contains(expectedSubstring: "checksum", actualString: failedWithError.Error,
            comparisonType: StringComparison.OrdinalIgnoreCase);

        var finalEvent = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.FinalStatus);
        Assert.Equal(expected: Contract.BenchmarkDataState.Update, actual: finalEvent.FinalStatus.State);
    }

    [Fact]
    public async Task SyncBenchmarkData_AllFilesStale_StreamsThePlanFirstListingEveryStaleFile()
    {
        using var temp = new TempBenchmarkDatabase();
        var (service, _) = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123");
        var writer = new FakeServerStreamWriter<Contract.BenchmarkSyncStreamEvent>();

        await service.SyncBenchmarkData(request: new Contract.SyncBenchmarkDataRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var firstEvent = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.Plan);
        Assert.Same(expected: writer.Written[0], actual: firstEvent);
        Assert.Equal(expected: TestFileSpecs.Count, actual: firstEvent.Plan.Files.Count);
        Assert.Equal(expected: firstEvent.Plan.Files.Sum(f => f.SizeBytes), actual: firstEvent.Plan.TotalBytes);
        Assert.True(firstEvent.Plan.TotalBytes > 0);
    }

    [Fact]
    public async Task SyncBenchmarkData_FileAlreadyCurrent_IsOmittedFromThePlanAndStreamsNoFailedEvent()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        var publishedOid = GitBlobHash.Compute(Encoding.UTF8.GetBytes(Fixtures["models.json"]));
        ledger.Upsert(new BenchmarkFileLedgerEntry(FileName: "models.json", PublishedOid: publishedOid, 42, 1,
            RepoCommit: "old-commit", SyncedAtUtc: DateTimeOffset.UtcNow));
        var (service, _) = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123");
        var writer = new FakeServerStreamWriter<Contract.BenchmarkSyncStreamEvent>();

        await service.SyncBenchmarkData(request: new Contract.SyncBenchmarkDataRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var plan = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.Plan).Plan;
        Assert.DoesNotContain(collection: plan.Files, filter: f => f.FileName == "models.json");
        Assert.Equal(expected: TestFileSpecs.Count - 1, actual: plan.Files.Count);

        Assert.DoesNotContain(
            collection: writer.Written,
            filter: e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.Progress &&
                         e.Progress.FileName == "models.json" &&
                         e.Progress.Stage == Contract.BenchmarkSyncStage.Failed);

        var finalEvent = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.FinalStatus);
        Assert.Equal(expected: Contract.BenchmarkDataState.Current, actual: finalEvent.FinalStatus.State);
    }

    private static (BenchmarkDataAdminGrpcService Service, BenchmarkSyncService SyncService) CreateService(
        TempBenchmarkDatabase temp,
        HttpMessageHandler handler)
    {
        var probe = new BenchmarkChecksumProbe(httpClientFactory: new FakeHttpClientFactory(handler),
            logger: NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = new BenchmarkFileLedger(temp.Database);
        var statusService = new BenchmarkDataStatusService(
            probe: probe, ledger: ledger, options: Options.Create(new BenchmarkSyncOptions()),
            logger: NullLogger<BenchmarkDataStatusService>.Instance);
        var syncService = new BenchmarkSyncService(
            httpClientFactory: new FakeHttpClientFactory(handler), probe: probe, database: temp.Database,
            ledger: ledger, logger: NullLogger<BenchmarkSyncService>.Instance, fileSpecs: TestFileSpecs);

        return (
            new BenchmarkDataAdminGrpcService(statusService: statusService, ledger: ledger, syncService: syncService,
                options: Options.Create(new BenchmarkSyncOptions())), syncService);
    }

    private static (BenchmarkDataAdminGrpcService Service, BenchmarkSyncService SyncService) CreateService(
        TempBenchmarkDatabase temp,
        IReadOnlyDictionary<string, string> servedBodies,
        string repoCommit,
        IReadOnlyDictionary<string, string>? publishedFixtures = null)
    {
        var oidSourceBodies = publishedFixtures ?? servedBodies;
        var publishedOids = oidSourceBodies.ToDictionary(
            keySelector: kvp => kvp.Key,
            elementSelector: kvp => GitBlobHash.Compute(Encoding.UTF8.GetBytes(kvp.Value)));

        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains(value: "/api/datasets/", comparisonType: StringComparison.Ordinal))
            {
                var treeEntries = publishedOids.Select(kvp =>
                    $$"""{ "type": "file", "path": "{{kvp.Key}}", "oid": "{{kvp.Value}}", "size": {{Encoding.UTF8.GetByteCount(oidSourceBodies[kvp.Key])}} }""");
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content: $"[{string.Join(',', values: treeEntries)}]",
                        encoding: Encoding.UTF8, mediaType: "application/json")
                };
                response.Headers.Add(name: "X-Repo-Commit", value: repoCommit);
                return response;
            }

            var fileName = path[(path.LastIndexOf('/') + 1)..];
            return servedBodies.TryGetValue(key: fileName, value: out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(content: body, encoding: Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        return CreateService(temp: temp, handler: handler);
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
}