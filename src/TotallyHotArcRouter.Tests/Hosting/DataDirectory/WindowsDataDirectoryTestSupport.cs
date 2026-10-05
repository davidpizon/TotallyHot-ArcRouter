using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TotallyHot.ArcRouter.Hosting.DataDirectory;

namespace TotallyHot.ArcRouter.Tests.Hosting.DataDirectory;

/// <summary>
/// Shared helpers for the Windows data-directory tests. They run the real ACL code unelevated by using a
/// policy that adds the current account to <c>SYSTEM</c> and <c>Administrators</c>: an unelevated process
/// can neither make <c>Administrators</c> an owner nor open a directory granted only to those two.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDataDirectoryTestSupport
{
    /// <summary>The current account's SID.</summary>
    public static SecurityIdentifier CurrentUser { get; } = WindowsIdentity.GetCurrent().User!;

    /// <summary>The production rules plus the current account, so the test process keeps access.</summary>
    public static WindowsDirectoryPolicy TestPolicy { get; } = new(Owner: CurrentUser,
        FullControl: [WindowsDirectoryPolicy.LocalSystem, WindowsDirectoryPolicy.Administrators, CurrentUser]);

    /// <summary>A fresh scratch directory for one test, under this run's root.</summary>
    public static string NewScratch()
    {
        var path = Path.Combine(TestScratchDirectory.RunRoot, "datadir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes a scratch tree, first giving the current account back control of anything restricted in it.</summary>
    public static void DeleteScratch(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            TryGrantCurrentUser(entry);

        // Links go first, one at a time: Directory.Delete's recursive walk fails with "access denied" on a
        // junction (reproduced 2026-10-03), while a non-recursive delete of the junction itself works.
        foreach (var link in new DirectoryInfo(path)
                     .EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                     .Where(entry => entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                     .OrderByDescending(entry => entry.FullName.Length)
                     .ToList())
            if (link is DirectoryInfo directory)
                directory.Delete(recursive: false);
            else
                link.Delete();

        Directory.Delete(path, recursive: true);
    }

    /// <summary>Creates a directory junction at <paramref name="link"/> pointing at <paramref name="target"/>; needs no privilege.</summary>
    public static void CreateJunction(string link, string target)
    {
        RunCmd($"mklink /J \"{link}\" \"{target}\"");
    }

    /// <summary>Creates a hard link at <paramref name="link"/> to the existing file <paramref name="target"/>.</summary>
    public static void CreateHardLink(string link, string target)
    {
        RunCmd($"mklink /H \"{link}\" \"{target}\"");
    }

    /// <summary>The access rules on <paramref name="path"/>, as SIDs.</summary>
    public static IReadOnlyList<FileSystemAccessRule> RulesOf(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();

        return
        [
            .. security.GetAccessRules(includeExplicit: true, includeInherited: true,
                    targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
        ];
    }

    private static void TryGrantCurrentUser(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return;

            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectorySecurity()
                : new FileSecurity();
            security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl,
                AccessControlType.Allow));

            if (security is DirectorySecurity directorySecurity)
                new DirectoryInfo(path).SetAccessControl(directorySecurity);
            else
                new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; Directory.Delete reports anything that is still stuck.
        }
    }

    private static void RunCmd(string arguments)
    {
        // Arguments, not ArgumentList: cmd parses its own command line, and ArgumentList's escaping of the
        // embedded quotes would garble it.
        var startInfo = new ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"cmd /c {arguments} failed: {error}");
    }
}
