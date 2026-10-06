using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Storage;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers phase 2 of #184 (ADR-0024): deleted text must be gone from the database file and its
/// write-ahead log, not merely unreachable through SQL. Each canary test inserts rows whose text is a
/// unique marker, deletes them through the store, and then searches the raw bytes of the files.
/// </summary>
public class SecureDeletionTests : IDisposable
{
    private readonly string _directory;
    private readonly string _transcriptPath;

    public SecureDeletionTests()
    {
        _directory = Path.Combine(path1: TestScratchDirectory.RunRoot, path2: Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _transcriptPath = Path.Combine(path1: _directory, path2: "transcripts.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(path: _directory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }

    [Fact]
    public void Open_AppliesSecureDeleteAndSynchronousNormalToAFreshNonPooledConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _transcriptPath,
            Pooling = false,
        }.ToString();

        using var connection = SqliteHardening.Open(connectionString);

        Assert.Equal(expected: 1L, actual: Pragma(connection: connection, name: "secure_delete"));
        Assert.Equal(expected: 1L, actual: Pragma(connection: connection, name: "synchronous"));
    }

    [Fact]
    public void TranscriptDatabase_OpenConnection_AppliesSecureDelete()
    {
        using var connection = CreateTranscriptDatabase().OpenConnection();

        Assert.Equal(expected: 1L, actual: Pragma(connection: connection, name: "secure_delete"));
    }

    [Fact]
    public void RouterMemoryDatabase_OpenConnection_AppliesSecureDelete()
    {
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
        {
            EmbeddingMemoryDatabasePath = Path.Combine(path1: _directory, path2: "memory.db"),
        }));
        database.EnsureCreated();

        using var connection = database.OpenConnection();

        Assert.Equal(expected: 1L, actual: Pragma(connection: connection, name: "secure_delete"));
    }

    [Fact]
    public async Task DeleteOldestAsync_LeavesNoCanaryInTheDatabaseOrItsLog()
    {
        var (_, store) = CreateStore();
        await InsertCanaryRowsAsync(store: store, count: 10);

        await store.DeleteOldestAsync(count: 10, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoCanary();
    }

    [Fact]
    public async Task DeleteBeforeAsync_LeavesNoCanaryInTheDatabaseOrItsLog()
    {
        var (_, store) = CreateStore();
        await InsertCanaryRowsAsync(store: store, count: 10);

        await store.DeleteBeforeAsync(cutoff: DateTimeOffset.UtcNow.AddDays(1),
            cancellationToken: TestContext.Current.CancellationToken);

        AssertNoCanary();
    }

    [Fact]
    public async Task DeleteAllAsync_LeavesNoCanaryInTheDatabaseOrItsLog()
    {
        var (_, store) = CreateStore();
        await InsertCanaryRowsAsync(store: store, count: 10);

        await store.DeleteAllAsync(TestContext.Current.CancellationToken);

        AssertNoCanary();
    }

    [Fact]
    public async Task FinalizeDeletionAsync_WithAReaderHoldingTheLog_RetriesThenReportsNotFinal()
    {
        var (database, store) = CreateStore();
        store.FinalizeRetryDelay = TimeSpan.Zero;
        await InsertCanaryRowsAsync(store: store, count: 3);

        // A read transaction pins the log's older frames, so a TRUNCATE checkpoint cannot finish.
        using var reader = database.OpenConnection();
        using (var begin = reader.CreateCommand())
        {
            begin.CommandText = "BEGIN; SELECT COUNT(*) FROM request_transcripts;";
            begin.ExecuteScalar();
        }

        await store.DeleteAllAsync(TestContext.Current.CancellationToken);

        Assert.False(await store.FinalizeDeletionAsync(TestContext.Current.CancellationToken));

        using (var end = reader.CreateCommand())
        {
            end.CommandText = "COMMIT;";
            end.ExecuteNonQuery();
        }

        Assert.True(await store.FinalizeDeletionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteManyAsync_LeavesNoCanaryVectorInTheMemoryDatabaseOrItsLog()
    {
        var memoryPath = Path.Combine(path1: _directory, path2: "memory.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
        {
            EmbeddingMemoryDatabasePath = memoryPath,
        }));
        database.EnsureCreated();
        var store = new SqliteMemoryEntryStore(database);

        var canary = new float[64];
        Array.Fill(array: canary, value: 123456.789f);
        var ids = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            var entry = await store.AppendAsync(
                entry: new MemoryEntry(Id: 0, TaskEmbedding: canary, ChosenModel: "m", Score: 0.5, Cost: 0.01,
                    VerifierTrace: null, CreatedAtUtc: DateTimeOffset.UtcNow),
                cancellationToken: TestContext.Current.CancellationToken);
            ids.Add(entry.Id);
        }

        await store.DeleteManyAsync(ids: ids, cancellationToken: TestContext.Current.CancellationToken);

        var needle = new byte[canary.Length * sizeof(float)];
        Buffer.BlockCopy(src: canary, srcOffset: 0, dst: needle, dstOffset: 0, count: needle.Length);
        Assert.DoesNotContain(expected: true,
            collection: new[] { memoryPath, memoryPath + "-wal" }.Select(path => Contains(ReadShared(path), needle)));
        Assert.Empty(await store.LoadAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Scrub_RemovesTextDeletedBeforeSecureDeleteWasOn_ThenIsNotRepeated()
    {
        CreateDatabaseWithFreedCanaryPagesWithoutSecureDelete();
        Assert.True(ContainsCanary(), "Precondition: the deleted text must still be in the file before the scrub.");
        var marker = _transcriptPath + ".scrubbed";

        var first = RunScrub(marker: marker, space: new SqliteScrub.VolumeSpace(Free: long.MaxValue, Total: 1));

        Assert.True(first == SqliteScrub.Outcome.Completed, string.Join(" | ", Logged));
        Assert.False(ContainsCanary());
        Assert.True(File.Exists(marker));
        Assert.Equal(expected: SqliteScrub.Outcome.NotNeeded,
            actual: RunScrub(marker: marker, space: new SqliteScrub.VolumeSpace(Free: long.MaxValue, Total: 1)));
    }

    [Fact]
    public void Scrub_WithABusyLogAfterTheRebuild_IsStillDoneAndIsNotRepeated()
    {
        CreateDatabaseWithFreedCanaryPagesWithoutSecureDelete();
        var marker = _transcriptPath + ".scrubbed";
        var readerString = new SqliteConnectionStringBuilder { DataSource = _transcriptPath, Pooling = false }.ToString();
        using var reader = new SqliteConnection(readerString);
        reader.Open();
        using (var begin = reader.CreateCommand())
        {
            // A read transaction pins the log, so the TRUNCATE after the rebuild reports busy.
            begin.CommandText = "BEGIN; SELECT COUNT(*) FROM t;";
            begin.ExecuteScalar();
        }

        var outcome = RunScrub(marker: marker, space: new SqliteScrub.VolumeSpace(Free: long.MaxValue, Total: 1));

        Assert.True(outcome == SqliteScrub.Outcome.Completed, string.Join(" | ", Logged));
        Assert.True(File.Exists(marker));
        Assert.Contains("write-ahead log was busy", string.Join(" | ", Logged));
    }

    [Fact]
    public void Scrub_WithTooLittleFreeSpace_DefersAndLeavesTheFileAlone()
    {
        CreateDatabaseWithFreedCanaryPagesWithoutSecureDelete();
        var marker = _transcriptPath + ".scrubbed";

        var outcome = RunScrub(marker: marker, space: new SqliteScrub.VolumeSpace(Free: 1024, Total: 1));

        Assert.Equal(expected: SqliteScrub.Outcome.Deferred, actual: outcome);
        Assert.False(File.Exists(marker));
        Assert.True(ContainsCanary());
    }

    [Fact]
    public void Scrub_WithNoDatabase_RecordsTheMarkerWithoutCreatingTheFile()
    {
        var marker = _transcriptPath + ".scrubbed";

        var outcome = RunScrub(marker: marker, space: new SqliteScrub.VolumeSpace(Free: long.MaxValue, Total: 1));

        Assert.Equal(expected: SqliteScrub.Outcome.NotNeeded, actual: outcome);
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(_transcriptPath));
    }

    [Fact]
    public async Task ScrubHostedService_RunsTheScrubInTheBackgroundAndRecordsTheMarker()
    {
        CreateDatabaseWithFreedCanaryPagesWithoutSecureDelete();
        using var service = new TotallyHot.ArcRouter.Hosting.TranscriptScrubHostedService(
            database: CreateTranscriptDatabase(), logger: NullLogger<TotallyHot.ArcRouter.Hosting.TranscriptScrubHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_transcriptPath + ".scrubbed"));
        Assert.False(ContainsCanary());
    }

    [Fact]
    public async Task MemoryDeleteMany_WithAReaderHoldingTheLog_LogsThatTheDeletionIsNotFinal()
    {
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
        {
            EmbeddingMemoryDatabasePath = Path.Combine(path1: _directory, path2: "memory.db"),
        }));
        database.EnsureCreated();
        var logger = new CapturingLogger();
        var store = new SqliteMemoryEntryStore(database: database, logger: logger);
        var entry = await store.AppendAsync(
            entry: new MemoryEntry(Id: 0, TaskEmbedding: new float[4], ChosenModel: "m", Score: 0.5, Cost: 0.01,
                VerifierTrace: null, CreatedAtUtc: DateTimeOffset.UtcNow),
            cancellationToken: TestContext.Current.CancellationToken);

        using var reader = database.OpenConnection();
        using (var begin = reader.CreateCommand())
        {
            begin.CommandText = "BEGIN; SELECT COUNT(*) FROM memory_entries;";
            begin.ExecuteScalar();
        }

        await store.DeleteManyAsync(ids: [entry.Id], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("not yet final", string.Join(" | ", logger.Lines));
    }

    [Fact]
    public void RunStartupMaintenance_WithNoDatabase_CreatesNothing()
    {
        CreateTranscriptDatabase().RunStartupMaintenance(NullLogger.Instance);

        Assert.False(File.Exists(_transcriptPath));
    }

    private const string Canary = "CANARY-7f3a9c1e-do-not-keep-";

    private SqliteScrub.Outcome RunScrub(string marker, SqliteScrub.VolumeSpace space)
    {
        var logger = new CapturingLogger();
        var outcome = SqliteScrub.Run(
            databasePath: _transcriptPath,
            markerPath: marker,
            tempDirectory: Path.Combine(path1: _directory, path2: "scrub-temp"),
            logger: logger,
            probeVolume: _ => space);
        Logged = logger.Lines;
        return outcome;
    }

    /// <summary>Gets what the last <see cref="RunScrub"/> logged, so a failed assertion can show why a scrub deferred.</summary>
    private IReadOnlyList<string> Logged { get; set; } = [];

    /// <summary>Collects formatted log lines, including any exception, for assertion messages.</summary>
    private sealed class CapturingLogger : ILogger, ILogger<SqliteMemoryEntryStore>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception) + exception);
        }
    }

    /// <summary>
    /// Builds a database the way an older router did: no <c>secure_delete</c>, rows inserted, then deleted
    /// and checkpointed, so the text sits in freed pages of the main file.
    /// </summary>
    private void CreateDatabaseWithFreedCanaryPagesWithoutSecureDelete()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _transcriptPath,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (id INTEGER PRIMARY KEY, body TEXT);";
        command.ExecuteNonQuery();
        for (var i = 0; i < 50; i++)
        {
            command.CommandText = "INSERT INTO t (body) VALUES ($body);";
            command.Parameters.Clear();
            command.Parameters.AddWithValue(parameterName: "$body", value: Canary + i + new string('x', 500));
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM t; PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    private (TranscriptDatabase Database, SqliteTranscriptStore Store) CreateStore()
    {
        var database = CreateTranscriptDatabase();
        database.EnsureCreated();
        return (database, new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true })));
    }

    private TranscriptDatabase CreateTranscriptDatabase()
    {
        return new TranscriptDatabase(Options.Create(new StorageOptions { TranscriptDatabasePath = _transcriptPath }));
    }

    private static async Task InsertCanaryRowsAsync(SqliteTranscriptStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var record = new TranscriptRecord(
                0,
                CorrelationId: "corr-" + i,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                RequestedModel: "m",
                RoutedModel: "m",
                Dimension: "d",
                Difficulty: "medium",
                Language: "python",
                false,
                PromptText: Canary + i + new string('p', 800),
                ResponseText: Canary + i + new string('r', 800),
                null,
                0.001m,
                false,
                1.0,
                1,
                1,
                null);
            await store.InsertAsync(record: record, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private void AssertNoCanary()
    {
        Assert.False(ContainsCanary(), "Deleted text is still readable in the database file or its write-ahead log.");
    }

    private bool ContainsCanary()
    {
        var needle = Encoding.UTF8.GetBytes(Canary);
        return new[] { _transcriptPath, _transcriptPath + "-wal" }.Any(path => Contains(haystack: ReadShared(path), needle: needle));
    }

    private static byte[] ReadShared(string path)
    {
        if (!File.Exists(path)) return [];

        using var stream = new FileStream(path: path, mode: FileMode.Open, access: FileAccess.Read,
            share: FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private static long Pragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return (long)command.ExecuteScalar()!;
    }
}
