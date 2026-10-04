using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ILogger = Serilog.ILogger;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// Moves a pre-ADR-0024 Windows data directory into a new tree that carries the protected DACL, run by
/// the elevated <c>--migrate-data-directory</c> command (plan §3.1, "Migration builds a new protected
/// tree").
/// </summary>
/// <remarks>
/// <para>
/// The old tree was writable by every local account, and changing an ACL does not revoke a handle opened
/// before the change. So migration never re-secures the old tree in place. It builds a new protected tree
/// beside it, copies in each file that passes the checks below, deletes every original, then swaps the
/// trees. When it finishes, no object from the old tree is left anywhere a standard account can open by
/// path - which matters on Windows, where <c>SeChangeNotifyPrivilege</c> lets any account skip the
/// traverse check on parent directories.
/// </para>
/// <para>
/// Each file is opened once, exclusively and without following a reparse point, and checked, copied and
/// deleted through that one handle. If another process holds a file open, the exclusive open fails and
/// migration stops, naming the file:
/// <list type="bullet">
/// <item><description>a junction or symbolic link is deleted itself; its target is never opened;</description></item>
/// <item><description>a file with more than one hard link loses only this name, so the outside file is unchanged;</description></item>
/// <item><description>
/// a file owned by anyone other than <c>SYSTEM</c>, <c>Administrators</c> or the legacy owner, and
/// <c>appsettings.local.json</c>, are copied into the quarantine folder inside the new root;
/// </description></item>
/// <item><description>the rest follows <see cref="DataDirectoryMigrationRules.Decide"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Deviation from the plan, recorded in the plan doc.</b> The plan moves accepted files by renaming
/// them through their handle and then resetting their owner and ACL. This copies them instead: a new
/// file created in the protected tree is born with the right owner and inherited DACL, which removes the
/// question of whether a handle-based ACL reset recomputes inheritance from the new parent. It costs
/// transient disk space of one file at a time.
/// </para>
/// <para>
/// <b>Crash safety.</b> Every step is idempotent and the filesystem itself records progress: an adopted
/// file exists only in the new tree once its original is gone, a partial copy carries
/// <see cref="DataDirectoryMigrationRules.PartialCopySuffix"/>, and the staging and retired trees are
/// found again by name. A staging tree is only resumed if it passes the same protection check as a root,
/// so a directory a standard account planted beside the root cannot steer recovery.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDataDirectoryMigration
{
    private const int MaxDrainPasses = 3;

    private readonly SecurityIdentifier? _legacyOwner;
    private readonly ILogger _logger;
    private readonly WindowsDirectoryPolicy _policy;
    private readonly string _root;

    private int _adopted;
    private int _discarded;
    private int _quarantined;
    private string? _quarantinePath;

    /// <summary>Initializes a new instance of the <see cref="WindowsDataDirectoryMigration"/> class.</summary>
    /// <param name="root">The data directory to migrate, normally <c>%ProgramData%\TotallyHotArcRouter</c>.</param>
    /// <param name="policy">The protection rules for the new tree; <see cref="WindowsDirectoryPolicy.Machine"/> in production.</param>
    /// <param name="legacyOwner">
    /// The account that installed the router (the MSI's <c>UserSID</c>), or the account running the command.
    /// A root it owns is migrated; a root owned by anyone else outside the policy is a squat.
    /// </param>
    /// <param name="logger">Receives one line per decision, with static templates.</param>
    public WindowsDataDirectoryMigration(string root, WindowsDirectoryPolicy policy, SecurityIdentifier? legacyOwner,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _policy = policy;
        _legacyOwner = legacyOwner;
        _logger = logger;
    }

    /// <summary>
    /// Brings the data directory to the protected state, resuming any earlier, interrupted run first.
    /// </summary>
    /// <exception cref="DataDirectoryMigrationBlockedException">Migration had to stop; see its message.</exception>
    public DataDirectoryMigrationResult Run()
    {
        CompleteInterruptedSwap();

        var inspection = WindowsDataDirectorySecurity.Inspect(_root, _policy);
        switch (inspection.State)
        {
            case DataDirectoryState.Missing:
                WindowsDataDirectorySecurity.CreateProtected(_root, _policy);
                _logger.Information("Created the protected data directory {Root}.", _root);
                return new DataDirectoryMigrationResult(DataDirectoryMigrationOutcome.Created);

            case DataDirectoryState.Protected:
                DrainRetiredTrees();
                return Result(DataDirectoryMigrationOutcome.AlreadyProtected);

            case DataDirectoryState.Inaccessible:
                throw new DataDirectoryMigrationBlockedException(
                    $"Cannot read the permissions of '{_root}'. Run --migrate-data-directory from an elevated prompt.");
        }

        if (!IsTrustedLegacyOwner(_root)) return SetSquatAside(inspection);

        _logger.Information("Migrating the data directory {Root} into a protected tree, because {Reason}.", _root,
            inspection.Reason);

        var staging = FindResumableStaging() ?? CreateStaging();
        _quarantinePath = DataDirectoryMigrationRules.NewQuarantinePath(staging);

        DrainUntilEmpty(source: _root, destination: staging, adopt: true);

        var retired = DataDirectoryMigrationRules.NewRetiredPath(_root);
        MoveDirectoryOrBlock(_root, retired);
        MoveDirectoryOrBlock(staging, _root);
        _logger.Information("The protected tree now stands at {Root}.", _root);

        // Anything a process created in the old tree between the last drain pass and the swap is in the
        // retired tree now; treat it as untrusted.
        _quarantinePath = Path.Combine(_root, Path.GetRelativePath(staging, _quarantinePath));
        DrainRetiredTree(retired);

        return Result(DataDirectoryMigrationOutcome.Migrated);
    }

    /// <summary>
    /// Finishes a swap a crash interrupted: the old root was already renamed away, so the protected
    /// staging tree just needs renaming into place. Startup never creates an empty root in this state.
    /// </summary>
    private void CompleteInterruptedSwap()
    {
        if (Path.Exists(_root)) return;

        var staging = FindResumableStaging();
        if (staging is null) return;

        MoveDirectoryOrBlock(staging, _root);
        _logger.Information("Completed an interrupted migration: moved {Staging} into place at {Root}.", staging,
            _root);
    }

    private string? FindResumableStaging()
    {
        foreach (var candidate in DataDirectoryMigrationRules.FindStaging(_root))
        {
            if (WindowsDataDirectorySecurity.Inspect(candidate, _policy).State == DataDirectoryState.Protected)
            {
                DeletePartialCopies(candidate);
                return candidate;
            }

            _logger.Warning(
                "Ignoring {Candidate}: it looks like a migration staging tree but is not protected, so it was not created by this router.",
                candidate);
        }

        return null;
    }

    private string CreateStaging()
    {
        var staging = DataDirectoryMigrationRules.NewStagingPath(_root);
        WindowsDataDirectorySecurity.CreateProtected(staging, _policy);
        return staging;
    }

    private bool IsTrustedLegacyOwner(string path)
    {
        // The link check comes first: reading a junction's owner by path would read its target's. A file
        // standing where the root should be is no legacy root either; it is set aside like any squat.
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
            return false;
        if (WindowsDataDirectorySecurity.TryGetOwner(path) is not { } owner) return false;

        return _policy.IsTrustedOwner(owner) || owner == _legacyOwner;
    }

    private DataDirectoryMigrationResult SetSquatAside(DataDirectoryInspection inspection)
    {
        var aside = DataDirectoryMigrationRules.NewSquattedPath(_root);
        if (File.Exists(_root))
            File.Move(_root, aside);
        else
            MoveDirectoryOrBlock(_root, aside);
        WindowsDataDirectorySecurity.CreateProtected(_root, _policy);

        _logger.Warning(
            "The data directory {Root} was owned by {Owner}, not by SYSTEM, Administrators or the installing account, so nothing in it can be trusted. It was renamed to {SetAside} untouched, and a fresh protected directory was created. An administrator should review that copy - it may still hold the operator's data - and delete it.",
            _root, inspection.Owner ?? "an unknown account", aside);

        return new DataDirectoryMigrationResult(DataDirectoryMigrationOutcome.SquatSetAside, SetAsidePath: aside);
    }

    private void DrainRetiredTrees()
    {
        foreach (var retired in DataDirectoryMigrationRules.FindRetired(_root))
        {
            _quarantinePath ??= DataDirectoryMigrationRules.NewQuarantinePath(_root);
            DrainRetiredTree(retired);
        }
    }

    private void DrainRetiredTree(string retired)
    {
        // Only a real directory is drained. Retired trees are created by this class's own rename, so a
        // link here was planted; remove the link and leave its target alone.
        if (File.GetAttributes(retired).HasFlag(FileAttributes.ReparsePoint))
        {
            DeleteLink(retired);
            return;
        }

        DrainUntilEmpty(source: retired, destination: null, adopt: false);
        DeleteEmptyDirectoryOrBlock(retired);
        _logger.Information("Removed the emptied old tree {Retired}.", retired);
    }

    /// <summary>
    /// Empties <paramref name="source"/>, repeating the walk a few times in case a process holding an old
    /// directory handle creates entries mid-walk, then fails closed if the tree still is not empty.
    /// </summary>
    private void DrainUntilEmpty(string source, string? destination, bool adopt)
    {
        for (var pass = 0; pass < MaxDrainPasses; pass++)
        {
            Drain(sourceDirectory: source, destinationDirectory: destination, relativePrefix: "", adopt: adopt);
            if (!Directory.EnumerateFileSystemEntries(source).Any()) return;
        }

        throw new DataDirectoryMigrationBlockedException(
            $"Entries keep appearing in '{source}' while it is being migrated. Stop every process using it and run --migrate-data-directory again.");
    }

    private void Drain(string sourceDirectory, string? destinationDirectory, string relativePrefix, bool adopt)
    {
        // Pinned for the whole walk of its children, and every ancestor is pinned by the frames above, so
        // no directory on the path can be swapped for a junction while paths built through it are in use.
        // Without this, an account that created a subdirectory could replace it with a junction between the
        // enumeration and the recursion, and this SYSTEM process would copy and delete files elsewhere.
        using var pin = PinDirectory(sourceDirectory);
        if (pin is null) return;

        foreach (var entry in new DirectoryInfo(sourceDirectory).EnumerateFileSystemInfos().ToList())
        {
            var relativePath = relativePrefix + entry.Name;

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _logger.Information("Removed the link {Path}, which pointed to {Target}; its target was not touched.",
                    entry.FullName, entry.LinkTarget ?? "an unknown target");
                DeleteLink(entry.FullName);
                _discarded++;
                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                Drain(sourceDirectory: entry.FullName,
                    destinationDirectory: destinationDirectory is null
                        ? null
                        : Path.Combine(destinationDirectory, entry.Name),
                    relativePrefix: relativePath + "/", adopt: adopt);

                if (!Directory.EnumerateFileSystemEntries(entry.FullName).Any())
                    DeleteEmptyDirectoryOrBlock(entry.FullName);
                continue;
            }

            MigrateFile(path: entry.FullName, relativePath: relativePath,
                destinationPath: destinationDirectory is null ? null : Path.Combine(destinationDirectory, entry.Name),
                adopt: adopt);
        }
    }

    /// <summary>
    /// Pins the directory at <paramref name="path"/> (see <see cref="WindowsFileNative.OpenDirectoryPinned"/>),
    /// returning <see langword="null"/> when it has vanished, or when it turned out to be a link, which is
    /// then removed without touching its target.
    /// </summary>
    private SafeFileHandle? PinDirectory(string path)
    {
        var (handle, error) = WindowsFileNative.OpenDirectoryPinned(path);
        if (handle is null)
        {
            if (WindowsFileNative.IsNotFound(error)) return null;

            throw new DataDirectoryMigrationBlockedException(
                $"Cannot open the directory '{path}' for migration; another process may hold it. Stop it and run --migrate-data-directory again.",
                new Win32Exception(error));
        }

        var (attributes, _) = WindowsFileNative.GetInformation(handle);
        if (!attributes.HasFlag(FileAttributes.ReparsePoint) && attributes.HasFlag(FileAttributes.Directory))
            return handle;

        handle.Dispose();
        _logger.Information("Removed the link {Path}; its target was not touched.", path);
        DeleteLink(path);
        _discarded++;
        return null;
    }

    private void MigrateFile(string path, string relativePath, string? destinationPath, bool adopt)
    {
        var (handle, error) = WindowsFileNative.OpenExclusive(path);
        if (handle is null)
        {
            if (WindowsFileNative.IsNotFound(error)) return;

            if (error is WindowsFileNative.ErrorSharingViolation or WindowsFileNative.ErrorLockViolation)
                throw new DataDirectoryMigrationBlockedException(
                    $"'{path}' is open in another process. Stop the router and anything else using the data directory, then run --migrate-data-directory again.");

            throw new DataDirectoryMigrationBlockedException($"Cannot open '{path}' for migration.",
                new Win32Exception(error));
        }

        using (handle)
        {
            var (attributes, linkCount) = WindowsFileNative.GetInformation(handle);

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _logger.Information("Removed the link {Path}; its target was not touched.", path);
                DeleteThroughHandle(handle, path);
                _discarded++;
                return;
            }

            if (linkCount > 1)
            {
                _logger.Information(
                    "Removed {Path}, a hard link with {LinkCount} names; the file lives on under its other names, unchanged.",
                    path, linkCount);
                DeleteThroughHandle(handle, path);
                _discarded++;
                return;
            }

            var owner = WindowsFileNative.GetOwner(handle);
            var ownerTrusted = _policy.IsTrustedOwner(owner) || owner == _legacyOwner;
            var (decision, pinnedSha256) = DataDirectoryMigrationRules.Decide(relativePath);

            if (!adopt || !ownerTrusted) decision = MigrationFileDecision.Quarantine;

            using var source = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            switch (decision)
            {
                case MigrationFileDecision.Discard:
                    _logger.Information("Discarded {Path}; the router downloads it again when it needs it.", path);
                    _discarded++;
                    break;

                case MigrationFileDecision.Quarantine:
                    var quarantined = Path.Combine(_quarantinePath!, relativePath);
                    CopyInto(source, quarantined, expectedSha256: null);
                    _logger.Warning(
                        "Kept {Path} (owner {Owner}) out of the live data directory; an administrator can review the copy at {QuarantinedPath}.",
                        path, WindowsDataDirectorySecurity.Describe(owner), quarantined);
                    _quarantined++;
                    break;

                default:
                    if (CopyInto(source, destinationPath!, pinnedSha256))
                    {
                        _adopted++;
                    }
                    else
                    {
                        _logger.Warning(
                            "Discarded {Path}: its SHA-256 does not match the pinned value, so it was not downloaded by this router. It is downloaded again on first use.",
                            path);
                        _discarded++;
                    }

                    break;
            }

            DeleteThroughHandle(handle, path);
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destinationPath"/> through a partial-copy name,
    /// flushed to disk before the rename, so a crash never leaves a truncated file under the real name.
    /// When <paramref name="expectedSha256"/> is set, a mismatching copy is deleted and this returns
    /// <see langword="false"/>.
    /// </summary>
    private static bool CopyInto(Stream source, string destinationPath, string? expectedSha256)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var partialPath = destinationPath + DataDirectoryMigrationRules.PartialCopySuffix;
        string actualSha256;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
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

        File.Move(partialPath, destinationPath, overwrite: true);
        return true;
    }

    private static void DeleteThroughHandle(SafeFileHandle handle, string path)
    {
        try
        {
            WindowsFileNative.MarkForDeletion(handle);
        }
        catch (Win32Exception ex)
        {
            throw new DataDirectoryMigrationBlockedException(
                $"Cannot delete '{path}' from the old data directory. Stop whatever holds it and run --migrate-data-directory again.",
                ex);
        }
    }

    private static void DeleteLink(string path)
    {
        var (handle, error) = WindowsFileNative.OpenForDelete(path);
        if (handle is null)
        {
            if (WindowsFileNative.IsNotFound(error)) return;

            throw new DataDirectoryMigrationBlockedException($"Cannot remove the link '{path}'.",
                new Win32Exception(error));
        }

        using (handle)
        {
            DeleteThroughHandle(handle, path);
        }
    }

    private static void DeleteEmptyDirectoryOrBlock(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DataDirectoryMigrationBlockedException(
                $"Cannot remove the old directory '{path}'; another process may hold it open. Stop it and run --migrate-data-directory again.",
                ex);
        }
    }

    private static void MoveDirectoryOrBlock(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DataDirectoryMigrationBlockedException(
                $"Cannot rename '{source}' to '{destination}'; another process may hold something in it open. Stop it and run --migrate-data-directory again.",
                ex);
        }
    }

    private static void DeletePartialCopies(string tree)
    {
        foreach (var partial in Directory.EnumerateFiles(tree, "*" + DataDirectoryMigrationRules.PartialCopySuffix,
                     SearchOption.AllDirectories))
            File.Delete(partial);
    }

    private DataDirectoryMigrationResult Result(DataDirectoryMigrationOutcome outcome)
    {
        return new DataDirectoryMigrationResult(outcome, Adopted: _adopted, Quarantined: _quarantined,
            Discarded: _discarded, QuarantinePath: _quarantined > 0 ? _quarantinePath : null);
    }
}
