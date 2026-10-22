using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers the one-time issue #319 purge: untrusted <c>is_judge_scored = 1</c> memory rows are deleted,
/// and transcript links that pointed at them are cleared. The seed is raw SQL on a file that already has
/// the current columns, so <see cref="RouterMemoryDatabase.EnsureCreated"/> does not rewrite a <c>1</c>
/// into the <c>0</c> default before the purge runs.
/// </summary>
public class Issue319UntrustedJudgeProvenancePurgeTests
{
    [Fact]
    public void EnsureCreated_OnCopiedDatabase_PurgesUntrustedJudgeRowsAndClearsTranscriptLinksOnce()
    {
        var memorySource = CreateMemorySeed();
        var judgeScoredId = Scalar(memorySource, "SELECT id FROM memory_entries WHERE is_judge_scored = 1;");
        var keptId = Scalar(memorySource, "SELECT id FROM memory_entries WHERE is_judge_scored = 0;");
        var transcriptSource = CreateTranscriptSeed(judgeScoredId: judgeScoredId, keptId: keptId);

        using (var source = Open(memorySource))
        {
            Assert.Equal(expected: 0L, actual: Scalar(source,
                "SELECT COUNT(*) FROM schema_meta WHERE key = 'issue-319-untrusted-judge-provenance-purge';"));
        }

        var memoryCopy = CopyToTemp(sourcePath: memorySource, fileName: "router_embedding_memory.db");
        var transcriptCopy = CopyToTemp(sourcePath: transcriptSource, fileName: "transcripts.db");
        var database = new RouterMemoryDatabase(
            Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = memoryCopy }),
            Options.Create(new StorageOptions { TranscriptDatabasePath = transcriptCopy }));

        database.EnsureCreated();

        using (var upgraded = database.OpenConnection())
        {
            Assert.Equal(expected: 0L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM memory_entries WHERE is_judge_scored = 1;"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded,
                "SELECT COUNT(*) FROM memory_entries WHERE chosen_model = 'kept' AND is_judge_scored = 0;"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM grade_receipts;"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM grader_scores;"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM judge_shadow_scores;"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded,
                "SELECT COUNT(*) FROM dimension_scores WHERE dimension LIKE 'live:%';"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded,
                "SELECT COUNT(*) FROM dimension_scores WHERE dimension = 'code_generation';"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded,
                "SELECT COUNT(*) FROM schema_meta WHERE key = 'issue-293-live-learning-purge' AND value = '2026-10-14T00:00:00.0000000+00:00';"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded,
                "SELECT COUNT(*) FROM schema_meta WHERE key = 'issue-319-untrusted-judge-provenance-purge';"));
        }

        using (var untouchedMemory = Open(memorySource))
        {
            Assert.Equal(expected: 1L, actual: Scalar(untouchedMemory, "SELECT COUNT(*) FROM memory_entries WHERE is_judge_scored = 1;"));
            Assert.Equal(expected: 1L, actual: Scalar(untouchedMemory, "SELECT COUNT(*) FROM memory_entries WHERE is_judge_scored = 0;"));
            Assert.Equal(expected: 0L, actual: Scalar(untouchedMemory,
                "SELECT COUNT(*) FROM schema_meta WHERE key = 'issue-319-untrusted-judge-provenance-purge';"));
        }

        using (var upgradedTranscript = Open(transcriptCopy))
        {
            Assert.Equal(expected: 0L, actual: Scalar(upgradedTranscript,
                "SELECT COUNT(*) FROM request_transcripts WHERE correlation_id = 'linked-to-one' AND memory_entry_id IS NOT NULL;"));
            Assert.Equal(expected: 1L, actual: Scalar(upgradedTranscript,
                "SELECT COUNT(*) FROM request_transcripts WHERE correlation_id = 'linked-to-one' AND memory_entry_id IS NULL AND is_judge_scored = 1 AND score = 0.5 AND prompt_text = 'prompt-one' AND response_text = 'response-one' AND requested_model = 'asked';"));
            Assert.Equal(expected: keptId, actual: Scalar(upgradedTranscript,
                "SELECT memory_entry_id FROM request_transcripts WHERE correlation_id = 'linked-to-zero';"));
            Assert.Equal(expected: 1L, actual: Scalar(upgradedTranscript,
                "SELECT COUNT(*) FROM request_transcripts WHERE correlation_id = 'already-null' AND memory_entry_id IS NULL;"));
            Assert.Equal(expected: 999_999L, actual: Scalar(upgradedTranscript,
                "SELECT memory_entry_id FROM request_transcripts WHERE correlation_id = 'already-dangling';"));
        }

        using (var untouchedTranscript = Open(transcriptSource))
        {
            Assert.Equal(expected: judgeScoredId, actual: Scalar(untouchedTranscript,
                "SELECT memory_entry_id FROM request_transcripts WHERE correlation_id = 'linked-to-one';"));
        }

        long insertedId;
        using (var upgraded = database.OpenConnection())
        {
            using var insert = upgraded.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO memory_entries (
                                     embedding, chosen_model, score, cost, created_at_utc, is_judge_scored)
                                 VALUES (X'02', 'after', 0.9, 0, '2026-10-22T00:00:00Z', 1);
                                 """;
            insert.ExecuteNonQuery();
            insertedId = Scalar(upgraded, "SELECT id FROM memory_entries WHERE chosen_model = 'after';");
        }

        using (var upgradedTranscript = Open(transcriptCopy))
        {
            using var insert = upgradedTranscript.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO request_transcripts (
                                     correlation_id, score, is_judge_scored, prompt_text, response_text,
                                     requested_model, memory_entry_id)
                                 VALUES ('after', 0.9, 1, 'later', 'later-response', 'asked', $id);
                                 """;
            insert.Parameters.AddWithValue("$id", insertedId);
            insert.ExecuteNonQuery();
        }

        string marker;
        using (var upgraded = database.OpenConnection())
            marker = Text(upgraded, "SELECT value FROM schema_meta WHERE key = 'issue-319-untrusted-judge-provenance-purge';");

        database.EnsureCreated();

        using (var upgraded = database.OpenConnection())
        {
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM memory_entries WHERE chosen_model = 'after' AND is_judge_scored = 1;"));
            Assert.Equal(expected: marker, actual: Text(upgraded,
                "SELECT value FROM schema_meta WHERE key = 'issue-319-untrusted-judge-provenance-purge';"));
        }

        using (var upgradedTranscript = Open(transcriptCopy))
        {
            Assert.Equal(expected: insertedId, actual: Scalar(upgradedTranscript,
                "SELECT memory_entry_id FROM request_transcripts WHERE correlation_id = 'after';"));
        }
    }

    [Fact]
    public void EnsureCreated_WhenTranscriptFileIsMissing_PurgesMemoryAndDoesNotCreateTheFile()
    {
        var memorySource = CreateMemorySeed();
        var memoryCopy = CopyToTemp(sourcePath: memorySource, fileName: "router_embedding_memory.db");
        var missingTranscript = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "transcripts.db");
        var database = new RouterMemoryDatabase(
            Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = memoryCopy }),
            Options.Create(new StorageOptions { TranscriptDatabasePath = missingTranscript }));

        database.EnsureCreated();

        using var upgraded = database.OpenConnection();
        Assert.Equal(expected: 0L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM memory_entries WHERE is_judge_scored = 1;"));
        Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM memory_entries WHERE is_judge_scored = 0;"));
        Assert.Equal(expected: 1L, actual: Scalar(upgraded,
            "SELECT COUNT(*) FROM schema_meta WHERE key = 'issue-319-untrusted-judge-provenance-purge';"));
        Assert.False(File.Exists(missingTranscript));
    }

    private static string CreateMemorySeed()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "seed.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE memory_entries (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  embedding BLOB NOT NULL,
                                  chosen_model TEXT NOT NULL,
                                  score REAL NOT NULL,
                                  cost REAL NOT NULL,
                                  verifier_trace TEXT NULL,
                                  created_at_utc TEXT NOT NULL,
                                  is_exploratory INTEGER NOT NULL DEFAULT 0,
                                  propensity REAL NOT NULL DEFAULT 1.0,
                                  dimension TEXT NULL,
                                  is_judge_scored INTEGER NOT NULL DEFAULT 0,
                                  embedding_model TEXT NULL);

                              CREATE TABLE dimension_scores (
                                  dimension TEXT NOT NULL,
                                  model TEXT NOT NULL,
                                  sum REAL NOT NULL,
                                  count INTEGER NOT NULL,
                                  PRIMARY KEY (dimension, model));

                              CREATE TABLE grader_scores (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  correlation_id TEXT NOT NULL,
                                  created_at_utc TEXT NOT NULL,
                                  dimension TEXT NOT NULL,
                                  model TEXT NOT NULL,
                                  grader_key TEXT NOT NULL,
                                  score REAL NOT NULL,
                                  grader_backbone_model TEXT NULL,
                                  response_length_chars INTEGER NULL,
                                  question_hmac TEXT NULL,
                                  prompt_version TEXT NULL);

                              CREATE TABLE judge_shadow_scores (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  correlation_id TEXT NOT NULL,
                                  created_at_utc TEXT NOT NULL,
                                  dimension TEXT NOT NULL,
                                  model TEXT NOT NULL,
                                  static_score REAL NOT NULL,
                                  judge_score REAL NOT NULL,
                                  judge_model TEXT NOT NULL,
                                  judge_prompt_version TEXT NOT NULL,
                                  judge_latency_ms INTEGER NOT NULL,
                                  used_logprobs INTEGER NOT NULL,
                                  syntax_authoritative INTEGER NULL);

                              CREATE TABLE grade_receipts (
                                  correlation_id TEXT PRIMARY KEY,
                                  created_at_utc TEXT NOT NULL,
                                  dimension TEXT NOT NULL,
                                  model TEXT NOT NULL,
                                  grade_status TEXT NOT NULL,
                                  unified_score REAL NOT NULL,
                                  question_hmac TEXT NULL,
                                  question_chars INTEGER NULL,
                                  context_messages INTEGER NULL,
                                  question_truncated INTEGER NOT NULL DEFAULT 0,
                                  fingerprint_ref TEXT NULL,
                                  scorer_version TEXT NOT NULL,
                                  prompt_version TEXT NULL,
                                  weights_version TEXT NOT NULL,
                                  abstained_graders TEXT NOT NULL DEFAULT '');

                              CREATE TABLE schema_meta (
                                  key TEXT PRIMARY KEY,
                                  value TEXT NOT NULL);

                              INSERT INTO memory_entries (
                                  embedding, chosen_model, score, cost, created_at_utc, is_judge_scored)
                              VALUES (X'00', 'untrusted', 0.5, 0, '2020-01-01T00:00:00Z', 1),
                                     (X'01', 'kept', 0.4, 0, '2020-01-01T00:00:00Z', 0);

                              INSERT INTO dimension_scores (dimension, model, sum, count)
                              VALUES ('live:code_generation', 'm', 1, 1),
                                     ('code_generation', 'm', 2, 2);

                              INSERT INTO grader_scores (
                                  correlation_id, created_at_utc, dimension, model, grader_key, score)
                              VALUES ('c-1', '2020-01-01T00:00:00Z', 'd', 'm', 'analysis', 0.5);

                              INSERT INTO judge_shadow_scores (
                                  correlation_id, created_at_utc, dimension, model, static_score, judge_score,
                                  judge_model, judge_prompt_version, judge_latency_ms, used_logprobs)
                              VALUES ('c-1', '2020-01-01T00:00:00Z', 'd', 'm', 0.2, 0.4, 'judge', 'v1', 3, 0);

                              INSERT INTO grade_receipts (
                                  correlation_id, created_at_utc, dimension, model, grade_status, unified_score,
                                  scorer_version, weights_version)
                              VALUES ('c-1', '2020-01-01T00:00:00Z', 'code_generation', 'm', 'Complete', 0.5, '3.0', '3.0');

                              INSERT INTO schema_meta (key, value)
                              VALUES ('issue-293-live-learning-purge', '2026-10-14T00:00:00.0000000+00:00');
                              """;
        command.ExecuteNonQuery();
        return path;
    }

    private static string CreateTranscriptSeed(long judgeScoredId, long keptId)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "transcripts.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE request_transcripts (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  correlation_id TEXT NOT NULL,
                                  score REAL NULL,
                                  is_judge_scored INTEGER NOT NULL,
                                  prompt_text TEXT NULL,
                                  response_text TEXT NULL,
                                  requested_model TEXT NOT NULL,
                                  memory_entry_id INTEGER NULL);

                              INSERT INTO request_transcripts (
                                  correlation_id, score, is_judge_scored, prompt_text, response_text,
                                  requested_model, memory_entry_id)
                              VALUES ('linked-to-one', 0.5, 1, 'prompt-one', 'response-one', 'asked', $judge),
                                     ('linked-to-zero', 0.2, 0, 'prompt-zero', 'response-zero', 'asked', $kept),
                                     ('already-null', 0.1, 0, 'prompt-null', 'response-null', 'asked', NULL),
                                     ('already-dangling', 0.3, 0, 'prompt-dangling', 'response-dangling', 'asked', 999999);
                              """;
        command.Parameters.AddWithValue("$judge", judgeScoredId);
        command.Parameters.AddWithValue("$kept", keptId);
        command.ExecuteNonQuery();
        return path;
    }

    private static string CopyToTemp(string sourcePath, string fileName)
    {
        var copy = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(sourceFileName: sourcePath, destFileName: copy);
        return copy;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        return Scalar(connection, sql);
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture);
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
