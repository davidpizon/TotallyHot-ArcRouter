using System.Runtime.Versioning;
using System.Security.Cryptography;
using ILogger = Serilog.ILogger;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// Makes an older Docker named volume at <c>/data</c> owner-only in place, at startup, as the container's
/// service account (plan §3.1, "Docker").
/// </summary>
/// <remarks>
/// <para>
/// A container needs no elevated step. Inside it the only accounts are root and the service account,
/// which already owns every file in the volume, and nothing on the host path to a named volume gives
/// other host accounts a write bit - so nothing in it was planted. Other host accounts could open files
/// by full path, though (the volume and file names are fixed), and a descriptor opened before a mode
/// change keeps working. So a mode change alone is not enough:
/// </para>
/// <list type="bullet">
/// <item><description>
/// each regular file is copied into a new <c>0600</c> file and renamed over the original, so an earlier
/// descriptor keeps only the old, unlinked inode;
/// </description></item>
/// <item><description>
/// a symbolic link, special file, or file with more than one hard link is renamed into a <c>0700</c>
/// quarantine folder instead of being copied;
/// </description></item>
/// <item><description>
/// model files follow <see cref="DataDirectoryMigrationRules"/>: a pinned embedding file is kept only if
/// its SHA-256 matches, and other model files are deleted for the router to download again;
/// </description></item>
/// <item><description>
/// last, every directory and then <c>/data</c> itself becomes <c>0700</c>, and the migration marker is
/// written.
/// </description></item>
/// </list>
/// <para>
/// <c>/data</c> is the mount point, so it cannot be swapped like the other platforms' trees. Each per-file
/// rename is atomic instead, and a partial copy a crash left behind is deleted on the next start.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class ContainerDataDirectoryMigration
{
    private readonly ILogger _logger;
    private readonly string _root;
    private string? _quarantine;

    /// <summary>Initializes a new instance of the <see cref="ContainerDataDirectoryMigration"/> class.</summary>
    /// <param name="root">The volume's mount point, normally <c>/data</c>.</param>
    /// <param name="logger">Receives one line per decision, with static templates.</param>
    public ContainerDataDirectoryMigration(string root, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(logger);

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _logger = logger;
    }

    /// <summary>Copies, quarantines and re-modes the volume's contents, then marks it protected.</summary>
    public void Run()
    {
        Rewrite(_root);
        TightenDirectories(_root);

        var marker = Path.Combine(_root, DataDirectoryMigrationRules.ProtectedMarkerFileName);
        if (!File.Exists(marker))
            using (new FileStream(marker, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew,
                       Access = FileAccess.Write,
                       UnixCreateMode = UnixDataDirectorySecurity.OwnerOnlyFileMode
                   }))
            {
            }

        _logger.Information("Made the container data volume {Root} owner-only.", _root);
    }

    private void Rewrite(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).ToList())
        {
            if (UnixNative.LStat(path) is not { } status) continue;

            if (status.Kind == UnixFileKind.Directory)
            {
                if (!string.Equals(path, Path.Combine(_root, DataDirectoryMigrationRules.QuarantineDirectoryName),
                        StringComparison.Ordinal))
                    Rewrite(path);
                continue;
            }

            if (path.EndsWith(DataDirectoryMigrationRules.PartialCopySuffix, StringComparison.Ordinal))
            {
                File.Delete(path);
                continue;
            }

            if (status.Kind != UnixFileKind.RegularFile || status.LinkCount > 1)
            {
                MoveToQuarantine(path);
                continue;
            }

            // The shared rules decide what happens to model files: a pinned embedding file is kept only if its
            // hash matches (OnnxEmbeddingClient never re-hashes a cached file), and every other model file is
            // re-downloadable, so it is dropped. Quarantine decisions - only appsettings.local.json - are kept
            // in place: inside a container only the service account could have written it (see the remarks),
            // and it is how a container operator configures the router.
            var (decision, pinnedSha256) =
                DataDirectoryMigrationRules.Decide(Path.GetRelativePath(_root, path).Replace('\\', '/'));
            if (decision == MigrationFileDecision.Discard)
            {
                File.Delete(path);
                _logger.Information("Discarded {Path}; the router downloads it again when it needs it.", path);
                continue;
            }

            var partial = path + DataDirectoryMigrationRules.PartialCopySuffix;
            string actualSha256;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destination = new FileStream(partial, new FileStreamOptions
                       {
                           Mode = FileMode.CreateNew,
                           Access = FileAccess.Write,
                           Share = FileShare.None,
                           UnixCreateMode = UnixDataDirectorySecurity.OwnerOnlyFileMode
                       }))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        destination.Write(buffer, 0, read);
                    }

                    destination.Flush(flushToDisk: true);
                }

                actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (decision == MigrationFileDecision.AdoptIfHashMatches &&
                !string.Equals(actualSha256, pinnedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                File.Delete(path);
                _logger.Warning(
                    "Discarded {Path}: its SHA-256 does not match the pinned value. It is downloaded again on first use.",
                    path);
                continue;
            }

            UnixNative.Rename(partial, path);
        }
    }

    private void MoveToQuarantine(string path)
    {
        if (_quarantine is null)
        {
            _quarantine = DataDirectoryMigrationRules.NewQuarantinePath(_root);
            Directory.CreateDirectory(_quarantine, UnixDataDirectorySecurity.OwnerOnlyDirectoryMode);
        }

        // The relative path is kept, not flattened: flattening is not injective (a/b and a_b would collide),
        // and rename(2) would silently replace the entry already quarantined under that name.
        var destination = Path.Combine(_quarantine, Path.GetRelativePath(_root, path));
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent, UnixDataDirectorySecurity.OwnerOnlyDirectoryMode);
        UnixNative.Rename(path, destination);
        _logger.Warning("Moved {Path}, a link or multiply-linked file, into {Quarantine} instead of copying it.", path,
            destination);
    }

    private static void TightenDirectories(string directory)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            if (UnixNative.LStat(child) is { Kind: UnixFileKind.Directory })
                TightenDirectories(child);

        File.SetUnixFileMode(directory, UnixDataDirectorySecurity.OwnerOnlyDirectoryMode);
    }
}
