using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.CodeRouterBench;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Covers <see cref="BenchmarkDataStatusService"/>'s three states
/// (docs/router/coderouterbench-sqlite-migration-plan.md, Phase 3).
/// </summary>
public class BenchmarkDataStatusServiceTests
{
    [Fact]
    public void Current_BeforeAnyRecheck_IsNull()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(temp, FakeHttpMessageHandler.AlwaysFails());

        Assert.Null(service.Current);
    }

    [Fact]
    public async Task RecheckAsync_EveryLedgerRowMatchesPublished_ReportsCurrent()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();

        // Every one of the eight BenchmarkFileSpec.All entries must have a matching ledger row for the
        // corpus to read as Current - a single matching file is not enough.
        var files = BenchmarkFileSpec.All
            .Select(spec => (spec.FileName, Oid: GitBlobHash.Compute(Encoding.UTF8.GetBytes(spec.FileName))))
            .ToArray();
        foreach (var (fileName, oid) in files)
        {
            ledger.Upsert(new BenchmarkFileLedgerEntry(fileName, oid, 7, 0, "commit1", DateTimeOffset.UtcNow));
        }

        var service = CreateService(temp, TreeHandler(files));

        var status = await service.RecheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BenchmarkDataState.Current, status.State);
        Assert.Null(status.Reason);
        Assert.Same(status, service.Current);
    }

    [Fact]
    public async Task RecheckAsync_LedgerRowMissing_ReportsUpdate()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.CreateLedger(); // schema only, no rows synced yet

        var service = CreateService(temp, TreeHandler(("models.json", "somepublishedoid00000000000000000000000")));

        var status = await service.RecheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BenchmarkDataState.Update, status.State);
    }

    [Fact]
    public async Task RecheckAsync_LedgerRowStale_ReportsUpdate()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        ledger.Upsert(new BenchmarkFileLedgerEntry(
            "models.json", "stale0000000000000000000000000000000000", 7, 0, "commit0", DateTimeOffset.UtcNow));

        var service = CreateService(temp, TreeHandler(("models.json", "fresh0000000000000000000000000000000000")));

        var status = await service.RecheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BenchmarkDataState.Update, status.State);
    }

    [Fact]
    public async Task RecheckAsync_ProbeUnreachable_ReportsCheckFailedRatherThanThrowing()
    {
        using var temp = new TempBenchmarkDatabase();
        var service = CreateService(temp, FakeHttpMessageHandler.AlwaysFails());

        var status = await service.RecheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BenchmarkDataState.CheckFailed, status.State);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public async Task RecheckAsync_AfterAPriorSuccessAndThenAFailure_OverwritesCachedStatus()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        var files = BenchmarkFileSpec.All
            .Select(spec => (spec.FileName, Oid: GitBlobHash.Compute(Encoding.UTF8.GetBytes(spec.FileName))))
            .ToArray();
        foreach (var (fileName, oid) in files)
        {
            ledger.Upsert(new BenchmarkFileLedgerEntry(fileName, oid, 7, 0, "commit1", DateTimeOffset.UtcNow));
        }

        // A probe that succeeds once, then fails - simulating Hugging Face going unreachable between checks.
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            return callCount == 1
                ? TreeResponse(files)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var service = CreateService(temp, handler);

        var first = await service.RecheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkDataState.Current, first.State);

        var second = await service.RecheckAsync(TestContext.Current.CancellationToken);

        // A later probe failure must never leave the stale "Current" reading in place - a genuinely
        // drifted machine would then look fine.
        Assert.Equal(BenchmarkDataState.CheckFailed, second.State);
        Assert.Equal(BenchmarkDataState.CheckFailed, service.Current!.State);
    }

    private static BenchmarkDataStatusService CreateService(TempBenchmarkDatabase temp, HttpMessageHandler handler)
    {
        var probe = new BenchmarkChecksumProbe(new FakeHttpClientFactory(handler), NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = new BenchmarkFileLedger(temp.Database);
        return new BenchmarkDataStatusService(
            probe, ledger, Options.Create(new BenchmarkSyncOptions()), NullLogger<BenchmarkDataStatusService>.Instance);
    }

    private static FakeHttpMessageHandler TreeHandler(params (string FileName, string Oid)[] files) =>
        new(_ => TreeResponse(files));

    private static HttpResponseMessage TreeResponse(params (string FileName, string Oid)[] files)
    {
        var entries = files.Select(f => $$"""{ "type": "file", "path": "{{f.FileName}}", "oid": "{{f.Oid}}", "size": 7 }""");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"[{string.Join(',', entries)}]", Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-Repo-Commit", "commit1");
        return response;
    }
}
