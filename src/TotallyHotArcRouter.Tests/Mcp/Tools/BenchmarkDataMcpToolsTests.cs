using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Mcp.Tools;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Mcp.Tools;

/// <summary>Covers <see cref="BenchmarkDataMcpTools"/>: delegation to the status service and sync service.</summary>
public sealed class BenchmarkDataMcpToolsTests
{
    [Fact]
    public async Task GetBenchmarkDataStatusAsync_NoPriorCheck_RunsOneAndReturnsIt()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger();
        var statusService = CreateStatusService(temp, FakeHttpMessageHandler.AlwaysFails());
        var tools = new BenchmarkDataMcpTools(statusService, CreateSyncService(temp, new Dictionary<string, string>()), Options.Create(new BenchmarkSyncOptions()));

        var status = await tools.GetBenchmarkDataStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BenchmarkDataState.CheckFailed, status.State);
        Assert.Same(status, statusService.Current);
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
        var statusService = CreateStatusService(temp, handler);
        await statusService.RecheckAsync(TestContext.Current.CancellationToken);
        var tools = new BenchmarkDataMcpTools(statusService, CreateSyncService(temp, new Dictionary<string, string>()), Options.Create(new BenchmarkSyncOptions()));

        await tools.GetBenchmarkDataStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task SyncBenchmarkDataAsync_ImportsEveryFileAndRecordsTheLedger()
    {
        using var temp = new TempBenchmarkDatabase();
        var statusService = CreateStatusService(temp, FakeHttpMessageHandler.AlwaysFails());
        var syncService = CreateSyncService(temp, Fixtures, repoCommit: "commit123");
        var tools = new BenchmarkDataMcpTools(statusService, syncService, Options.Create(new BenchmarkSyncOptions()));

        var result = await tools.SyncBenchmarkDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal("commit123", result.RepoCommit);
        Assert.All(result.Files, outcome => Assert.True(outcome.Succeeded, $"{outcome.FileName}: {outcome.ErrorMessage}"));

        var ledger = temp.CreateLedger();
        Assert.Equal(TestFileSpecs.Count, ledger.GetAll().Count);
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

    private static BenchmarkDataStatusService CreateStatusService(TempBenchmarkDatabase temp, HttpMessageHandler handler)
    {
        var probe = new BenchmarkChecksumProbe(new FakeHttpClientFactory(handler), NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = new BenchmarkFileLedger(temp.Database);
        return new BenchmarkDataStatusService(
            probe, ledger, Options.Create(new BenchmarkSyncOptions()), NullLogger<BenchmarkDataStatusService>.Instance);
    }

    private static BenchmarkSyncService CreateSyncService(
        TempBenchmarkDatabase temp,
        IReadOnlyDictionary<string, string> servedBodies,
        string repoCommit = "commit")
    {
        var publishedOids = servedBodies.ToDictionary(
            kvp => kvp.Key,
            kvp => GitBlobHash.Compute(Encoding.UTF8.GetBytes(kvp.Value)));

        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/api/datasets/", StringComparison.Ordinal))
            {
                var treeEntries = publishedOids.Select(kvp =>
                    $$"""{ "type": "file", "path": "{{kvp.Key}}", "oid": "{{kvp.Value}}", "size": {{Encoding.UTF8.GetByteCount(servedBodies[kvp.Key])}} }""");
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

        var httpClientFactory = new FakeHttpClientFactory(handler);
        var probe = new BenchmarkChecksumProbe(httpClientFactory, NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = temp.CreateLedger();
        return new BenchmarkSyncService(
            httpClientFactory, probe, temp.Database, ledger, NullLogger<BenchmarkSyncService>.Instance, TestFileSpecs);
    }
}
