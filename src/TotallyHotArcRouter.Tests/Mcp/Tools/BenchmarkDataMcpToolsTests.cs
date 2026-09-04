using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Mcp.Tools;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Mcp.Tools;

/// <summary>Covers <see cref="BenchmarkDataMcpTools"/>: delegation to the status service and sync service.</summary>
public sealed class BenchmarkDataMcpToolsTests
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

    [Fact]
    public async Task GetBenchmarkDataStatusAsync_NoPriorCheck_RunsOneAndReturnsIt()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger();
        var statusService = CreateStatusService(temp: temp, handler: FakeHttpMessageHandler.AlwaysFails());
        var tools = new BenchmarkDataMcpTools(statusService: statusService,
            syncService: CreateSyncService(temp: temp, servedBodies: new Dictionary<string, string>()),
            options: Options.Create(new BenchmarkSyncOptions()));

        var status = await tools.GetBenchmarkDataStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: BenchmarkDataState.CheckFailed, actual: status.State);
        Assert.Same(expected: status, actual: statusService.Current);
    }

    [Fact]
    public async Task GetBenchmarkDataStatusAsync_PriorCheckExists_ReturnsCachedValueWithoutReprobing()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger();
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var statusService = CreateStatusService(temp: temp, handler: handler);
        await statusService.RecheckAsync(TestContext.Current.CancellationToken);
        var tools = new BenchmarkDataMcpTools(statusService: statusService,
            syncService: CreateSyncService(temp: temp, servedBodies: new Dictionary<string, string>()),
            options: Options.Create(new BenchmarkSyncOptions()));

        await tools.GetBenchmarkDataStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: callCount);
    }

    [Fact]
    public async Task SyncBenchmarkDataAsync_ImportsEveryFileAndRecordsTheLedger()
    {
        using var temp = new TempBenchmarkDatabase();
        var statusService = CreateStatusService(temp: temp, handler: FakeHttpMessageHandler.AlwaysFails());
        var syncService = CreateSyncService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123");
        var tools = new BenchmarkDataMcpTools(statusService: statusService, syncService: syncService,
            options: Options.Create(new BenchmarkSyncOptions()));

        var result = await tools.SyncBenchmarkDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: "commit123", actual: result.RepoCommit);
        Assert.All(collection: result.Files,
            action: outcome => Assert.True(condition: outcome.Succeeded,
                userMessage: $"{outcome.FileName}: {outcome.ErrorMessage}"));

        var ledger = temp.CreateLedger();
        Assert.Equal(expected: TestFileSpecs.Count, actual: ledger.GetAll().Count);
    }

    private static BenchmarkDataStatusService CreateStatusService(TempBenchmarkDatabase temp,
        HttpMessageHandler handler)
    {
        var probe = new BenchmarkChecksumProbe(httpClientFactory: new FakeHttpClientFactory(handler),
            logger: NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = new BenchmarkFileLedger(temp.Database);
        return new BenchmarkDataStatusService(
            probe: probe, ledger: ledger, options: Options.Create(new BenchmarkSyncOptions()),
            logger: NullLogger<BenchmarkDataStatusService>.Instance);
    }

    private static BenchmarkSyncService CreateSyncService(
        TempBenchmarkDatabase temp,
        IReadOnlyDictionary<string, string> servedBodies,
        string repoCommit = "commit")
    {
        var publishedOids = servedBodies.ToDictionary(
            keySelector: kvp => kvp.Key,
            elementSelector: kvp => GitBlobHash.Compute(Encoding.UTF8.GetBytes(kvp.Value)));

        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains(value: "/api/datasets/", comparisonType: StringComparison.Ordinal))
            {
                var treeEntries = publishedOids.Select(kvp =>
                    $$"""{ "type": "file", "path": "{{kvp.Key}}", "oid": "{{kvp.Value}}", "size": {{Encoding.UTF8.GetByteCount(servedBodies[kvp.Key])}} }""");
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

        var httpClientFactory = new FakeHttpClientFactory(handler);
        var probe = new BenchmarkChecksumProbe(httpClientFactory: httpClientFactory,
            logger: NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = temp.CreateLedger();
        return new BenchmarkSyncService(
            httpClientFactory: httpClientFactory, probe: probe, database: temp.Database, ledger: ledger,
            logger: NullLogger<BenchmarkSyncService>.Instance, fileSpecs: TestFileSpecs);
    }
}