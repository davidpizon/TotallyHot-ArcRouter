using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Sessions.Export;
using TotallyHot.ArcRouter.Sessions.Import;

namespace TotallyHot.ArcRouter.Tests.Sessions.Fixtures;

/// <summary>
/// Offline reader for a schema-v1 conversation export zip. Opens <c>turns.jsonl</c> and yields each
/// turn's metadata plus request/response bytes so tests can replay real (or synthetic) exchanges
/// through existing parsers without importing into <c>SessionStore</c>.
/// </summary>
internal static class ConversationZipReader
{
    private static readonly (string JsonName, BodySlot Slot)[] BodySlots =
    [
        ("request", BodySlot.ClientRequest),
        ("response", BodySlot.ClientResponse),
        ("provider_request", BodySlot.ProviderRequest),
        ("provider_response", BodySlot.ProviderResponse),
    ];

    /// <summary>
    /// Reads every turn from the zip at <paramref name="zipPath"/>. Verifies the manifest's schema
    /// version and the SHA-256 of <c>turns.jsonl</c> before yielding bodies.
    /// </summary>
    /// <param name="zipPath">Absolute path to a schema-v1 conversation zip.</param>
    /// <returns>The turns in <c>turns.jsonl</c> order.</returns>
    /// <exception cref="ConversationZipException">When the archive is unsafe, the wrong version, or hash-mismatched.</exception>
    /// <exception cref="ArgumentException">When <paramref name="zipPath"/> is blank.</exception>
    /// <exception cref="FileNotFoundException">When the zip does not exist.</exception>
    public static IReadOnlyList<ConversationZipTurn> ReadAll(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("The conversation zip was not found.", zipPath);
        }

        using var stream = File.OpenRead(zipPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = IndexEntries(zip);
        ValidateManifest(entries);

        var turnsEntry = RequireEntry(entries, "turns.jsonl");
        var turnsBytes = ReadEntry(turnsEntry);
        VerifyTurnsHash(entries, turnsBytes);

        var turns = new List<ConversationZipTurn>();
        using var reader = new StreamReader(new MemoryStream(turnsBytes), Encoding.UTF8);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            turns.Add(ParseTurn(line, lineNumber, entries));
        }

        return turns;
    }

    /// <summary>Builds a name→entry map and refuses zip-slip or duplicate names.</summary>
    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (!ConversationImporter.IsSafeEntryName(entry.FullName))
            {
                throw new ConversationZipException("The archive contains an entry name that is not allowed.");
            }

            if (entry.FullName.EndsWith('/')) continue;
            if (!entries.TryAdd(entry.FullName, entry))
            {
                throw new ConversationZipException("The archive contains two entries with the same name.");
            }
        }

        return entries;
    }

    /// <summary>Requires <c>manifest.json</c> and <c>schema_version == 1</c>.</summary>
    private static void ValidateManifest(Dictionary<string, ZipArchiveEntry> entries)
    {
        var manifestEntry = RequireEntry(entries, "manifest.json");
        using var document = JsonDocument.Parse(ReadEntry(manifestEntry));
        var root = document.RootElement;
        if (!root.TryGetProperty("schema_version", out var versionElement) ||
            !versionElement.TryGetInt32(out var version))
        {
            throw new ConversationZipException("The archive manifest is missing schema_version.");
        }

        if (version != ConversationExportWriter.SchemaVersion)
        {
            throw new ConversationZipException(
                $"Unsupported conversation archive schema version {version}; this reader supports version {ConversationExportWriter.SchemaVersion}.");
        }
    }

    /// <summary>Recomputes the SHA-256 of <c>turns.jsonl</c> and compares it to the manifest when present.</summary>
    private static void VerifyTurnsHash(Dictionary<string, ZipArchiveEntry> entries, byte[] turnsBytes)
    {
        var manifestEntry = entries["manifest.json"];
        using var document = JsonDocument.Parse(ReadEntry(manifestEntry));
        if (!document.RootElement.TryGetProperty("turns_jsonl", out var turnsMeta) ||
            turnsMeta.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (turnsMeta.TryGetProperty("length", out var lengthElement) &&
            lengthElement.TryGetInt64(out var length) &&
            length != turnsBytes.LongLength)
        {
            throw new ConversationZipException("The turns.jsonl length does not match the manifest.");
        }

        if (!turnsMeta.TryGetProperty("sha256", out var hashElement) ||
            hashElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var expected = hashElement.GetString();
        var actual = Convert.ToHexStringLower(SHA256.HashData(turnsBytes));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConversationZipException("The turns.jsonl hash does not match the manifest.");
        }
    }

    /// <summary>Parses one <c>turns.jsonl</c> line and loads its non-missing bodies.</summary>
    private static ConversationZipTurn ParseTurn(
        string line, int lineNumber, Dictionary<string, ZipArchiveEntry> entries)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new ConversationZipException($"turns.jsonl line {lineNumber} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryGuid(root, "archive_turn_id", out var archiveTurnId) ||
                !TryGuid(root, "archive_session_id", out var archiveSessionId) ||
                !root.TryGetProperty("client_session_id", out var clientElement) ||
                clientElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(clientElement.GetString()) ||
                !root.TryGetProperty("turn_sequence", out var sequenceElement) ||
                !sequenceElement.TryGetInt32(out var turnSequence) ||
                !root.TryGetProperty("created_at_utc", out var createdElement) ||
                createdElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(createdElement.GetString(), out var createdAtUtc))
            {
                throw new ConversationZipException(
                    $"turns.jsonl line {lineNumber} is missing a required identity field.");
            }

            string? metadataJson = null;
            if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                metadataJson = metadata.GetRawText();
            }

            byte[]? clientRequest = null;
            byte[]? clientResponse = null;
            byte[]? providerRequest = null;
            byte[]? providerResponse = null;

            if (root.TryGetProperty("bodies", out var bodies) && bodies.ValueKind == JsonValueKind.Object)
            {
                foreach (var (jsonName, slot) in BodySlots)
                {
                    if (!bodies.TryGetProperty(jsonName, out var body) || body.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var bytes = ReadBody(body, entries, lineNumber, jsonName);
                    switch (slot)
                    {
                        case BodySlot.ClientRequest:
                            clientRequest = bytes;
                            break;
                        case BodySlot.ClientResponse:
                            clientResponse = bytes;
                            break;
                        case BodySlot.ProviderRequest:
                            providerRequest = bytes;
                            break;
                        case BodySlot.ProviderResponse:
                            providerResponse = bytes;
                            break;
                    }
                }
            }

            return new ConversationZipTurn(
                archiveTurnId,
                archiveSessionId,
                clientElement.GetString()!,
                turnSequence,
                createdAtUtc,
                metadataJson,
                clientRequest,
                clientResponse,
                providerRequest,
                providerResponse);
        }
    }

    /// <summary>Loads one body entry, or returns <see langword="null"/> when the line marks it missing.</summary>
    private static byte[]? ReadBody(
        JsonElement body, Dictionary<string, ZipArchiveEntry> entries, int lineNumber, string name)
    {
        if (body.TryGetProperty("missing", out var missingElement) &&
            missingElement.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (!body.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String ||
            pathElement.GetString() is not { Length: > 0 } path)
        {
            throw new ConversationZipException(
                $"turns.jsonl line {lineNumber} body '{name}' has no path.");
        }

        if (!ConversationImporter.IsSafeEntryName(path) ||
            !path.StartsWith("conversations/", StringComparison.Ordinal))
        {
            throw new ConversationZipException(
                $"turns.jsonl line {lineNumber} body '{name}' path is not allowed.");
        }

        if (!entries.TryGetValue(path, out var entry))
        {
            throw new ConversationZipException(
                $"turns.jsonl line {lineNumber} body '{name}' refers to a missing zip entry.");
        }

        var bytes = ReadEntry(entry);
        if (body.TryGetProperty("length", out var lengthElement) &&
            lengthElement.TryGetInt64(out var length) &&
            length != bytes.LongLength)
        {
            throw new ConversationZipException(
                $"turns.jsonl line {lineNumber} body '{name}' length does not match the zip entry.");
        }

        if (body.TryGetProperty("sha256", out var hashElement) &&
            hashElement.ValueKind == JsonValueKind.String &&
            hashElement.GetString() is { Length: > 0 } expected)
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConversationZipException(
                    $"turns.jsonl line {lineNumber} body '{name}' hash does not match the zip entry.");
            }
        }

        return bytes;
    }

    private static ZipArchiveEntry RequireEntry(Dictionary<string, ZipArchiveEntry> entries, string name)
    {
        if (!entries.TryGetValue(name, out var entry))
        {
            throw new ConversationZipException($"The archive is missing required entry '{name}'.");
        }

        return entry;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool TryGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               Guid.TryParse(element.GetString(), out value);
    }

    private enum BodySlot
    {
        ClientRequest,
        ClientResponse,
        ProviderRequest,
        ProviderResponse,
    }
}
