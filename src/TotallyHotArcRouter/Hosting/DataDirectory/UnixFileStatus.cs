using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>The kind of a filesystem entry, as a non-following <c>lstat</c> reports it.</summary>
public enum UnixFileKind
{
    /// <summary>A regular file.</summary>
    RegularFile,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link (the link itself, never its target).</summary>
    SymbolicLink,

    /// <summary>Anything else: a socket, FIFO, or device node.</summary>
    Other
}

/// <summary>
/// The parts of a Unix <c>stat</c> result the data-directory checks need, read without following links.
/// .NET exposes the mode bits (<see cref="File.GetUnixFileMode(string)"/>) but not the owner or the hard-link
/// count, and ADR-0024's rules need both: the owner decides whether a directory is trusted, and a file
/// with more than one link shares its inode with something outside the tree.
/// </summary>
/// <param name="Kind">What the entry is.</param>
/// <param name="Mode">The permission bits (<c>st_mode &amp; 07777</c>).</param>
/// <param name="Uid">The owning user id.</param>
/// <param name="Gid">The owning group id.</param>
/// <param name="LinkCount">The number of hard links to the inode.</param>
/// <param name="Device">The device the entry lives on.</param>
/// <param name="MountId">
/// The kernel's mount id (Linux 5.8+ <c>statx</c>), or <see langword="null"/> where unavailable. A bind mount
/// of a directory on the same filesystem keeps its <paramref name="Device"/>, so only this tells it apart.
/// </param>
public readonly record struct UnixFileStatus(UnixFileKind Kind, uint Mode, uint Uid, uint Gid, uint LinkCount,
    ulong Device = 0, ulong? MountId = null)
{
    /// <summary>Whether any group or other permission bit is set - the bits ADR-0024 requires to be clear.</summary>
    public bool GrantsGroupOrOther => (Mode & 0x3F) != 0;

    /// <summary>
    /// Whether this entry lives on the same mount as <paramref name="parent"/>. A directory that does not is a
    /// mount point: walking into it would leave the data tree.
    /// </summary>
    public bool IsOnSameMountAs(UnixFileStatus parent)
    {
        if (Device != parent.Device) return false;
        return MountId is null || parent.MountId is null || MountId == parent.MountId;
    }
}

/// <summary>
/// Thin P/Invoke wrappers over the libc calls the data-directory checks need and .NET does not expose:
/// a non-following stat, the effective uid, and a non-following chown.
/// </summary>
/// <remarks>
/// The stat structure's layout differs by platform and architecture, so this reads it from a byte buffer
/// at fixed offsets rather than declaring a struct:
/// <list type="bullet">
/// <item><description>
/// Linux uses <c>statx</c> (glibc 2.28+, musl 1.2.5+), whose <c>struct statx</c> has one layout on every
/// architecture - unlike <c>struct stat</c>, which differs between x64 and arm64.
/// </description></item>
/// <item><description>
/// macOS uses the 64-bit-inode <c>lstat</c>, exported as <c>lstat$INODE64</c> on x64 and as plain
/// <c>lstat</c> on arm64, where it is the only variant.
/// </description></item>
/// </list>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static class UnixNative
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7FF;
    private const uint StatxMountId = 0x1000;
    private const int Enoent = 2;
    private const int Enotdir = 20;

    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint SymbolicLinkType = 0xA000;

    /// <summary>
    /// Returns <paramref name="path"/>'s status without following a final symbolic link, or
    /// <see langword="null"/> when nothing exists at the path.
    /// </summary>
    /// <exception cref="Win32Exception">The call failed for a reason other than a missing path.</exception>
    public static UnixFileStatus? LStat(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsMacOS()) return LStatMac(path);

        var buffer = new byte[256];
        if (statx(dirfd: AtFdCwd, path: path, flags: AtSymlinkNoFollow, mask: StatxBasicStats | StatxMountId,
                buffer: buffer) != 0)
            return MissingOrThrow(path);

        // struct statx: stx_mask @0, stx_nlink @16, stx_uid @20, stx_gid @24, stx_mode (u16) @28,
        // stx_dev_major @136, stx_dev_minor @140, stx_mnt_id (u64) @144 when stx_mask has STATX_MNT_ID.
        var returnedMask = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var linkCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16));
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20));
        var gid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24));
        var mode = (uint)BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28));
        var device = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(136)) << 32) |
                     BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(140));
        ulong? mountId = (returnedMask & StatxMountId) != 0
            ? BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(144))
            : null;
        return Create(mode: mode, uid: uid, gid: gid, linkCount: linkCount, device: device, mountId: mountId);
    }

    /// <summary>Returns this process's effective user id.</summary>
    public static uint EffectiveUserId()
    {
        return geteuid();
    }

    /// <summary>
    /// Changes the owner of <paramref name="path"/> without following a final symbolic link - so a link
    /// planted where a directory was expected changes only the link, never its target.
    /// </summary>
    /// <exception cref="Win32Exception">The call failed.</exception>
    public static void LChown(string path, uint uid, uint gid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (lchown(path: path, owner: uid, group: gid) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"lchown('{path}') failed.");
    }

    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="destination"/> with <c>rename(2)</c>, whatever
    /// either is - a directory, a file, or a symbolic link, which is renamed itself rather than followed.
    /// </summary>
    /// <exception cref="Win32Exception">The call failed.</exception>
    public static void Rename(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (rename(oldPath: source, newPath: destination) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"rename('{source}', '{destination}') failed.");
    }

    /// <summary>
    /// Resolves a local account's uid and primary gid with <c>id</c>, or <see langword="null"/> when the
    /// account does not exist. Used for the service account, which only the elevated migration command and
    /// root-run CLI flags need to know - the service itself only ever compares against its own uid.
    /// </summary>
    public static (uint Uid, uint Gid)? TryResolveAccount(string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        var uid = RunId("-u", accountName);
        var gid = RunId("-g", accountName);
        return uid is null || gid is null ? null : (uid.Value, gid.Value);
    }

    private static uint? RunId(string option, string accountName)
    {
        try
        {
            var startInfo = new ProcessStartInfo("id")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(option);
            startInfo.ArgumentList.Add(accountName);

            using var process = Process.Start(startInfo);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && uint.TryParse(output, out var value) ? value : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static UnixFileStatus? LStatMac(string path)
    {
        var buffer = new byte[256];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? lstat_inode64(path: path, buffer: buffer)
            : lstat_arm64(path: path, buffer: buffer);
        if (result != 0) return MissingOrThrow(path);

        // 64-bit-inode struct stat: st_dev (i32) @0, st_mode (u16) @4, st_nlink (u16) @6, st_ino (u64) @8,
        // st_uid @16, st_gid @20.
        var mode = (uint)BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4));
        var linkCount = (uint)BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6));
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16));
        var gid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20));
        var device = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return Create(mode: mode, uid: uid, gid: gid, linkCount: linkCount, device: device, mountId: null);
    }

    private static UnixFileStatus? MissingOrThrow(string path)
    {
        var errno = Marshal.GetLastPInvokeError();
        if (errno is Enoent or Enotdir) return null;

        throw new Win32Exception(errno, $"stat('{path}') failed.");
    }

    private static UnixFileStatus Create(uint mode, uint uid, uint gid, uint linkCount, ulong device, ulong? mountId)
    {
        var kind = (mode & FileTypeMask) switch
        {
            DirectoryType => UnixFileKind.Directory,
            RegularFileType => UnixFileKind.RegularFile,
            SymbolicLinkType => UnixFileKind.SymbolicLink,
            _ => UnixFileKind.Other
        };

        return new UnixFileStatus(Kind: kind, Mode: mode & 0xFFF, Uid: uid, Gid: gid, LinkCount: linkCount,
            Device: device, MountId: mountId);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_inode64(string path, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat_arm64(string path, [Out] byte[] buffer);

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int rename(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown(string path, uint owner, uint group);
}
