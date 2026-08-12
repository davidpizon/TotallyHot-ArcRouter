using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Reads and writes <c>benchmark_files</c>, the per-file checksum ledger Phase 3's startup probe compares
/// against Hugging Face's published <c>oid</c>s to compute the corpus's <c>Current</c>/<c>Update</c>/
/// <c>CheckFailed</c> state (docs/router/coderouterbench-sqlite-migration-plan.md).
/// </summary>
public sealed class BenchmarkFileLedger
{
    private readonly BenchmarkDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="BenchmarkFileLedger"/> class.</summary>
    public BenchmarkFileLedger(BenchmarkDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Gets every recorded file sync, ordered by file name.</summary>
    public IReadOnlyList<BenchmarkFileLedgerEntry> GetAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_name, published_oid, size_bytes, row_count, repo_commit, synced_at_utc
            FROM benchmark_files
            ORDER BY file_name;
            """;

        List<BenchmarkFileLedgerEntry> entries = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    /// <summary>Gets the recorded sync for <paramref name="fileName"/>, or <see langword="null"/> if it has never synced.</summary>
    public BenchmarkFileLedgerEntry? TryGet(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_name, published_oid, size_bytes, row_count, repo_commit, synced_at_utc
            FROM benchmark_files
            WHERE file_name = $fileName;
            """;
        command.Parameters.AddWithValue("$fileName", fileName);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    /// <summary>
    /// Inserts or replaces the ledger row for <paramref name="entry"/>'s file. The caller is responsible
    /// for calling this only after the corresponding table import has committed, so the ledger never
    /// claims a file is synced when its data is not.
    /// </summary>
    public void Upsert(BenchmarkFileLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_files (file_name, published_oid, size_bytes, row_count, repo_commit, synced_at_utc)
            VALUES ($fileName, $publishedOid, $sizeBytes, $rowCount, $repoCommit, $syncedAtUtc)
            ON CONFLICT(file_name) DO UPDATE SET
                published_oid = excluded.published_oid,
                size_bytes    = excluded.size_bytes,
                row_count     = excluded.row_count,
                repo_commit   = excluded.repo_commit,
                synced_at_utc = excluded.synced_at_utc;
            """;
        command.Parameters.AddWithValue("$fileName", entry.FileName);
        command.Parameters.AddWithValue("$publishedOid", entry.PublishedOid);
        command.Parameters.AddWithValue("$sizeBytes", entry.SizeBytes);
        command.Parameters.AddWithValue("$rowCount", entry.RowCount);
        command.Parameters.AddWithValue("$repoCommit", entry.RepoCommit);
        command.Parameters.AddWithValue("$syncedAtUtc", entry.SyncedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static BenchmarkFileLedgerEntry ReadEntry(SqliteDataReader reader) => new(
        FileName: reader.GetString(0),
        PublishedOid: reader.GetString(1),
        SizeBytes: reader.GetInt64(2),
        RowCount: reader.GetInt32(3),
        RepoCommit: reader.GetString(4),
        SyncedAtUtc: DateTimeOffset.Parse(
            reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
