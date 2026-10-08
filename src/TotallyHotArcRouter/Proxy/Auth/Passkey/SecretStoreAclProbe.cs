using System.Buffers.Binary;
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
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
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
            var uid = InteropUnix.Geteuid();
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
        public static extern int Geteuid();

        // libc writes the platform's whole `struct stat` (144 bytes on x86-64 Linux), which is larger and laid
        // out differently from any small hand-written struct: a short managed struct is overrun and corrupts
        // the heap. So the call gets a generously oversized byte buffer and only the owner's uid is read.
        private const int StatBufferSize = 512;

        [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
        private static extern int statNative(string path, byte[] buf);

        public static bool TryStat(string path, out UnixFileStat stat)
        {
            stat = default;
            if (!TryGetOwnerOffsets(out var uidOffset, out var gidOffset)) return false;

            var buffer = new byte[StatBufferSize];
            try
            {
                if (statNative(path, buffer) != 0) return false;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                // Fail closed on a libc that does not export `stat` (glibc before 2.33).
                return false;
            }

            stat = new UnixFileStat(
                Uid: BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(uidOffset)),
                Gid: BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(gidOffset)));
            return true;
        }

        /// <summary>
        /// The byte offsets of <c>st_uid</c> and <c>st_gid</c> in <c>struct stat</c> for the supported
        /// platforms; unsupported combinations return <see langword="false"/> so the probe fails closed.
        /// </summary>
        private static bool TryGetOwnerOffsets(out int uidOffset, out int gidOffset)
        {
            (uidOffset, gidOffset) = (OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture) switch
            {
                (true, _, Architecture.X64 or Architecture.Arm64) => (16, 20),
                (_, true, Architecture.X64) => (28, 32),
                (_, true, Architecture.Arm64) => (24, 28),
                _ => (-1, -1),
            };
            return uidOffset >= 0;
        }
    }
}
