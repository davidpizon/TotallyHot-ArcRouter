using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.CodeRouterBench;

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
        ["summary.json"] = """{ "total_tasks": 4 }"""
    };

    private static readonly IReadOnlyList<BenchmarkFileSpec> TestFileSpecs =
    [
        new(FileName: "id_probing_results_long.csv", Kind: BenchmarkFileKind.IdResultsCsv, Split: "probing", 2),
        new(FileName: "id_test_results_long.csv", Kind: BenchmarkFileKind.IdResultsCsv, Split: "id_test", 1),
        new(FileName: "ood176_results_long.csv", Kind: BenchmarkFileKind.OodResultsCsv, null, 1),
        new(FileName: "id_probing_tasks.jsonl", Kind: BenchmarkFileKind.IdTasksJsonl, Split: "probing", 1),
        new(FileName: "id_test_tasks.jsonl", Kind: BenchmarkFileKind.IdTasksJsonl, Split: "id_test", 1),
        new(FileName: "ood176_tasks.jsonl", Kind: BenchmarkFileKind.OodTasksJsonl, null, 1),
        new(FileName: "models.json", Kind: BenchmarkFileKind.ModelsJson, null, null),
        new(FileName: "summary.json", Kind: BenchmarkFileKind.SummaryJson, null, null)
    ];

    [Fact]
    public async Task SyncAsync_AllFilesValid_ImportsEveryFileAndRecordsTheLedger()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123");

        var result = await service.SyncAsync(datasetRef: "main", null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "commit123", actual: result.RepoCommit);
        Assert.Equal(8, actual: result.Files.Count);
        Assert.All(collection: result.Files,
            action: outcome => Assert.True(condition: outcome.Succeeded,
                userMessage: $"{outcome.FileName}: {outcome.ErrorMessage}"));

        var ledger = temp.CreateLedger();
        Assert.Equal(8, actual: ledger.GetAll().Count);
        var modelsEntry = ledger.TryGet("models.json");
        Assert.NotNull(modelsEntry);
        Assert.Equal(expected: "commit123", actual: modelsEntry!.RepoCommit);
    }

    [Fact]
    public async Task SyncAsync_ChecksumMismatch_LeavesTheLedgerRowAbsent()
    {
        using var temp = new TempBenchmarkDatabase();
        // Probe's published oid is computed from the *original* fixture; the served body is swapped -
        // exactly what a file tampered with (or truncated) after publication would look like.
        var servedBodies = new Dictionary<string, string>(Fixtures)
        {
            ["models.json"] = """{ "tampered-after-checksum-was-published": {} }"""
        };
        var service = CreateService(temp: temp, servedBodies: servedBodies, repoCommit: "commit123",
            publishedFixtures: Fixtures);

        var result = await service.SyncAsync(datasetRef: "main", null,
            cancellationToken: TestContext.Current.CancellationToken);

        var modelsOutcome = result.Files.Single(f => f.FileName == "models.json");
        Assert.False(modelsOutcome.Succeeded);
        Assert.Contains(expectedSubstring: "checksum", actualString: modelsOutcome.ErrorMessage,
            comparisonType: StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, actual: result.Files.Count(f => f.Succeeded));

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
        var service = CreateService(temp: temp, servedBodies: servedBodies, repoCommit: "commit123");

        var result = await service.SyncAsync(datasetRef: "main", null,
            cancellationToken: TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "id_probing_results_long.csv");
        Assert.False(outcome.Succeeded);
        Assert.Contains(expectedSubstring: "data row", actualString: outcome.ErrorMessage,
            comparisonType: StringComparison.OrdinalIgnoreCase);

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_id_results WHERE split = 'probing';";
        Assert.Equal(0, actual: Convert.ToInt32(command.ExecuteScalar()));

        var ledger = temp.CreateLedger();
        Assert.Null(ledger.TryGet("id_probing_results_long.csv"));
    }

    [Fact]
    public async Task SyncAsync_FileMissingFromPublishedTree_ReportsFailureWithoutThrowing()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123",
            omitFromTree: "summary.json");

        var result = await service.SyncAsync(datasetRef: "main", null,
            cancellationToken: TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "summary.json");
        Assert.False(outcome.Succeeded);
        Assert.Equal(7, actual: result.Files.Count(f => f.Succeeded));
    }

    [Fact]
    public async Task SyncAsync_CallerCancels_PropagatesInsteadOfRecordingAPerFileFailure()
    {
        using var temp = new TempBenchmarkDatabase();
        using var cts = new CancellationTokenSource();
        var service = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123",
            cancelDownloadsWith: cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SyncAsync(datasetRef: "main", null, cancellationToken: cts.Token));

        var ledger = temp.CreateLedger();
        Assert.Empty(ledger.GetAll());
    }

    [Fact]
    public async Task SyncAsync_ReportsCompletedProgressForEveryFile()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123");
        List<BenchmarkSyncProgress> updates = [];
        // A synchronous IProgress<T>, not System.Progress<T>: the latter marshals through
        // SynchronizationContext.Post, whose delivery timing relative to the awaited SyncAsync call
        // depends on whichever context the test runner happens to install - not guaranteed to have
        // drained by the time this method returns.
        var progress = new SynchronousProgress<BenchmarkSyncProgress>(updates.Add);

        await service.SyncAsync(datasetRef: "main", progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(collection: updates,
            filter: u => u.FileName == "models.json" && u.Stage == BenchmarkSyncStage.Completed);
        Assert.Equal(8, actual: updates.Count(u => u.Stage == BenchmarkSyncStage.Completed));
        // Every progress update for a file being synced carries its published size, constant across the
        // whole file's lifecycle, so a progress bar's denominator never shifts mid-download.
        Assert.All(collection: updates, action: u => Assert.NotNull(u.TotalBytes));
        foreach (var group in updates.GroupBy(u => u.FileName))
            Assert.Single(group.Select(u => u.TotalBytes).Distinct());
    }

    [Fact]
    public async Task SyncAsync_FileAlreadyCurrent_IsSkippedWithoutADownloadRequest()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        var publishedOid = GitBlobHash.Compute(Encoding.UTF8.GetBytes(Fixtures["models.json"]));
        ledger.Upsert(new BenchmarkFileLedgerEntry(FileName: "models.json", PublishedOid: publishedOid, 123, 1,
            RepoCommit: "old-commit", SyncedAtUtc: DateTimeOffset.UtcNow));

        HashSet<string> requestedFiles = [];
        var service = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123",
            onFileRequested: name => requestedFiles.Add(name));

        var result = await service.SyncAsync(datasetRef: "main", null,
            cancellationToken: TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "models.json");
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Skipped);
        Assert.Equal(1, actual: outcome.RowCount);
        Assert.DoesNotContain(expected: "models.json", set: requestedFiles);
        Assert.Equal(7, actual: result.Files.Count(f => !f.Skipped && f.Succeeded));
    }

    [Fact]
    public async Task SyncAsync_ChecksumMismatch_LeavesNoFileInTheTempDirectory()
    {
        using var temp = new TempBenchmarkDatabase();
        var tempRoot = Path.GetTempPath();
        var before = Directory.GetDirectories(path: tempRoot, searchPattern: "arcrouter-bench-*");
        var servedBodies = new Dictionary<string, string>(Fixtures)
        {
            ["models.json"] = """{ "tampered-after-checksum-was-published": {} }"""
        };
        var service = CreateService(temp: temp, servedBodies: servedBodies, repoCommit: "commit123",
            publishedFixtures: Fixtures);

        await service.SyncAsync(datasetRef: "main", null, cancellationToken: TestContext.Current.CancellationToken);

        var after = Directory.GetDirectories(path: tempRoot, searchPattern: "arcrouter-bench-*");
        Assert.Equal(expected: before.Length, actual: after.Length);
    }

    [Fact]
    public async Task SyncAsync_LfsTrackedFile_VerifiesAgainstItsRealContentSha256AndSucceeds()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(
            temp: temp, servedBodies: Fixtures, repoCommit: "commit123",
            lfsFileNames: new HashSet<string> { "models.json" });

        var result = await service.SyncAsync(datasetRef: "main", null,
            cancellationToken: TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "models.json");
        Assert.True(condition: outcome.Succeeded, userMessage: outcome.ErrorMessage);

        var ledger = temp.CreateLedger();
        var entry = ledger.TryGet("models.json");
        Assert.NotNull(entry);
        Assert.Equal(expected: ContentSha256Hash.Compute(Encoding.UTF8.GetBytes(Fixtures["models.json"])),
            actual: entry!.PublishedOid);
    }

    [Fact]
    public async Task SyncAsync_Completes_DeletesItsTempDirectory()
    {
        using var temp = new TempBenchmarkDatabase();
        var tempRoot = Path.GetTempPath();
        var before = Directory.GetDirectories(path: tempRoot, searchPattern: "arcrouter-bench-*");
        var service = CreateService(temp: temp, servedBodies: Fixtures, repoCommit: "commit123");

        await service.SyncAsync(datasetRef: "main", null, cancellationToken: TestContext.Current.CancellationToken);

        var after = Directory.GetDirectories(path: tempRoot, searchPattern: "arcrouter-bench-*");
        Assert.Equal(expected: before.Length, actual: after.Length);
    }

    private static BenchmarkSyncService CreateService(
        TempBenchmarkDatabase temp,
        IReadOnlyDictionary<string, string> servedBodies,
        string repoCommit,
        IReadOnlyDictionary<string, string>? publishedFixtures = null,
        string? omitFromTree = null,
        CancellationTokenSource? cancelDownloadsWith = null,
        Action<string>? onFileRequested = null,
        IReadOnlySet<string>? lfsFileNames = null)
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
                var treeEntries = publishedOids
                    .Where(kvp => kvp.Key != omitFromTree)
                    .Select(kvp =>
                    {
                        var size = Encoding.UTF8.GetByteCount(oidSourceBodies[kvp.Key]);
                        if (lfsFileNames?.Contains(kvp.Key) == true)
                        {
                            // A deliberately-wrong top-level oid (not the real git blob hash of the served
                            // bytes) proves the sync verifies against lfs.oid, not this one.
                            var lfsOid = ContentSha256Hash.Compute(Encoding.UTF8.GetBytes(oidSourceBodies[kvp.Key]));
                            return
                                $$"""{ "type": "file", "path": "{{kvp.Key}}", "oid": "0000000000000000000000000000000000wrong", "size": 1, "lfs": { "oid": "{{lfsOid}}", "size": {{size}} } }""";
                        }

                        return
                            $$"""{ "type": "file", "path": "{{kvp.Key}}", "oid": "{{kvp.Value}}", "size": {{size}} }""";
                    });
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content: $"[{string.Join(',', values: treeEntries)}]",
                        encoding: Encoding.UTF8, mediaType: "application/json")
                };
                response.Headers.Add(name: "X-Repo-Commit", value: repoCommit);
                return response;
            }

            var fileName = path[(path.LastIndexOf('/') + 1)..];
            onFileRequested?.Invoke(fileName);
            cancelDownloadsWith?.Cancel();
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

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}