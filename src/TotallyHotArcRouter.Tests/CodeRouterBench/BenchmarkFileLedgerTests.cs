using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkFileLedger"/>.</summary>
public class BenchmarkFileLedgerTests
{
    [Fact]
    public void TryGet_UnknownFile_ReturnsNull()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();

        Assert.Null(ledger.TryGet("id_probing_results_long.csv"));
    }

    [Fact]
    public void Upsert_ThenTryGet_RoundTripsTheRow()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        var entry = new BenchmarkFileLedgerEntry(
            FileName: "id_probing_results_long.csv",
            PublishedOid: "ac74df13c0b582e12c92507c40e54a57ca0db65a",
            6_426_347,
            56_640,
            RepoCommit: "deadbeef",
            SyncedAtUtc: DateTimeOffset.Parse("2026-08-12T00:00:00Z"));

        ledger.Upsert(entry);
        var found = ledger.TryGet("id_probing_results_long.csv");

        Assert.Equal(expected: entry, actual: found);
    }

    [Fact]
    public void Upsert_SameFileTwice_ReplacesThePriorRowRatherThanDuplicating()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        var first = new BenchmarkFileLedgerEntry(
            FileName: "ood176_results_long.csv",
            PublishedOid: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            100,
            1,
            RepoCommit: "commit-a",
            SyncedAtUtc: DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        var second = first with
        {
            PublishedOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            RepoCommit = "commit-b",
            SyncedAtUtc = DateTimeOffset.Parse("2026-08-12T00:00:00Z")
        };

        ledger.Upsert(first);
        ledger.Upsert(second);

        var all = ledger.GetAll();
        Assert.Single(all);
        Assert.Equal(expected: second, actual: all[0]);
    }

    [Fact]
    public void GetAll_MultipleFiles_ReturnsEachOrderedByFileName()
    {
        using var temp = new TempBenchmarkDatabase();
        var ledger = temp.CreateLedger();
        ledger.Upsert(new BenchmarkFileLedgerEntry(
            FileName: "ood176_tasks.jsonl", PublishedOid: "a", 1, 1, RepoCommit: "c",
            SyncedAtUtc: DateTimeOffset.UtcNow));
        ledger.Upsert(new BenchmarkFileLedgerEntry(
            FileName: "id_test_tasks.jsonl", PublishedOid: "b", 1, 1, RepoCommit: "c",
            SyncedAtUtc: DateTimeOffset.UtcNow));

        var all = ledger.GetAll();

        Assert.Equal(2, actual: all.Count);
        Assert.Equal(expected: "id_test_tasks.jsonl", actual: all[0].FileName);
        Assert.Equal(expected: "ood176_tasks.jsonl", actual: all[1].FileName);
    }
}