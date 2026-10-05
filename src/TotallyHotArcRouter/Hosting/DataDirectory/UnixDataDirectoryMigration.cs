using System.Runtime.Versioning;
using System.Security.Cryptography;
using ILogger = Serilog.ILogger;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// Moves a pre-ADR-0024 Linux or macOS data directory (state or logs) into a new owner-only tree, run as
/// root by <c>install.sh</c> through <c>--migrate-data-directory</c> with the service stopped (plan §3.1).
/// </summary>
/// <remarks>
/// <para>The order matters, because a copy cannot see a writer:</para>
/// <list type="number">
/// <item><description>
/// <b>Lock.</b> Every directory in the old tree, top-down, becomes root-owned with mode <c>0700</c>. Each
/// directory is changed only after its parent is locked, so no other account can swap a name under it,
/// and a symbolic link is never followed - <c>lchown</c> changes a link itself, and links are never
/// chmodded. A lookup checks a directory's current mode, so from here on no other account can open
/// anything new in the tree, even through a directory descriptor or working directory opened earlier.
/// </description></item>
/// <item><description>
/// <b>Look for writers</b> with an <see cref="IOpenWriterScanner"/>. If any process still holds a file open
/// for writing, migration stops and leaves the tree locked. The service fails closed on a locked tree,
/// and the next run starts again from step 1.
/// </description></item>
/// <item><description>
/// <b>Copy</b> each accepted file into a new <c>0600</c> file in a new <c>0700</c> tree owned by the service
/// account, and unlink the original once the copy is flushed. A descriptor opened before migration keeps
/// only the old, unlinked inode. Rejected entries (links, extra hard-link names, files with untrusted
/// owners, <c>appsettings.local.json</c>) stay where they are.
/// </description></item>
/// <item><description>
/// <b>Swap.</b> The old tree is renamed aside as a quarantine. It stays root-owned and <c>0700</c>, which
/// is enough on Unix, where every lookup needs search permission on each directory in the path. The new
/// tree, with <see cref="DataDirectoryMigrationRules.ProtectedMarkerFileName"/>, is renamed into place.
/// </description></item>
/// </list>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class UnixDataDirectoryMigration
{
    private const uint RootUid = 0;

    private readonly uint? _legacyUid;
    private readonly ILogger _logger;
    private readonly string _root;
    private readonly IOpenWriterScanner _scanner;
    private readonly uint _serviceGid;
    private readonly uint _serviceUid;

    private int _adopted;
    private int _discarded;
    private int _quarantined;

    /// <summary>Initializes a new instance of the <see cref="UnixDataDirectoryMigration"/> class.</summary>
    /// <param name="root">The directory to migrate: the state directory, or on Linux the separate logs directory.</param>
    /// <param name="serviceUid">The service account's uid, which owns the new tree.</param>
    /// <param name="serviceGid">The service account's primary gid.</param>
    /// <param name="legacyUid">The account that ran the install through <c>sudo</c> (<c>SUDO_UID</c>), if any; a tree it owns is migrated, not treated as a squat.</param>
    /// <param name="scanner">Finds processes still writing into the locked tree.</param>
    /// <param name="logger">Receives one line per decision, with static templates.</param>
    public UnixDataDirectoryMigration(string root, uint serviceUid, uint serviceGid, uint? legacyUid,
        IOpenWriterScanner scanner, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(logger);

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _serviceUid = serviceUid;
        _serviceGid = serviceGid;
        _legacyUid = legacyUid;
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>Brings the directory to the protected state, resuming any earlier, interrupted run first.</summary>
    /// <exception cref="DataDirectoryMigrationBlockedException">Migration had to stop; see its message.</exception>
    public DataDirectoryMigrationResult Run()
    {
        CompleteInterruptedSwap();

        var inspection = Inspect(_root);
        switch (inspection.State)
        {
            case DataDirectoryState.Missing:
                CreateOwnedDirectory(_root);
                WriteMarker(_root);
                _logger.Information("Created the protected data directory {Root}.", _root);
                return new DataDirectoryMigrationResult(DataDirectoryMigrationOutcome.Created);

            case DataDirectoryState.Protected when HasMarker(_root):
                return new DataDirectoryMigrationResult(DataDirectoryMigrationOutcome.AlreadyProtected);

            case DataDirectoryState.Inaccessible:
                throw new DataDirectoryMigrationBlockedException($"Cannot read '{_root}'. Run --migrate-data-directory as root.");
        }

        var status = UnixNative.LStat(_root);
        if (status is not { Kind: UnixFileKind.Directory } directory || !IsTrustedOwner(directory.Uid))
            return SetSquatAside(inspection);

        _logger.Information("Migrating the data directory {Root} into an owner-only tree.", _root);

        Lock(_root);

        var writers = _scanner.FindWriters(_root);
        if (writers.Count > 0)
            throw new DataDirectoryMigrationBlockedException(
                $"'{_root}' is locked, but these processes can still write into it, so it was not copied: {string.Join("; ", writers)}. Stop them and run --migrate-data-directory again.");

        var staging = FindResumableStaging() ?? CreateStaging();
        Copy(sourceDirectory: _root, destinationDirectory: staging, relativePrefix: "");
        WriteMarker(staging);

        var quarantine = DataDirectoryMigrationRules.NewLockedQuarantinePath(_root);
        UnixNative.Rename(_root, quarantine);
        UnixNative.Rename(staging, _root);
        _logger.Information("The protected tree now stands at {Root}.", _root);

        if (PruneEmptyDirectories(quarantine))
        {
            Directory.Delete(quarantine);
            quarantine = null;
        }
        else
        {
            _logger.Warning(
                "Entries migration did not adopt are in {Quarantine}, which only root can open. An administrator should review and delete it.",
                quarantine);
        }

        return new DataDirectoryMigrationResult(DataDirectoryMigrationOutcome.Migrated, Adopted: _adopted,
            Quarantined: _quarantined, Discarded: _discarded, SetAsidePath: quarantine);
    }

    private DataDirectoryInspection Inspect(string path)
    {
        return UnixDataDirectorySecurity.Inspect(path, [_serviceUid]);
    }

    private bool IsTrustedOwner(uint uid)
    {
        return uid == RootUid || uid == _serviceUid || uid == _legacyUid;
    }

    private void CompleteInterruptedSwap()
    {
        if (UnixNative.LStat(_root) is not null) return;

        var staging = FindResumableStaging();
        if (staging is null) return;

        WriteMarker(staging);
        UnixNative.Rename(staging, _root);
        _logger.Information("Completed an interrupted migration: moved {Staging} into place at {Root}.", staging,
            _root);
    }

    private string? FindResumableStaging()
    {
        foreach (var candidate in DataDirectoryMigrationRules.FindStaging(_root))
        {
            if (Inspect(candidate).State == DataDirectoryState.Protected)
            {
                foreach (var partial in Directory.EnumerateFiles(candidate,
                             "*" + DataDirectoryMigrationRules.PartialCopySuffix, SearchOption.AllDirectories))
                    File.Delete(partial);
                return candidate;
            }

            _logger.Warning(
                "Ignoring {Candidate}: it looks like a migration staging tree but is not owned by the service account with mode 0700.",
                candidate);
        }

        return null;
    }

    private string CreateStaging()
    {
        var staging = DataDirectoryMigrationRules.NewStagingPath(_root);
        CreateOwnedDirectory(staging);
        return staging;
    }

    private DataDirectoryMigrationResult SetSquatAside(DataDirectoryInspection inspection)
    {
        var aside = DataDirectoryMigrationRules.NewSquattedPath(_root);
        UnixNative.Rename(_root, aside);
        CreateOwnedDirectory(_root);
        WriteMarker(_root);

        _logger.Warning(
            "The data directory {Root} was owned by {Owner}, not by root, the service account or the installing account, so nothing in it can be trusted. It was renamed to {SetAside} untouched, and a fresh protected directory was created. An administrator should review that copy and delete it.",
            _root, inspection.Owner ?? "an unknown account", aside);

        return new DataDirectoryMigrationResult(DataDirectoryMigrationOutcome.SquatSetAside, SetAsidePath: aside);
    }

    /// <summary>
    /// Makes every directory in the tree root-owned and <c>0700</c>, parents before children. A directory on
    /// a different mount from its parent stops migration before anything in it changes: a bind or FUSE mount
    /// planted below the root would otherwise let this root-run walk chown, copy and unlink files outside the
    /// data directory. The root itself may be a mount point.
    /// </summary>
    private static void Lock(string directory)
    {
        var status = UnixNative.LStat(directory)!.Value;
        UnixNative.LChown(directory, RootUid, RootUid);
        File.SetUnixFileMode(directory, UnixDataDirectorySecurity.OwnerOnlyDirectoryMode);

        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            if (UnixNative.LStat(child) is not { Kind: UnixFileKind.Directory } childStatus) continue;

            if (!childStatus.IsOnSameMountAs(status))
                throw new DataDirectoryMigrationBlockedException(
                    $"'{child}' is a mount point inside the data directory, so migrating it would reach outside the tree. Unmount it and run --migrate-data-directory again.");

            Lock(child);
        }
    }

    private void Copy(string sourceDirectory, string destinationDirectory, string relativePrefix)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(sourceDirectory).ToList())
        {
            var name = Path.GetFileName(path);
            var relativePath = relativePrefix + name;
            if (UnixNative.LStat(path) is not { } status) continue;

            switch (status.Kind)
            {
                case UnixFileKind.Directory:
                    var destination = Path.Combine(destinationDirectory, name);
                    if (!Directory.Exists(destination)) CreateOwnedDirectory(destination);
                    Copy(path, destination, relativePath + "/");
                    continue;

                case UnixFileKind.SymbolicLink or UnixFileKind.Other:
                    Reject(path, "it is a link or special file");
                    continue;
            }

            if (status.LinkCount > 1)
            {
                Reject(path, "it has more than one hard link");
                continue;
            }

            if (!IsTrustedOwner(status.Uid))
            {
                Reject(path, "it is owned by an untrusted account");
                continue;
            }

            var (decision, pinnedSha256) = DataDirectoryMigrationRules.Decide(relativePath);
            switch (decision)
            {
                case MigrationFileDecision.Quarantine:
                    Reject(path, "migration never adopts it");
                    continue;

                case MigrationFileDecision.Discard:
                    File.Delete(path);
                    _discarded++;
                    _logger.Information("Discarded {Path}; the router downloads it again when it needs it.", path);
                    continue;
            }

            if (CopyInto(path, Path.Combine(destinationDirectory, name), pinnedSha256))
            {
                _adopted++;
            }
            else
            {
                _discarded++;
                _logger.Warning(
                    "Discarded {Path}: its SHA-256 does not match the pinned value. It is downloaded again on first use.",
                    path);
            }

            File.Delete(path);
        }
    }

    private void Reject(string path, string reason)
    {
        _quarantined++;
        _logger.Warning("Left {Path} behind in the locked old tree, because {Reason}.", path, reason);
    }

    private bool CopyInto(string sourcePath, string destinationPath, string? expectedSha256)
    {
        var partialPath = destinationPath + DataDirectoryMigrationRules.PartialCopySuffix;
        string actualSha256;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            // The source's parent is locked, so its name cannot be swapped for a link between the lstat
            // above and this open.
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(partialPath, new FileStreamOptions
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

        if (expectedSha256 is not null &&
            !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partialPath);
            return false;
        }

        // Explicitly, not just through UnixCreateMode: the umask filters that, and under a restrictive one the
        // copy would be 0000 - readable by root, which finishes the migration, but not by the service.
        File.SetUnixFileMode(partialPath, UnixDataDirectorySecurity.OwnerOnlyFileMode);
        UnixNative.LChown(partialPath, _serviceUid, _serviceGid);
        File.Move(partialPath, destinationPath, overwrite: true);
        return true;
    }

    private void CreateOwnedDirectory(string path)
    {
        UnixDataDirectorySecurity.CreateProtected(path);
        UnixNative.LChown(path, _serviceUid, _serviceGid);
    }

    private void WriteMarker(string directory)
    {
        var marker = Path.Combine(directory, DataDirectoryMigrationRules.ProtectedMarkerFileName);
        if (File.Exists(marker)) return;

        using (new FileStream(marker, new FileStreamOptions
               {
                   Mode = FileMode.CreateNew,
                   Access = FileAccess.Write,
                   UnixCreateMode = UnixDataDirectorySecurity.OwnerOnlyFileMode
               }))
        {
        }

        File.SetUnixFileMode(marker, UnixDataDirectorySecurity.OwnerOnlyFileMode);
        UnixNative.LChown(marker, _serviceUid, _serviceGid);
    }

    /// <summary>Whether <paramref name="directory"/> carries the migration marker.</summary>
    public static bool HasMarker(string directory)
    {
        return File.Exists(Path.Combine(directory, DataDirectoryMigrationRules.ProtectedMarkerFileName));
    }

    /// <summary>Deletes every empty directory under <paramref name="directory"/>, returning whether it is now empty itself.</summary>
    private static bool PruneEmptyDirectories(string directory)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(directory).ToList())
            if (UnixNative.LStat(child) is { Kind: UnixFileKind.Directory } && PruneEmptyDirectories(child))
                Directory.Delete(child);

        return !Directory.EnumerateFileSystemEntries(directory).Any();
    }
}
