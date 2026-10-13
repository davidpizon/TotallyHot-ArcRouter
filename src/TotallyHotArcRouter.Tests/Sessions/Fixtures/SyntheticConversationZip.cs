using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Tests.Sessions.Fixtures;

/// <summary>
/// Builds a minimal valid schema-v1 conversation zip in a temp file for tests. No real user content —
/// only the bytes the caller supplies — so CI never depends on a downloaded archive.
/// </summary>
internal static class SyntheticConversationZip
{
    /// <summary>Default OpenAI-shaped request body used when a test does not care about the request.</summary>
    public static readonly byte[] DefaultRequest =
        """{"model":"gpt-test","messages":[{"role":"user","content":"hello"}]}"""u8.ToArray();

    /// <summary>
    /// Writes a one-turn zip and returns its absolute path. The caller deletes the file (and its
    /// directory) when finished.
    /// </summary>
    /// <param name="clientResponse">The client-facing response body stored in the zip.</param>
    /// <param name="clientRequest">The client request body; defaults to <see cref="DefaultRequest"/>.</param>
    /// <param name="provider">Metadata <c>provider</c> value (default <c>openai</c>).</param>
    /// <param name="streaming">Metadata <c>streaming</c> flag.</param>
    /// <param name="directory">Optional parent directory; a unique subdirectory is created under it or under temp.</param>
    /// <returns>The absolute path of the zip file.</returns>
    public static string Write(
        byte[] clientResponse,
        byte[]? clientRequest = null,
        string provider = "openai",
        bool streaming = false,
        string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        clientRequest ??= DefaultRequest;

        var root = directory ?? Path.Combine(Path.GetTempPath(), "thar-convzip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "corpus.zip");

        var archiveSessionId = Guid.CreateVersion7();
        var archiveTurnId = Guid.CreateVersion7();
        const string clientSessionId = "synthetic-client";
        const int turnSequence = 1;
        var createdAt = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        var requestPath =
            $"conversations/{archiveSessionId:D}/turns/{turnSequence:D4}.request.json";
        var responsePath =
            $"conversations/{archiveSessionId:D}/turns/{turnSequence:D4}.response.json";

        var metadata = new JsonObject
        {
            ["provider"] = provider,
            ["streaming"] = streaming,
            ["requested_model"] = "gpt-test",
            ["routed_model"] = "gpt-test",
            ["session_id"] = clientSessionId,
            ["turn_number"] = turnSequence,
            ["created_at_utc"] = createdAt.ToString("O"),
        };

        var turnLine = new JsonObject
        {
            ["archive_turn_id"] = archiveTurnId.ToString("D"),
            ["archive_session_id"] = archiveSessionId.ToString("D"),
            ["client_session_id"] = clientSessionId,
            ["turn_sequence"] = turnSequence,
            ["created_at_utc"] = createdAt.ToString("O"),
            ["origin"] = "captured",
            ["secrets_obscured"] = null,
            ["content_fidelity"] = "full-body",
            ["metadata"] = metadata,
            ["bodies"] = new JsonObject
            {
                ["request"] = BodyNode(requestPath, clientRequest),
                ["response"] = BodyNode(responsePath, clientResponse),
            },
        };

        var turnsBytes = Encoding.UTF8.GetBytes(turnLine.ToJsonString() + "\n");
        var turnsHash = Convert.ToHexStringLower(SHA256.HashData(turnsBytes));

        var manifest = new JsonObject
        {
            ["schema_version"] = ConversationExportWriter.SchemaVersion,
            ["exported_at_utc"] = createdAt.ToString("O"),
            ["router_version"] = "test",
            ["incomplete"] = false,
            ["filter"] = new JsonObject(),
            ["counts"] = new JsonObject
            {
                ["conversations"] = 1,
                ["turns"] = 1,
                ["missing_bodies"] = 0,
                ["corrupt_turns"] = 0,
                ["incomplete_sessions"] = 0,
                ["bytes"] = clientRequest.LongLength + clientResponse.LongLength,
            },
            ["turns_jsonl"] = new JsonObject
            {
                ["path"] = "turns.jsonl",
                ["sha256"] = turnsHash,
                ["length"] = turnsBytes.LongLength,
            },
            ["sessions"] = new JsonArray
            {
                new JsonObject
                {
                    ["archive_session_id"] = archiveSessionId.ToString("D"),
                    ["client_session_id"] = clientSessionId,
                    ["session_sha256"] = null,
                    ["turns_selected"] = 1,
                    ["turns_exported"] = 1,
                    ["corrupt_turns"] = 0,
                    ["incomplete"] = false,
                },
            },
            ["corrupt_turns"] = new JsonArray(),
            ["bodies"] = new JsonArray
            {
                ManifestBody(requestPath, clientRequest),
                ManifestBody(responsePath, clientResponse),
            },
        };

        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        using var file = File.Create(zipPath);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        WriteEntry(zip, "manifest.json", manifestBytes);
        WriteEntry(zip, "turns.jsonl", turnsBytes);
        WriteEntry(zip, requestPath, clientRequest);
        WriteEntry(zip, responsePath, clientResponse);

        return zipPath;
    }

    /// <summary>
    /// Rewrites an existing synthetic zip's <c>manifest.json</c> <c>schema_version</c> (for refusal tests).
    /// </summary>
    /// <param name="sourceZip">Zip to copy from.</param>
    /// <param name="destinationZip">Where to write the tampered copy.</param>
    /// <param name="schemaVersion">The version to plant in the manifest.</param>
    public static void RewriteSchemaVersion(string sourceZip, string destinationZip, int schemaVersion)
    {
        var entries = ReadAll(sourceZip);
        var manifest = JsonNode.Parse(entries["manifest.json"])!;
        manifest["schema_version"] = schemaVersion;
        entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        WriteAll(destinationZip, entries);
    }

    /// <summary>Rewrites <c>turns.jsonl</c> without updating the manifest hash (for hash-mismatch tests).</summary>
    /// <param name="sourceZip">Zip to copy from.</param>
    /// <param name="destinationZip">Where to write the tampered copy.</param>
    public static void CorruptTurnsHash(string sourceZip, string destinationZip)
    {
        var entries = ReadAll(sourceZip);
        entries["turns.jsonl"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries["turns.jsonl"]) + " \n");
        WriteAll(destinationZip, entries);
    }

    /// <summary>Adds an unsafe entry name to a copy of the zip (for zip-slip tests).</summary>
    /// <param name="sourceZip">Zip to copy from.</param>
    /// <param name="destinationZip">Where to write the tampered copy.</param>
    /// <param name="unsafeName">The unsafe entry name to plant.</param>
    public static void AddUnsafeEntry(string sourceZip, string destinationZip, string unsafeName)
    {
        var entries = ReadAll(sourceZip);
        entries[unsafeName] = "evil"u8.ToArray();
        WriteAll(destinationZip, entries);
    }

    private static JsonObject BodyNode(string path, byte[] bytes) => new()
    {
        ["path"] = path,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        ["length"] = bytes.LongLength,
        ["missing"] = false,
    };

    private static JsonObject ManifestBody(string path, byte[] bytes) => new()
    {
        ["path"] = path,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        ["length"] = bytes.LongLength,
    };

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(bytes);
    }

    private static Dictionary<string, byte[]> ReadAll(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result[entry.FullName] = buffer.ToArray();
        }

        return result;
    }

    private static void WriteAll(string path, Dictionary<string, byte[]> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            WriteEntry(zip, name, bytes);
        }
    }
}
