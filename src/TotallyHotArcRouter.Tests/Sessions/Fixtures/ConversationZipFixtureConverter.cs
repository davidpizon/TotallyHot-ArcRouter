using System.IO.Compression;
using TotallyHot.ArcRouter.Sessions.Export;
using TotallyHot.ArcRouter.Sessions.Import;

namespace TotallyHot.ArcRouter.Tests.Sessions.Fixtures;

/// <summary>
/// Copies a schema-v1 conversation zip into a loose fixture directory with the same relative layout.
/// Refuses destinations at or under the repository root so an operator cannot accidentally check
/// conversation bodies into the tree (parent plan §3).
/// </summary>
internal static class ConversationZipFixtureConverter
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into a new directory at <paramref name="destinationDirectory"/>.
    /// </summary>
    /// <param name="zipPath">Absolute path to the source zip.</param>
    /// <param name="destinationDirectory">Absolute path of the directory to create and fill.</param>
    /// <param name="repositoryRoot">
    /// Absolute path of the repository root that must not receive the fixture. Tests inject a fake root;
    /// production callers pass the real checkout root.
    /// </param>
    /// <exception cref="ConversationZipException">When the destination is under the repo root or the zip is invalid.</exception>
    /// <exception cref="ArgumentException">When a path argument is blank or not fully qualified.</exception>
    public static void Convert(string zipPath, string destinationDirectory, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (!Path.IsPathFullyQualified(zipPath))
        {
            throw new ArgumentException("The zip path must be absolute.", nameof(zipPath));
        }

        if (!Path.IsPathFullyQualified(destinationDirectory))
        {
            throw new ArgumentException("The destination directory must be absolute.", nameof(destinationDirectory));
        }

        if (!Path.IsPathFullyQualified(repositoryRoot))
        {
            throw new ArgumentException("The repository root must be absolute.", nameof(repositoryRoot));
        }

        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("The conversation zip was not found.", zipPath);
        }

        var destination = Path.GetFullPath(destinationDirectory);
        var root = Path.GetFullPath(repositoryRoot);
        if (IsUnderOrEqual(destination, root))
        {
            throw new ConversationZipException(
                "Refusing to write a conversation fixture under the repository root; pass a destination outside the tree.");
        }

        using var stream = File.OpenRead(zipPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        // Validate layout before creating the destination so a refused zip leaves nothing on disk.
        var pending = new List<(string RelativePath, byte[] Bytes)>();
        var sawManifest = false;
        var sawTurns = false;
        foreach (var entry in zip.Entries)
        {
            if (!ConversationImporter.IsSafeEntryName(entry.FullName))
            {
                throw new ConversationZipException("The archive contains an entry name that is not allowed.");
            }

            if (entry.FullName.EndsWith('/')) continue;

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            var bytes = buffer.ToArray();

            if (entry.FullName == "manifest.json")
            {
                sawManifest = true;
                ValidateSchemaVersion(bytes);
            }
            else if (entry.FullName == "turns.jsonl")
            {
                sawTurns = true;
            }

            pending.Add((entry.FullName, bytes));
        }

        if (!sawManifest)
        {
            throw new ConversationZipException("The archive is missing required entry 'manifest.json'.");
        }

        if (!sawTurns)
        {
            throw new ConversationZipException("The archive is missing required entry 'turns.jsonl'.");
        }

        Directory.CreateDirectory(destination);
        foreach (var (relativePath, bytes) in pending)
        {
            var target = Path.GetFullPath(Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnderOrEqual(target, destination))
            {
                throw new ConversationZipException("The archive contains an entry name that is not allowed.");
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (File.Exists(target))
            {
                throw new ConversationZipException($"Destination already contains '{relativePath}'.");
            }

            File.WriteAllBytes(target, bytes);
        }
    }

    /// <summary>Reports whether <paramref name="path"/> is <paramref name="folder"/> or a child of it.</summary>
    internal static bool IsUnderOrEqual(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder), comparison)
               || (path + Path.DirectorySeparatorChar).StartsWith(root, comparison);
    }

    private static void ValidateSchemaVersion(byte[] manifestBytes)
    {
        using var document = System.Text.Json.JsonDocument.Parse(manifestBytes);
        if (!document.RootElement.TryGetProperty("schema_version", out var versionElement) ||
            !versionElement.TryGetInt32(out var version))
        {
            throw new ConversationZipException("The archive manifest is missing schema_version.");
        }

        if (version != ConversationExportWriter.SchemaVersion)
        {
            throw new ConversationZipException(
                $"Unsupported conversation archive schema version {version}; this converter supports version {ConversationExportWriter.SchemaVersion}.");
        }
    }
}
