using System.Runtime.Versioning;
using System.Security.Principal;
using TotallyHot.ArcRouter.Hosting.DataDirectory;
using static TotallyHot.ArcRouter.Tests.Hosting.DataDirectory.WindowsDataDirectoryTestSupport;

namespace TotallyHot.ArcRouter.Tests.Hosting.DataDirectory;

/// <summary>
/// Covers <see cref="WindowsDataDirectoryMigration"/> (ADR-0024 rule 3; plan §3.1) on real directories:
/// the per-file rules, the link and hard-link handling that keeps a SYSTEM-run migration from touching
/// anything outside the tree, fail-closed on an open file, and crash recovery. The root's legacy owner
/// is the test account, standing in for the installing user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDataDirectoryMigrationTests
{
    [Fact]
    public void LegacyRoot_IsReplacedByAProtectedTree_WithTheRulesApplied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(scratch, "TotallyHotArcRouter")).FullName;
            Write(root, "transcripts.db", "rows");
            Write(root, @"logs\arcrouter-20261003.log", "log line");
            Write(root, "appsettings.local.json", "{ \"planted\": true }");
            Write(root, @"models\llm_router\model.onnx", "weights");
            Write(root, @"models\bge-large-en-v1.5\tokenizer.json", "not the pinned tokenizer");

            var result = Migrate(root);

            Assert.Equal(DataDirectoryMigrationOutcome.Migrated, result.Outcome);
            Assert.Equal(DataDirectoryState.Protected, WindowsDataDirectorySecurity.Inspect(root, TestPolicy).State);
            Assert.Equal("rows", File.ReadAllText(Path.Combine(root, "transcripts.db")));
            Assert.Equal("log line", File.ReadAllText(Path.Combine(root, @"logs\arcrouter-20261003.log")));

            // The overlay is never adopted: it is kept in the quarantine for an administrator to review.
            Assert.False(File.Exists(Path.Combine(root, "appsettings.local.json")));
            Assert.NotNull(result.QuarantinePath);
            Assert.StartsWith(root, result.QuarantinePath);
            Assert.Equal("{ \"planted\": true }",
                File.ReadAllText(Path.Combine(result.QuarantinePath, "appsettings.local.json")));

            // Model files are re-downloadable, so unverifiable ones are dropped rather than trusted.
            Assert.False(File.Exists(Path.Combine(root, @"models\llm_router\model.onnx")));
            Assert.False(File.Exists(Path.Combine(root, @"models\bge-large-en-v1.5\tokenizer.json")));

            Assert.Equal(2, result.Adopted);
            Assert.Equal(1, result.Quarantined);
            Assert.Equal(2, result.Discarded);

            // Every file inherits the protected DACL, and no staging or retired tree is left beside the root.
            Assert.All(RulesOf(Path.Combine(root, "transcripts.db")),
                rule => Assert.Contains(rule.IdentityReference, TestPolicy.FullControl));
            Assert.Empty(DataDirectoryMigrationRules.FindStaging(root));
            Assert.Empty(DataDirectoryMigrationRules.FindRetired(root));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void Junction_InTheOldTree_IsRemoved_WithoutTouchingItsTarget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var outside = Directory.CreateDirectory(Path.Combine(scratch, "someone-elses-files")).FullName;
            Write(outside, "precious.txt", "keep me");
            var root = Directory.CreateDirectory(Path.Combine(scratch, "TotallyHotArcRouter")).FullName;
            CreateJunction(Path.Combine(root, "logs"), outside);

            Migrate(root);

            Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
            Assert.False(Path.Exists(Path.Combine(root, "logs", "precious.txt")));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void HardLink_InTheOldTree_LosesOnlyItsName_AndIsNotAdopted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var outsideFile = Path.Combine(scratch, "outside.txt");
            File.WriteAllText(outsideFile, "outside content");
            var root = Directory.CreateDirectory(Path.Combine(scratch, "TotallyHotArcRouter")).FullName;
            CreateHardLink(Path.Combine(root, "linked.db"), outsideFile);

            var result = Migrate(root);

            Assert.Equal("outside content", File.ReadAllText(outsideFile));
            Assert.False(File.Exists(Path.Combine(root, "linked.db")));
            Assert.Equal(0, result.Adopted);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void ReadOnlyFile_IsAdopted_AndItsOriginalRemoved()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(scratch, "TotallyHotArcRouter")).FullName;
            Write(root, "cluster_model.json", "{}");
            File.SetAttributes(Path.Combine(root, "cluster_model.json"), FileAttributes.ReadOnly);

            var result = Migrate(root);

            Assert.Equal(1, result.Adopted);
            Assert.Equal("{}", File.ReadAllText(Path.Combine(root, "cluster_model.json")));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void AFileWhereTheRootShouldBe_IsSetAside_NotDeleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "TotallyHotArcRouter");
            File.WriteAllText(root, "not a directory");

            var result = Migrate(root);

            Assert.Equal(DataDirectoryMigrationOutcome.SquatSetAside, result.Outcome);
            Assert.Equal("not a directory", File.ReadAllText(result.SetAsidePath!));
            Assert.Equal(DataDirectoryState.Protected, WindowsDataDirectorySecurity.Inspect(root, TestPolicy).State);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void FileHeldOpen_StopsMigration_AndLeavesTheOldTreeInPlace()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(scratch, "TotallyHotArcRouter")).FullName;
            Write(root, "transcripts.db", "rows");

            using (new FileStream(Path.Combine(root, "transcripts.db"), FileMode.Open, FileAccess.ReadWrite,
                       FileShare.ReadWrite))
            {
                var ex = Assert.Throws<DataDirectoryMigrationBlockedException>(() => Migrate(root));
                Assert.Contains("transcripts.db", ex.Message);
            }

            Assert.Equal("rows", File.ReadAllText(Path.Combine(root, "transcripts.db")));
            Assert.Equal(DataDirectoryState.Unprotected, WindowsDataDirectorySecurity.Inspect(root, TestPolicy).State);

            // The next run, with nothing holding the file, resumes into the same staging tree and finishes.
            Assert.Single(DataDirectoryMigrationRules.FindStaging(root));
            Assert.Equal(DataDirectoryMigrationOutcome.Migrated, Migrate(root).Outcome);
            Assert.Equal("rows", File.ReadAllText(Path.Combine(root, "transcripts.db")));
            Assert.Empty(DataDirectoryMigrationRules.FindStaging(root));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void InterruptedSwap_IsCompleted_ByMovingTheStagingTreeIntoPlace()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "TotallyHotArcRouter");
            var staging = DataDirectoryMigrationRules.NewStagingPath(root);
            WindowsDataDirectorySecurity.CreateProtected(staging, TestPolicy);
            Write(staging, "transcripts.db", "rows");
            Write(staging, "half-written.db" + DataDirectoryMigrationRules.PartialCopySuffix, "partial");

            var result = Migrate(root);

            Assert.Equal(DataDirectoryMigrationOutcome.AlreadyProtected, result.Outcome);
            Assert.Equal("rows", File.ReadAllText(Path.Combine(root, "transcripts.db")));
            Assert.False(File.Exists(Path.Combine(root, "half-written.db" + DataDirectoryMigrationRules.PartialCopySuffix)));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// <summary>
    /// A crash between the two renames, after which something recreated the root (any account may, in
    /// %ProgramData%): the recreated entry is set aside and the migrated tree takes its place, rather than
    /// the recreated entry being treated as a squat that leaves the migrated data stranded.
    /// </summary>
    [Fact]
    public void InterruptedSwap_WithARecreatedRoot_InstallsTheMigratedTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "TotallyHotArcRouter");
            var staging = DataDirectoryMigrationRules.NewStagingPath(root);
            WindowsDataDirectorySecurity.CreateProtected(staging, TestPolicy);
            Write(staging, "transcripts.db", "migrated rows");
            Directory.CreateDirectory(DataDirectoryMigrationRules.NewRetiredPath(root));
            Directory.CreateDirectory(root);
            Write(root, "recreated.txt", "after the crash");

            var result = Migrate(root);

            Assert.Equal(DataDirectoryMigrationOutcome.AlreadyProtected, result.Outcome);
            Assert.Equal("migrated rows", File.ReadAllText(Path.Combine(root, "transcripts.db")));
            var aside = Assert.Single(Directory.EnumerateDirectories(scratch, "TotallyHotArcRouter.squatted-*"));
            Assert.Equal("after the crash", File.ReadAllText(Path.Combine(aside, "recreated.txt")));
            Assert.Empty(DataDirectoryMigrationRules.FindRetired(root));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void UnprotectedStagingLookalike_IsNotResumed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            // A directory any account could plant beside the root. Recovery must not adopt it as the new tree.
            var root = Path.Combine(scratch, "TotallyHotArcRouter");
            var planted = Directory.CreateDirectory(DataDirectoryMigrationRules.NewStagingPath(root)).FullName;
            Write(planted, "appsettings.local.json", "{ \"evil\": true }");

            var result = Migrate(root);

            Assert.Equal(DataDirectoryMigrationOutcome.Created, result.Outcome);
            Assert.False(File.Exists(Path.Combine(root, "appsettings.local.json")));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void MissingRoot_IsCreatedProtected_AndAProtectedRootIsLeftAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "TotallyHotArcRouter");

            Assert.Equal(DataDirectoryMigrationOutcome.Created, Migrate(root).Outcome);
            Write(root, "transcripts.db", "rows");
            Assert.Equal(DataDirectoryMigrationOutcome.AlreadyProtected, Migrate(root).Outcome);
            Assert.Equal("rows", File.ReadAllText(Path.Combine(root, "transcripts.db")));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void RootOwnedByAnUntrustedAccount_IsSetAsideUntouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        // Only an elevated run can give a directory an owner other than itself; there, the test account
        // plays the squatter and the machine policy plus an unrelated legacy owner decide.
        Assert.SkipUnless(WindowsDataDirectorySecurity.IsElevated(), "needs elevation to stand in an untrusted owner");
        var scratch = NewScratch();
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(scratch, "TotallyHotArcRouter")).FullName;
            Write(root, "appsettings.local.json", "{ \"planted\": true }");
            var security = new DirectoryInfo(root).GetAccessControl();
            security.SetOwner(CurrentUser);
            new DirectoryInfo(root).SetAccessControl(security);

            var unrelated = new SecurityIdentifier("S-1-5-21-1-2-3-4242");
            var result = new WindowsDataDirectoryMigration(root, WindowsDirectoryPolicy.Machine, unrelated,
                Serilog.Core.Logger.None).Run();

            Assert.Equal(DataDirectoryMigrationOutcome.SquatSetAside, result.Outcome);
            Assert.True(File.Exists(Path.Combine(result.SetAsidePath!, "appsettings.local.json")));
            Assert.False(File.Exists(Path.Combine(root, "appsettings.local.json")));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    private static DataDirectoryMigrationResult Migrate(string root)
    {
        return new WindowsDataDirectoryMigration(root, TestPolicy, CurrentUser, Serilog.Core.Logger.None).Run();
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
