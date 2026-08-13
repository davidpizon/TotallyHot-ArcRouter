using System.Net;
using System.Text;
using TotallyHot.ArcRouter.CodeRouterBench;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static readonly IReadOnlyList<BenchmarkFileSpec> TestFileSpecs =
    [
        new("id_probing_results_long.csv", BenchmarkFileKind.IdResultsCsv, "probing", 1),
        new("id_test_results_long.csv", BenchmarkFileKind.IdResultsCsv, "id_test", 1),
        new("ood176_results_long.csv", BenchmarkFileKind.OodResultsCsv, null, 1),
        new("id_probing_tasks.jsonl", BenchmarkFileKind.IdTasksJsonl, "probing", 1),
        new("id_test_tasks.jsonl", BenchmarkFileKind.IdTasksJsonl, "id_test", 1),
        new("ood176_tasks.jsonl", BenchmarkFileKind.OodTasksJsonl, null, 1),
        new("models.json", BenchmarkFileKind.ModelsJson, null, null),
        new("summary.json", BenchmarkFileKind.SummaryJson, null, null),
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
        ["summary.json"] = """{ "total_tasks": 4 }""",
    };

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
    public async Task GetBenchmarkStatus_BeforeAnyRecheck_RunsOneAndReturnsEveryFile()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger(); // schema only, no rows synced yet
        var (service, _) = CreateService(temp, FakeHttpMessageHandler.AlwaysFails());

        var response = await service.GetBenchmarkStatus(new Contract.GetBenchmarkStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(Contract.BenchmarkDataState.CheckFailed, response.State);
        Assert.True(response.HasReason);
        Assert.Equal(BenchmarkFileSpec.All.Count, response.Files.Count);
        Assert.All(response.Files, f => Assert.False(f.Synced));
    }

    [Fact]
    public async Task RecheckBenchmarkData_SomeFilesSynced_ReportsUpdateAndProjectsEveryFile()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        var syncedAt = DateTimeOffset.UtcNow;
        ledger.Upsert(new BenchmarkFileLedgerEntry("models.json", "oid-models", 42, 3, "commit1", syncedAt));

        var (service, _) = CreateService(temp, FakeHttpMessageHandler.AlwaysFails());

        var response = await service.RecheckBenchmarkData(new Contract.RecheckBenchmarkDataRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(Contract.BenchmarkDataState.CheckFailed, response.State);
        var modelsFile = Assert.Single(response.Files, f => f.FileName == "models.json");
        Assert.True(modelsFile.Synced);
        Assert.Equal(42, modelsFile.SizeBytes);
        Assert.Equal(3, modelsFile.RowCount);

        var neverSynced = Assert.Single(response.Files, f => f.FileName == "summary.json");
        Assert.False(neverSynced.Synced);
        Assert.Equal(0, neverSynced.RowCount);
    }

    [Fact]
    public async Task SyncBenchmarkData_AllFilesValid_StreamsCompletedProgressThenCurrentFinalStatus()
    {
        using var temp = new TempBenchmarkDatabase();
        var (service, _) = CreateService(temp, Fixtures, repoCommit: "commit123");
        var writer = new FakeServerStreamWriter<Contract.BenchmarkSyncStreamEvent>();

        await service.SyncBenchmarkData(new Contract.SyncBenchmarkDataRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var completedEvents = writer.Written
            .Where(e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.Progress)
            .Select(e => e.Progress)
            .Where(p => p.Stage == Contract.BenchmarkSyncStage.Completed)
            .ToList();
        Assert.Equal(TestFileSpecs.Count, completedEvents.Count);

        var finalEvent = Assert.Single(writer.Written, e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.FinalStatus);
        Assert.Equal(Contract.BenchmarkDataState.Current, finalEvent.FinalStatus.State);
        Assert.All(finalEvent.FinalStatus.Files, f => Assert.True(f.Synced));

        var ledger = temp.CreateLedger();
        Assert.Equal(TestFileSpecs.Count, ledger.GetAll().Count);
    }

    [Fact]
    public async Task SyncBenchmarkData_ChecksumMismatch_StreamsFailedProgressWithError()
    {
        using var temp = new TempBenchmarkDatabase();
        var servedBodies = new Dictionary<string, string>(Fixtures)
        {
            ["models.json"] = """{ "tampered-after-checksum-was-published": {} }""",
        };
        var (service, _) = CreateService(temp, servedBodies, repoCommit: "commit123", publishedFixtures: Fixtures);
        var writer = new FakeServerStreamWriter<Contract.BenchmarkSyncStreamEvent>();

        await service.SyncBenchmarkData(new Contract.SyncBenchmarkDataRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var failedWithError = writer.Written
            .Where(e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.Progress)
            .Select(e => e.Progress)
            .Single(p => p.FileName == "models.json" && p.Stage == Contract.BenchmarkSyncStage.Failed && p.HasError);
        Assert.Contains("checksum", failedWithError.Error, StringComparison.OrdinalIgnoreCase);

        var finalEvent = Assert.Single(writer.Written, e => e.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.FinalStatus);
        Assert.Equal(Contract.BenchmarkDataState.Update, finalEvent.FinalStatus.State);
    }

    private static (BenchmarkDataAdminGrpcService Service, BenchmarkSyncService SyncService) CreateService(
        TempBenchmarkDatabase temp,
        HttpMessageHandler handler)
    {
        var probe = new BenchmarkChecksumProbe(new FakeHttpClientFactory(handler), NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = new BenchmarkFileLedger(temp.Database);
        var statusService = new BenchmarkDataStatusService(
            probe, ledger, Options.Create(new BenchmarkSyncOptions()), NullLogger<BenchmarkDataStatusService>.Instance);
        var syncService = new BenchmarkSyncService(
            new FakeHttpClientFactory(handler), probe, temp.Database, ledger, NullLogger<BenchmarkSyncService>.Instance, TestFileSpecs);

        return (new BenchmarkDataAdminGrpcService(statusService, ledger, syncService, Options.Create(new BenchmarkSyncOptions())), syncService);
    }

    private static (BenchmarkDataAdminGrpcService Service, BenchmarkSyncService SyncService) CreateService(
        TempBenchmarkDatabase temp,
        IReadOnlyDictionary<string, string> servedBodies,
        string repoCommit,
        IReadOnlyDictionary<string, string>? publishedFixtures = null)
    {
        var oidSourceBodies = publishedFixtures ?? servedBodies;
        var publishedOids = oidSourceBodies.ToDictionary(
            kvp => kvp.Key,
            kvp => GitBlobHash.Compute(Encoding.UTF8.GetBytes(kvp.Value)));

        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/api/datasets/", StringComparison.Ordinal))
            {
                var treeEntries = publishedOids.Select(kvp =>
                    $$"""{ "type": "file", "path": "{{kvp.Key}}", "oid": "{{kvp.Value}}", "size": {{Encoding.UTF8.GetByteCount(oidSourceBodies[kvp.Key])}} }""");
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"[{string.Join(',', treeEntries)}]", Encoding.UTF8, "application/json"),
                };
                response.Headers.Add("X-Repo-Commit", repoCommit);
                return response;
            }

            var fileName = path[(path.LastIndexOf('/') + 1)..];
            return servedBodies.TryGetValue(fileName, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        return CreateService(temp, handler);
    }
}
