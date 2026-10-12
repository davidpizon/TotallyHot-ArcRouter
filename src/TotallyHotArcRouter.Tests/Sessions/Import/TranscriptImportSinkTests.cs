using System.Text.Json;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Sessions.Import;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Sessions.Import;

/// <summary>
/// Pins the learning row an imported turn gets (#165 phase 4): metadata only, unscored so the quality rescan will
/// grade it, bound to its session file, marked imported, and under a correlation id that cannot collide with a
/// captured turn's.
/// </summary>
public sealed class TranscriptImportSinkTests : IDisposable
{
    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot, "sink-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the scratch directory the database lives in.</summary>
    public TranscriptImportSinkTests()
    {
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch cleanup is also done by TestTempDirectorySweeper.
        }
    }

    /// <summary>An imported turn becomes an unscored, imported, metadata-only row bound to its session file.</summary>
    [Fact]
    public async Task TurnStored_WritesAnUnscoredImportedRow()
    {
        var database = new TranscriptDatabase(Options.Create(new StorageOptions
        {
            TranscriptDatabasePath = Path.Combine(_root, "transcripts.db"),
        }));
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(
            database, new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }));
        var sink = new TranscriptImportSink(store);
        var session = SessionArchiveIds.NewArchiveSessionId();
        var turn = SessionArchiveIds.NewArchiveTurnId();
        using var metadata = JsonDocument.Parse(
            """{"requested_model":"auto","routed_model":"gpt-x","dimension":"code_generation","utility":false,"exploratory":true,"propensity":0.25,"prompt_tokens":12,"completion_tokens":34,"estimated_cost_usd":0.5}""");

        await sink.TurnStoredAsync(
            new ImportedTurn(session, turn, "client-a", 3, DateTimeOffset.UtcNow, metadata.RootElement.Clone(), "hello there", "hi"),
            TestContext.Current.CancellationToken);

        var record = await store.GetTranscriptAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(SessionTurnOrigin.Imported, record!.Origin);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT origin, score, correlation_id, archive_session_id, archive_turn_id, prompt_text, response_text,
                                     prompt_text_length, response_text_length, routed_model, is_exploratory, input_tokens
                              FROM request_transcripts;
                              """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(SessionTurnOrigin.Imported, reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal($"imp-{session:N}:3", reader.GetString(2));
        Assert.Equal(session.ToString("D"), reader.GetString(3));
        Assert.Equal(turn.ToString("D"), reader.GetString(4));
        Assert.True(reader.IsDBNull(5));
        Assert.True(reader.IsDBNull(6));
        Assert.Equal("hello there".Length, reader.GetInt32(7));
        Assert.Equal("hi".Length, reader.GetInt32(8));
        Assert.Equal("gpt-x", reader.GetString(9));
        Assert.Equal(1, reader.GetInt32(10));
        Assert.Equal(12, reader.GetInt32(11));
        Assert.False(reader.Read());
    }
}
