using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// Handle-based file operations the Windows migration needs and .NET does not expose: opening an entry
/// without following a reparse point, exclusively, with delete access; reading its link count and owner
/// through that handle; and deleting through it. Working through one handle from check to delete means
/// nothing can swap the entry in between (plan §3.1, "No open handles").
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileNative
{
    private const uint Delete = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;
    private const uint FileDispositionFlagDelete = 0x1;
    private const uint FileDispositionFlagIgnoreReadOnlyAttribute = 0x10;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotSupported = 50;
    private const int SeFileObject = 1;
    private const int OwnerSecurityInformation = 0x1;

    /// <summary>The Win32 error a sharing or lock conflict reports.</summary>
    public const int ErrorSharingViolation = 32;

    /// <summary>The Win32 error a lock conflict reports.</summary>
    public const int ErrorLockViolation = 33;

    /// <summary>The Win32 errors for an entry that no longer exists.</summary>
    public static bool IsNotFound(int error)
    {
        return error is 2 or 3;
    }

    /// <summary>
    /// Opens the regular file at <paramref name="path"/> for reading, with delete access, sharing nothing,
    /// and without following a reparse point. Returns <see langword="null"/> and the Win32 error code on
    /// failure - a sharing violation means another process holds the file open.
    /// </summary>
    public static (SafeFileHandle? Handle, int Error) OpenExclusive(string path)
    {
        var handle = CreateFileW(path, GenericRead | Delete | ReadControl, 0, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return (handle, 0);

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return (null, error);
    }

    /// <summary>
    /// Opens the entry at <paramref name="path"/> - a file, a directory, or a link itself - with delete
    /// access only, without following a reparse point. Used to remove links and hard-link names without
    /// touching what they point at.
    /// </summary>
    public static (SafeFileHandle? Handle, int Error) OpenForDelete(string path)
    {
        var handle = CreateFileW(path, Delete | FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return (handle, 0);

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return (null, error);
    }

    /// <summary>
    /// Opens the directory at <paramref name="path"/> without following a reparse point, sharing read and
    /// write but not delete. While the handle is open, nobody can rename, delete or replace the directory -
    /// so paths built through it keep naming what the walk already checked, rather than a junction swapped
    /// in after the check.
    /// </summary>
    public static (SafeFileHandle? Handle, int Error) OpenDirectoryPinned(string path)
    {
        var handle = CreateFileW(path, FileReadAttributes, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return (handle, 0);

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return (null, error);
    }

    /// <summary>Reads the open entry's attributes and hard-link count.</summary>
    /// <exception cref="Win32Exception">The call failed.</exception>
    public static (FileAttributes Attributes, uint LinkCount) GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        return ((FileAttributes)information.FileAttributes, information.NumberOfLinks);
    }

    /// <summary>Reads the open entry's owner through its handle.</summary>
    /// <exception cref="Win32Exception">The call failed.</exception>
    public static SecurityIdentifier GetOwner(SafeFileHandle handle)
    {
        var result = GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation, out var owner, IntPtr.Zero,
            IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (result != 0) throw new Win32Exception(result);

        try
        {
            return new SecurityIdentifier(owner);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    /// Marks the open entry for deletion; it is removed when its last handle closes. For a link this
    /// removes the link, and for one name of a hard-linked file it removes only that name. A read-only
    /// file is deleted too (<c>FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE</c>, Windows 10 1809 and
    /// later); on older systems that flag is unavailable and a read-only file fails.
    /// </summary>
    /// <exception cref="Win32Exception">The call failed.</exception>
    public static void MarkForDeletion(SafeFileHandle handle)
    {
        var flags = FileDispositionFlagDelete | FileDispositionFlagIgnoreReadOnlyAttribute;
        if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref flags, sizeof(uint))) return;

        var error = Marshal.GetLastPInvokeError();
        if (error is not (ErrorInvalidParameter or ErrorNotSupported)) throw new Win32Exception(error);

        byte deleteFile = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfoClass, ref deleteFile, 1))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref byte information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref uint information, uint bufferSize);

    [DllImport("advapi32.dll")]
    private static extern int GetSecurityInfo(SafeFileHandle handle, int objectType, int securityInformation,
        out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
