using System.Net;
using System.Text;
using TotallyHot.ArcRouter.CodeRouterBench;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Covers <see cref="BenchmarkSyncService"/> end to end against a fake Hugging Face endpoint serving
/// small fixture bytes for all eight <see cref="BenchmarkFileSpec"/> files - the fake handler drives both
/// the tree-API probe and every per-file download, so the whole download -> verify -> import -> ledger
/// pipeline runs with no real network I/O. A small test-only <see cref="BenchmarkFileSpec"/> manifest
/// (matching these fixtures' actual row counts) is supplied to the service, since
/// <see cref="BenchmarkFileSpec.All"/>'s production row-count assertions (56,640 rows and up) are not
/// something a fast unit test fixture can satisfy.
/// </summary>
public class BenchmarkSyncServiceTests
{
    private static readonly Dictionary<string, string> Fixtures = new()
    {
        ["id_probing_results_long.csv"] =
            "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\nt2,bug_fixing,claude-opus-4-6,0.5\n",
        ["id_test_results_long.csv"] = "task_id,dimension,model,score\nt3,code_generation,claude-opus-4-6,1.0\n",
        ["ood176_results_long.csv"] =
            "task_id,source_split,bench,dimension,model\nt4,ood,swebench,code_generation,claude-opus-4-6\n",
        ["id_probing_tasks.jsonl"] = """{"task_id":"t1","dimension":"code_generation"}""" + "\n",
        ["id_test_tasks.jsonl"] = """{"task_id":"t3","dimension":"code_generation"}""" + "\n",
        ["ood176_tasks.jsonl"] = """{"task_id":"t4","bench":"swebench","dimension":"code_generation"}""" + "\n",
        ["models.json"] = """{ "claude-opus-4-6": { "provider": "anthropic" } }""",
        ["summary.json"] = """{ "total_tasks": 4 }""",
    };

    private static readonly IReadOnlyList<BenchmarkFileSpec> TestFileSpecs =
    [
        new("id_probing_results_long.csv", BenchmarkFileKind.IdResultsCsv, "probing", 2),
        new("id_test_results_long.csv", BenchmarkFileKind.IdResultsCsv, "id_test", 1),
        new("ood176_results_long.csv", BenchmarkFileKind.OodResultsCsv, null, 1),
        new("id_probing_tasks.jsonl", BenchmarkFileKind.IdTasksJsonl, "probing", 1),
        new("id_test_tasks.jsonl", BenchmarkFileKind.IdTasksJsonl, "id_test", 1),
        new("ood176_tasks.jsonl", BenchmarkFileKind.OodTasksJsonl, null, 1),
        new("models.json", BenchmarkFileKind.ModelsJson, null, null),
        new("summary.json", BenchmarkFileKind.SummaryJson, null, null),
    ];

    [Fact]
    public async Task SyncAsync_AllFilesValid_ImportsEveryFileAndRecordsTheLedger()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(temp, Fixtures, repoCommit: "commit123");

        var result = await service.SyncAsync("main", progress: null, TestContext.Current.CancellationToken);

        Assert.Equal("commit123", result.RepoCommit);
        Assert.Equal(8, result.Files.Count);
        Assert.All(result.Files, outcome => Assert.True(outcome.Succeeded, $"{outcome.FileName}: {outcome.ErrorMessage}"));

        var ledger = temp.CreateLedger();
        Assert.Equal(8, ledger.GetAll().Count);
        var modelsEntry = ledger.TryGet("models.json");
        Assert.NotNull(modelsEntry);
        Assert.Equal("commit123", modelsEntry!.RepoCommit);
    }

    [Fact]
    public async Task SyncAsync_ChecksumMismatch_LeavesTheLedgerRowAbsent()
    {
        using var temp = new TempBenchmarkDatabase();
        // Probe's published oid is computed from the *original* fixture; the served body is swapped -
        // exactly what a file tampered with (or truncated) after publication would look like.
        var servedBodies = new Dictionary<string, string>(Fixtures)
        {
            ["models.json"] = """{ "tampered-after-checksum-was-published": {} }""",
        };
        var service = CreateService(temp, servedBodies, repoCommit: "commit123", publishedFixtures: Fixtures);

        var result = await service.SyncAsync("main", progress: null, TestContext.Current.CancellationToken);

        var modelsOutcome = result.Files.Single(f => f.FileName == "models.json");
        Assert.False(modelsOutcome.Succeeded);
        Assert.Contains("checksum", modelsOutcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, result.Files.Count(f => f.Succeeded));

        var ledger = temp.CreateLedger();
        Assert.Null(ledger.TryGet("models.json"));
    }

    [Fact]
    public async Task SyncAsync_RowCountMismatch_RollsBackTheWholeFileImport()
    {
        using var temp = new TempBenchmarkDatabase();
        // Served (and published-checksum-matching) body has one row; the test manifest above expects two.
        var oneRowFixture = "task_id,dimension,model,score\nt1,code_generation,claude-opus-4-6,1.0\n";
        var servedBodies = new Dictionary<string, string>(Fixtures) { ["id_probing_results_long.csv"] = oneRowFixture };
        var service = CreateService(temp, servedBodies, repoCommit: "commit123");

        var result = await service.SyncAsync("main", progress: null, TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "id_probing_results_long.csv");
        Assert.False(outcome.Succeeded);
        Assert.Contains("data row", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_id_results WHERE split = 'probing';";
        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));

        var ledger = temp.CreateLedger();
        Assert.Null(ledger.TryGet("id_probing_results_long.csv"));
    }

    [Fact]
    public async Task SyncAsync_FileMissingFromPublishedTree_ReportsFailureWithoutThrowing()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(temp, Fixtures, repoCommit: "commit123", omitFromTree: "summary.json");

        var result = await service.SyncAsync("main", progress: null, TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "summary.json");
        Assert.False(outcome.Succeeded);
        Assert.Equal(7, result.Files.Count(f => f.Succeeded));
    }

    [Fact]
    public async Task SyncAsync_ReportsCompletedProgressForEveryFile()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(temp, Fixtures, repoCommit: "commit123");
        List<BenchmarkSyncProgress> updates = [];
        // A synchronous IProgress<T>, not System.Progress<T>: the latter marshals through
        // SynchronizationContext.Post, whose delivery timing relative to the awaited SyncAsync call
        // depends on whichever context the test runner happens to install - not guaranteed to have
        // drained by the time this method returns.
        var progress = new SynchronousProgress<BenchmarkSyncProgress>(updates.Add);

        await service.SyncAsync("main", progress, TestContext.Current.CancellationToken);

        Assert.Contains(updates, u => u.FileName == "models.json" && u.Stage == BenchmarkSyncStage.Completed);
        Assert.Equal(8, updates.Count(u => u.Stage == BenchmarkSyncStage.Completed));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static BenchmarkSyncService CreateService(
        TempBenchmarkDatabase temp,
        IReadOnlyDictionary<string, string> servedBodies,
        string repoCommit,
        IReadOnlyDictionary<string, string>? publishedFixtures = null,
        string? omitFromTree = null)
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
                var treeEntries = publishedOids
                    .Where(kvp => kvp.Key != omitFromTree)
                    .Select(kvp =>
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

        var httpClient = new HttpClient(handler);
        var probe = new BenchmarkChecksumProbe(httpClient, NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = temp.CreateLedger();
        return new BenchmarkSyncService(
            httpClient, probe, temp.Database, ledger, NullLogger<BenchmarkSyncService>.Instance, TestFileSpecs);
    }
}
