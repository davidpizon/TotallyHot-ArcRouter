using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Startup probe that verifies <c>secrets.dat</c> is protected the way ADR-0020 requires before passkey
/// enrollment or gated operations proceed. Fails closed on any I/O or ACL parse error so a misconfigured
/// store cannot silently weaken the gate.
/// </summary>
public sealed class SecretStoreAclProbe
{
    /// <summary>
    /// Returns whether <paramref name="secretsPath"/> exists and its ACL or Unix mode matches the
    /// machine-shared secret-store policy.
    /// </summary>
    /// <param name="secretsPath">Absolute path to <c>secrets.dat</c>.</param>
    public bool Check(string secretsPath)
    {
        if (string.IsNullOrWhiteSpace(secretsPath)) return false;

        try
        {
            if (!File.Exists(secretsPath)) return true;

            if ((File.GetAttributes(secretsPath) & FileAttributes.ReparsePoint) != 0) return false;

            return OperatingSystem.IsWindows()
                ? CheckWindows(secretsPath)
                : CheckUnix(secretsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool CheckWindows(string secretsPath)
    {
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var rules = new FileInfo(secretsPath).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        if (rules.Count == 0) return false;

        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid) return false;
            if (!sid.Equals(localSystem) && !sid.Equals(administrators)) return false;
        }

        return rules.Any(r => r.IdentityReference.Equals(localSystem)) &&
               rules.Any(r => r.IdentityReference.Equals(administrators));
    }

    private static bool CheckUnix(string secretsPath)
    {
        var mode = File.GetUnixFileMode(secretsPath);
        if (mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) return false;

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return true;

        var stat = StatUnix(secretsPath);
        if (stat is null) return false;

        var uid = GetEffectiveUserId();
        return stat.Value.Uid == uid || stat.Value.Uid == 0;
    }

    private static int GetEffectiveUserId()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var uid = InteropUnix.GetEffectiveUserId();
            if (uid >= 0) return uid;
        }

        return Environment.UserName == "root" ? 0 : -1;
    }

    private static UnixFileStat? StatUnix(string path)
    {
        if (InteropUnix.TryStat(path, out var stat)) return stat;
        return null;
    }

    private readonly record struct UnixFileStat(uint Uid, uint Gid);

    private static class InteropUnix
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "geteuid")]
        public static extern int GetEffectiveUserId();

        [StructLayout(LayoutKind.Sequential)]
        public struct StatStruct
        {
            public uint st_dev;
            public uint st_ino;
            public uint st_mode;
            public uint st_nlink;
            public uint st_uid;
            public uint st_gid;
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
        private static extern int statNative(string path, out StatStruct buf);

        public static bool TryStat(string path, out UnixFileStat stat)
        {
            if (statNative(path, out var raw) != 0)
            {
                stat = default;
                return false;
            }

            stat = new UnixFileStat(raw.st_uid, raw.st_gid);
            return true;
        }
    }
}
