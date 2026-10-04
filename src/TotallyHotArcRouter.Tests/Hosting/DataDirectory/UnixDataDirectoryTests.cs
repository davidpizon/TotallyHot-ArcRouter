using System.Diagnostics;
using System.Runtime.Versioning;
using TotallyHot.ArcRouter.Hosting.DataDirectory;

namespace TotallyHot.ArcRouter.Tests.Hosting.DataDirectory;

/// <summary>
/// Covers ADR-0024's Linux and macOS side against a real filesystem: <see cref="UnixNative"/>'s
/// non-following stat, <see cref="UnixDataDirectorySecurity"/>'s owner-only checks, the startup decision
/// (<see cref="DataDirectoryBootstrap.DecideUnix"/>), the container's in-place migration, and - when the
/// suite runs as root, as it does in the Linux container check - the full <see cref="UnixDataDirectoryMigration"/>.
/// Every test skips on Windows.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixDataDirectoryTests
{
    private const UnixFileMode OpenDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                                   UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                                   UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                                                   UnixFileMode.OtherExecute;

    [Fact]
    public void LStat_ReportsKindOwnerModeAndLinkCount_WithoutFollowingLinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var scratch = NewScratch();
        try
        {
            var file = Path.Combine(scratch, "file");
            File.WriteAllText(file, "x");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            var link = Path.Combine(scratch, "link");
            File.CreateSymbolicLink(link, file);
            Run("ln", file, Path.Combine(scratch, "hard"));

            var fileStatus = UnixNative.LStat(file)!.Value;
            Assert.Equal(UnixFileKind.RegularFile, fileStatus.Kind);
            Assert.Equal(UnixNative.EffectiveUserId(), fileStatus.Uid);
            Assert.Equal(Convert.ToUInt32("640", 8), fileStatus.Mode);
            Assert.Equal(2u, fileStatus.LinkCount);
            Assert.True(fileStatus.GrantsGroupOrOther);

            Assert.Equal(UnixFileKind.SymbolicLink, UnixNative.LStat(link)!.Value.Kind);
            Assert.Equal(UnixFileKind.Directory, UnixNative.LStat(scratch)!.Value.Kind);
            Assert.Null(UnixNative.LStat(Path.Combine(scratch, "absent")));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void CreateProtected_Is0700_AndInspectsAsProtected_WhileA0755DirectoryDoesNot()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var scratch = NewScratch();
        try
        {
            var euid = UnixNative.EffectiveUserId();
            var root = Path.Combine(scratch, "root");
            UnixDataDirectorySecurity.CreateProtected(root);

            Assert.Equal(UnixDataDirectorySecurity.OwnerOnlyDirectoryMode, File.GetUnixFileMode(root));
            Assert.Equal(DataDirectoryState.Protected, UnixDataDirectorySecurity.Inspect(root, [euid]).State);

            var open = Path.Combine(scratch, "open");
            Directory.CreateDirectory(open);
            File.SetUnixFileMode(open, OpenDirectoryMode);
            var inspection = UnixDataDirectorySecurity.Inspect(open, [euid]);
            Assert.Equal(DataDirectoryState.Unprotected, inspection.State);
            Assert.True(inspection.OwnerTrusted);

            Assert.False(UnixDataDirectorySecurity.Inspect(root, [euid + 1]).OwnerTrusted);

            // 0300 has no group or other bits but cannot be listed by its owner, so it is not protected.
            File.SetUnixFileMode(root, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Contains("read, write and search", UnixDataDirectorySecurity.Inspect(root, [euid]).Reason);
            File.SetUnixFileMode(root, UnixDataDirectorySecurity.OwnerOnlyDirectoryMode);

            var link = Path.Combine(scratch, "link");
            Directory.CreateSymbolicLink(link, root);
            Assert.Contains("symbolic link", UnixDataDirectorySecurity.Inspect(link, [euid]).Reason);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void DecideUnix_UsesAMigratedRoot_AndFailsClosedOnAnOpenRootItOwns()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var scratch = NewScratch();
        try
        {
            var euid = UnixNative.EffectiveUserId();
            var root = Path.Combine(scratch, "state");

            // Missing: created owner-only, with the marker, and used.
            var created = Decide(scratch, root, [euid], container: false);
            Assert.Equal(root, created.Directory);
            Assert.True(UnixDataDirectoryMigration.HasMarker(root));

            // 0700 alone is not enough: systemd's StateDirectoryMode sets that on every start.
            File.Delete(Path.Combine(root, DataDirectoryMigrationRules.ProtectedMarkerFileName));
            Assert.Throws<DataDirectoryNotProtectedException>(() => Decide(scratch, root, [euid], container: false));

            // And an open root this process owns is the unmigrated service case.
            File.SetUnixFileMode(root, OpenDirectoryMode);
            Assert.Throws<DataDirectoryNotProtectedException>(() => Decide(scratch, root, [euid], container: false));

            // Someone else's root is simply not ours to use.
            var fallback = Decide(scratch, root, [euid + 1], container: false);
            Assert.Equal(Path.Combine(scratch, "user"), fallback.Directory);
            Assert.True(fallback.MachineWideUnavailable);
            Assert.Equal(UnixDataDirectorySecurity.OwnerOnlyDirectoryMode, File.GetUnixFileMode(fallback.Directory) &
                                                                           OpenDirectoryMode);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void ContainerVolume_IsRewrittenInPlace_LinksQuarantined_AndEveryDirectoryMade0700()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var scratch = NewScratch();
        try
        {
            var euid = UnixNative.EffectiveUserId();
            var volume = Path.Combine(scratch, "data");
            Directory.CreateDirectory(Path.Combine(volume, "logs"));
            File.SetUnixFileMode(volume, OpenDirectoryMode);
            File.SetUnixFileMode(Path.Combine(volume, "logs"), OpenDirectoryMode);
            File.WriteAllText(Path.Combine(volume, "transcripts.db"), "rows");
            File.SetUnixFileMode(Path.Combine(volume, "transcripts.db"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            var outside = Path.Combine(scratch, "outside.txt");
            File.WriteAllText(outside, "outside");
            File.CreateSymbolicLink(Path.Combine(volume, "logs", "link.log"), outside);
            // Would collide with logs/link.log if quarantine flattened paths.
            File.CreateSymbolicLink(Path.Combine(volume, "logs_link.log"), outside);

            // An earlier reader's descriptor must not see the rewritten file.
            using var earlyReader = new FileStream(Path.Combine(volume, "transcripts.db"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            var inodeBefore = Inode(Path.Combine(volume, "transcripts.db"));

            var resolution = Decide(scratch, volume, [euid], container: true);

            Assert.Equal(volume, resolution.Directory);
            Assert.True(UnixDataDirectoryMigration.HasMarker(volume));
            Assert.Equal("rows", File.ReadAllText(Path.Combine(volume, "transcripts.db")));
            Assert.Equal(UnixDataDirectorySecurity.OwnerOnlyFileMode,
                File.GetUnixFileMode(Path.Combine(volume, "transcripts.db")));
            Assert.NotEqual(inodeBefore, Inode(Path.Combine(volume, "transcripts.db")));
            Assert.Equal(UnixDataDirectorySecurity.OwnerOnlyDirectoryMode, File.GetUnixFileMode(volume));
            Assert.Equal(UnixDataDirectorySecurity.OwnerOnlyDirectoryMode,
                File.GetUnixFileMode(Path.Combine(volume, "logs")));
            Assert.False(Path.Exists(Path.Combine(volume, "logs", "link.log")));
            var quarantined = Directory.EnumerateFileSystemEntries(
                    Path.Combine(volume, DataDirectoryMigrationRules.QuarantineDirectoryName), "*",
                    SearchOption.AllDirectories)
                .Where(entry => UnixNative.LStat(entry)?.Kind == UnixFileKind.SymbolicLink);
            Assert.Equal(2, quarantined.Count());
            Assert.Equal("outside", File.ReadAllText(outside));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Migration_AsRoot_LocksCopiesAndSwaps_LeavingRejectsInALockedQuarantine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        Assert.SkipUnless(UnixNative.EffectiveUserId() == 0, "lchown to another account needs root");
        var scratch = NewScratch();
        try
        {
            const uint serviceUid = 54321;
            const uint serviceGid = 54321;
            const uint legacyUid = 1000;
            var root = Path.Combine(scratch, "totallyhot-arcrouter");
            Directory.CreateDirectory(Path.Combine(root, "models", "llm_router"));
            File.SetUnixFileMode(root, OpenDirectoryMode);
            File.WriteAllText(Path.Combine(root, "transcripts.db"), "rows");
            File.WriteAllText(Path.Combine(root, "appsettings.local.json"), "{}");
            File.WriteAllText(Path.Combine(root, "models", "llm_router", "model.onnx"), "weights");
            File.WriteAllText(Path.Combine(root, "foreign.db"), "planted");
            UnixNative.LChown(Path.Combine(root, "foreign.db"), 4242, 4242);
            var outside = Path.Combine(scratch, "outside.txt");
            File.WriteAllText(outside, "outside");
            File.CreateSymbolicLink(Path.Combine(root, "link"), outside);
            UnixNative.LChown(root, serviceUid, serviceGid);

            var result = new UnixDataDirectoryMigration(root, serviceUid, serviceGid, legacyUid,
                new StubScanner([]), Serilog.Core.Logger.None).Run();

            Assert.Equal(DataDirectoryMigrationOutcome.Migrated, result.Outcome);
            var rootStatus = UnixNative.LStat(root)!.Value;
            Assert.Equal(serviceUid, rootStatus.Uid);
            Assert.Equal(Convert.ToUInt32("700", 8), rootStatus.Mode);
            Assert.True(UnixDataDirectoryMigration.HasMarker(root));

            var copied = UnixNative.LStat(Path.Combine(root, "transcripts.db"))!.Value;
            Assert.Equal(serviceUid, copied.Uid);
            Assert.Equal(Convert.ToUInt32("600", 8), copied.Mode);
            Assert.Equal("rows", File.ReadAllText(Path.Combine(root, "transcripts.db")));

            Assert.False(File.Exists(Path.Combine(root, "appsettings.local.json")));
            Assert.False(File.Exists(Path.Combine(root, "foreign.db")));
            Assert.False(Path.Exists(Path.Combine(root, "link")));
            Assert.False(File.Exists(Path.Combine(root, "models", "llm_router", "model.onnx")));
            Assert.Equal("outside", File.ReadAllText(outside));

            // The rejects stay in the old tree, now root-owned and 0700.
            Assert.NotNull(result.SetAsidePath);
            var quarantine = UnixNative.LStat(result.SetAsidePath)!.Value;
            Assert.Equal(0u, quarantine.Uid);
            Assert.Equal(Convert.ToUInt32("700", 8), quarantine.Mode);
            Assert.True(File.Exists(Path.Combine(result.SetAsidePath, "appsettings.local.json")));
            Assert.Equal(3, result.Quarantined);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Migration_AsRoot_WithAnOpenWriter_StopsAndLeavesTheTreeLocked()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        Assert.SkipUnless(UnixNative.EffectiveUserId() == 0, "lchown to another account needs root");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "totallyhot-arcrouter");
            Directory.CreateDirectory(root);
            File.SetUnixFileMode(root, OpenDirectoryMode);
            File.WriteAllText(Path.Combine(root, "transcripts.db"), "rows");
            UnixNative.LChown(root, 54321, 54321);

            var migration = new UnixDataDirectoryMigration(root, 54321, 54321, null,
                new StubScanner(["process 99 (arcrouter) has 'transcripts.db' open for writing"]),
                Serilog.Core.Logger.None);

            var ex = Assert.Throws<DataDirectoryMigrationBlockedException>(migration.Run);

            Assert.Contains("process 99", ex.Message);
            var status = UnixNative.LStat(root)!.Value;
            Assert.Equal(0u, status.Uid);
            Assert.Equal(Convert.ToUInt32("700", 8), status.Mode);
            Assert.Equal("rows", File.ReadAllText(Path.Combine(root, "transcripts.db")));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void LinuxScanner_FindsThisTestsOwnWriter_WhenRunFromAnotherProcessView()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        var scratch = NewScratch();
        try
        {
            var path = Path.Combine(scratch, "held.db");
            // A child process holds the file open for writing; the scanner skips only its own pid.
            using var holder = Process.Start(new ProcessStartInfo("sh", ["-c", $"exec 3>>'{path}'; sleep 5"])
            {
                UseShellExecute = false
            })!;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(3);
                IReadOnlyList<string> writers = [];
                while (DateTime.UtcNow < deadline && writers.Count == 0)
                {
                    if (OperatingSystem.IsLinux()) writers = new LinuxOpenWriterScanner().FindWriters(scratch);
                    if (writers.Count == 0) Thread.Sleep(50);
                }

                Assert.Contains(writers, writer => writer.Contains("held.db") && writer.Contains("open for writing"));
            }
            finally
            {
                holder.Kill();
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// Another account's process holding a file in the tree must block migration: either it is seen
    /// writing, or - where this root lacks <c>CAP_SYS_PTRACE</c>, as in a rootless container - it cannot be
    /// inspected and is reported anyway. The first version skipped uninspectable processes and so failed
    /// open; the Linux container check caught it.
    /// </summary>
    [Fact]
    public void LinuxScanner_AsRoot_ReportsAnotherAccountsHolder_EvenWhenItCannotInspectIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        Assert.SkipUnless(UnixNative.EffectiveUserId() == 0, "needs root to start a process as another account");
        var scratch = NewScratch();
        try
        {
            var path = Path.Combine(scratch, "held.db");
            File.WriteAllText(path, "rows");
            UnixNative.LChown(path, 65534, 65534);
            UnixNative.LChown(scratch, 65534, 65534);
            using var holder = Process.Start(new ProcessStartInfo("setpriv",
                ["--reuid=65534", "--regid=65534", "--clear-groups", "sh", "-c", $"exec 3>>'{path}'; sleep 5"])
            {
                UseShellExecute = false
            })!;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(3);
                IReadOnlyList<string> writers = [];
                while (DateTime.UtcNow < deadline && writers.Count == 0)
                {
                    if (OperatingSystem.IsLinux()) writers = new LinuxOpenWriterScanner().FindWriters(scratch);
                    if (writers.Count == 0) Thread.Sleep(50);
                }

                Assert.Contains(writers, writer => writer.Contains("held.db") || writer.Contains("could not be inspected"));
            }
            finally
            {
                holder.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The container migration applies the shared model rules: an embedding file that does not match its
    /// pinned hash, and any other model file, are dropped for the router to download again. The operator's
    /// overlay is kept, since only the container's own account could have written it.
    /// </summary>
    [Fact]
    public void ContainerVolume_AppliesTheModelRules_AndKeepsTheOverlay()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var scratch = NewScratch();
        try
        {
            var volume = Path.Combine(scratch, "data");
            Directory.CreateDirectory(Path.Combine(volume, "models", "bge-large-en-v1.5"));
            Directory.CreateDirectory(Path.Combine(volume, "models", "llm_router"));
            File.WriteAllText(Path.Combine(volume, "models", "bge-large-en-v1.5", "tokenizer.json"), "planted");
            File.WriteAllText(Path.Combine(volume, "models", "llm_router", "model.onnx"), "weights");
            File.WriteAllText(Path.Combine(volume, "appsettings.local.json"), "{}");
            File.SetUnixFileMode(volume, OpenDirectoryMode);

            Decide(scratch, volume, [UnixNative.EffectiveUserId()], container: true);

            Assert.False(File.Exists(Path.Combine(volume, "models", "bge-large-en-v1.5", "tokenizer.json")));
            Assert.False(File.Exists(Path.Combine(volume, "models", "llm_router", "model.onnx")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(volume, "appsettings.local.json")));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// A root-owned directory - what a blocked migration leaves behind - makes the service account refuse to
    /// start rather than fall back to split state, while any other account still falls back.
    /// </summary>
    [Fact]
    public void DecideUnix_RootOwnedRoot_RefusesTheServiceAccount_ButNotOthers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        Assert.SkipWhen(UnixNative.EffectiveUserId() == 0, "needs a non-root account looking at a root-owned directory");
        var scratch = NewScratch();
        try
        {
            // /usr stands in for the locked tree: a root-owned directory every system has.
            const string rootOwned = "/usr";
            var euid = UnixNative.EffectiveUserId();

            Assert.Throws<DataDirectoryNotProtectedException>(() => DataDirectoryBootstrap.DecideUnix(rootOwned,
                Path.Combine(scratch, "user"), Path.Combine(scratch, "last"), [euid], container: false,
                serviceAccountUid: euid));

            var other = DataDirectoryBootstrap.DecideUnix(rootOwned, Path.Combine(scratch, "user"),
                Path.Combine(scratch, "last"), [euid], container: false, serviceAccountUid: euid + 1);
            Assert.Equal(Path.Combine(scratch, "user"), other.Directory);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// A symbolic link holding the reserved <c>quarantine</c> name must not route the quarantine outside the
    /// volume: it is parked and moved into a real quarantine folder, and its target is untouched.
    /// </summary>
    [Fact]
    public void ContainerVolume_QuarantineNameHeldByASymlink_IsNotFollowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var scratch = NewScratch();
        try
        {
            var volume = Path.Combine(scratch, "data");
            var outside = Directory.CreateDirectory(Path.Combine(scratch, "outside")).FullName;
            Directory.CreateDirectory(volume);
            Directory.CreateSymbolicLink(Path.Combine(volume, DataDirectoryMigrationRules.QuarantineDirectoryName), outside);
            File.CreateSymbolicLink(Path.Combine(volume, "stray-link"), "/etc/hostname");
            File.SetUnixFileMode(volume, OpenDirectoryMode);

            Decide(scratch, volume, [UnixNative.EffectiveUserId()], container: true);

            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            var quarantine = Path.Combine(volume, DataDirectoryMigrationRules.QuarantineDirectoryName);
            Assert.Equal(UnixFileKind.Directory, UnixNative.LStat(quarantine)!.Value.Kind);
            Assert.Equal(2, Directory.EnumerateFileSystemEntries(quarantine, "*", SearchOption.AllDirectories)
                .Count(entry => UnixNative.LStat(entry)?.Kind == UnixFileKind.SymbolicLink));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>A logs directory created at startup carries the marker, so the next start accepts it.</summary>
    [Fact]
    public void VerifyUnixLogsDirectory_CreatesItMarked_SoTheNextStartAcceptsIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var scratch = NewScratch();
        try
        {
            var logs = Path.Combine(scratch, "logs");

            DataDirectoryBootstrap.VerifyUnixLogsDirectory(logs);
            DataDirectoryBootstrap.VerifyUnixLogsDirectory(logs);

            Assert.True(UnixDataDirectoryMigration.HasMarker(logs));
            Assert.Equal(UnixDataDirectorySecurity.OwnerOnlyDirectoryMode, File.GetUnixFileMode(logs));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// A root-run command that created a secret in the service's tree hands it back to the service account,
    /// without following a link out of the tree.
    /// </summary>
    [Fact]
    public void RestoreServiceOwnership_AsRoot_HandsRootCreatedEntriesToTheTreeOwner()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        Assert.SkipUnless(UnixNative.EffectiveUserId() == 0, "lchown to another account needs root");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "state");
            Directory.CreateDirectory(Path.Combine(root, "keys"));
            File.WriteAllText(Path.Combine(root, "keys", "key.xml"), "k");
            File.WriteAllText(Path.Combine(root, "secrets.dat"), "s");
            var outside = Path.Combine(scratch, "outside.txt");
            File.WriteAllText(outside, "o");
            File.CreateSymbolicLink(Path.Combine(root, "link"), outside);
            UnixNative.LChown(root, 54321, 54321);

            DataDirectoryBootstrap.RestoreServiceOwnership(root);

            Assert.Equal(54321u, UnixNative.LStat(Path.Combine(root, "secrets.dat"))!.Value.Uid);
            Assert.Equal(54321u, UnixNative.LStat(Path.Combine(root, "keys"))!.Value.Uid);
            Assert.Equal(54321u, UnixNative.LStat(Path.Combine(root, "keys", "key.xml"))!.Value.Uid);
            Assert.Equal(0u, UnixNative.LStat(outside)!.Value.Uid);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static DataDirectoryResolution Decide(string scratch, string root, uint[] trustedOwners, bool container)
    {
        return DataDirectoryBootstrap.DecideUnix(machineWide: root, perUser: Path.Combine(scratch, "user"),
            lastResort: Path.Combine(scratch, "last"), trustedOwners: trustedOwners, container: container);
    }

    private static string NewScratch()
    {
        var path = Path.Combine(TestScratchDirectory.RunRoot, "unix-datadir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Inode(string path)
    {
        return (OperatingSystem.IsMacOS() ? Run("stat", "-f", "%i", path) : Run("stat", "-c", "%i", path)).Trim();
    }

    private static string Run(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private sealed class StubScanner(IReadOnlyList<string> writers) : IOpenWriterScanner
    {
        public IReadOnlyList<string> FindWriters(string root)
        {
            return writers;
        }
    }
}
