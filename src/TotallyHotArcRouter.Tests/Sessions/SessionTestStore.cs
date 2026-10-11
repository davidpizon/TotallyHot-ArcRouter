using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// A real <see cref="SessionStore"/> over a scratch database and folder with an in-memory master key, for the
/// tests of body hashes and export. Several can exist at once; each owns its own directory.
/// </summary>
internal sealed class SessionTestStore : IDisposable
{
    private readonly TranscriptDatabase _database;

    /// <summary>Builds a store in a fresh scratch directory.</summary>
    public SessionTestStore()
    {
        Root = Path.Combine(TestScratchDirectory.RunRoot, "session-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Folder = Path.Combine(Root, "sessions");
        _database = new TranscriptDatabase(Options.Create(
            new StorageOptions { TranscriptDatabasePath = Path.Combine(Root, "transcripts.db") }));
        Index = new SessionIndex(_database);
        Index.EnsureCreated();
        Store = new SessionStore(Index, new InMemoryMasterKeyStore(), Folder);
    }

    /// <summary>Gets the scratch directory that holds the database and the session folder.</summary>
    public string Root { get; }

    /// <summary>Gets the session folder.</summary>
    public string Folder { get; }

    /// <summary>Gets the index over the scratch database.</summary>
    public SessionIndex Index { get; }

    /// <summary>Gets the store under test.</summary>
    public SessionStore Store { get; }

    /// <summary>Runs a statement that changes the index directly, to stage a state the store would not produce.</summary>
    /// <param name="sql">The statement.</param>
    public void Execute(string sql)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Runs a query that returns one integer.</summary>
    /// <param name="sql">The query.</param>
    /// <returns>The first column of the first row.</returns>
    public long Scalar(string sql)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the path of a session's file.</summary>
    /// <param name="archiveSessionId">The session.</param>
    /// <returns>The absolute path.</returns>
    public string FileOf(Guid archiveSessionId) =>
        Path.Combine(Folder, Index.TryGetSession(archiveSessionId)!.FileName);

    /// <inheritdoc/>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch cleanup is also done by TestTempDirectorySweeper.
        }
    }
}
