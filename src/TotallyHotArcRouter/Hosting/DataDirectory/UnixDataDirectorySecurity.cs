using System.Globalization;
using System.Runtime.Versioning;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// Creates and verifies owner-only data directories on Linux and macOS - ADR-0024's equivalent of the
/// Windows protected DACL. A directory passes when it is a real directory (not a symbolic link), is owned
/// by a trusted account, and grants no group or other permission bits.
/// </summary>
/// <remarks>
/// Only directories are checked, not every file in them. A path lookup needs search permission on every
/// directory along the way, so a <c>0700</c> directory keeps every other account away from everything
/// beneath it, whatever mode an individual file was created with.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static class UnixDataDirectorySecurity
{
    /// <summary>The mode a protected directory gets: read, write and search for its owner only.</summary>
    public const UnixFileMode OwnerOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The mode a protected file gets: read and write for its owner only.</summary>
    public const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Inspects <paramref name="path"/> without following it if it is a symbolic link. A protected
    /// directory has exactly the owner's read, write and search bits among its permission bits.
    /// </summary>
    /// <param name="path">The directory to inspect.</param>
    /// <param name="trustedOwners">The uids allowed to own the directory.</param>
    public static DataDirectoryInspection Inspect(string path, IReadOnlyCollection<uint> trustedOwners)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(trustedOwners);

        UnixFileStatus? status;
        try
        {
            status = UnixNative.LStat(path);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DataDirectoryInspection.Inaccessible;
        }

        if (status is not { } found) return DataDirectoryInspection.Missing;

        var owner = $"uid {found.Uid.ToString(CultureInfo.InvariantCulture)}";
        var ownerTrusted = trustedOwners.Contains(found.Uid);

        if (found.Kind == UnixFileKind.SymbolicLink)
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, owner, ownerTrusted,
                "it is a symbolic link");

        if (found.Kind != UnixFileKind.Directory)
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, owner, ownerTrusted,
                "it is not a directory");

        if (!ownerTrusted)
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, owner, false,
                $"it is owned by {owner}, which can change its mode");

        if (found.GrantsGroupOrOther)
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, owner, true,
                $"its mode {Convert.ToString(found.Mode, 8)} grants group or other access");

        // The owner needs all three bits: 0300, say, passes the write probe but cannot list the directory.
        if ((found.Mode & 0x1C0) != 0x1C0)
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, owner, true,
                $"its mode {Convert.ToString(found.Mode, 8)} does not give its owner read, write and search access");

        return new DataDirectoryInspection(DataDirectoryState.Protected, owner, true);
    }

    /// <summary>
    /// Creates <paramref name="path"/> with mode <c>0700</c> in a single <c>mkdir</c>, so it never exists
    /// with a wider mode. The process umask can only remove bits, never add group or other ones; the mode is
    /// then set explicitly too, so a restrictive umask cannot leave the owner without access.
    /// </summary>
    /// <exception cref="IOException">Something already exists at <paramref name="path"/>.</exception>
    public static void CreateProtected(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Path.Exists(path) || UnixNative.LStat(path) is not null)
            throw new IOException($"Cannot create a protected data directory at '{path}': something already exists there.");

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

        Directory.CreateDirectory(path: path, unixCreateMode: OwnerOnlyDirectoryMode);
        // Set explicitly as well: mkdir's mode is filtered by the umask, and a restrictive one (077 is fine,
        // 777 is not) would leave a 0000 directory that root can still fill but the service cannot read.
        File.SetUnixFileMode(path, OwnerOnlyDirectoryMode);
    }
}
