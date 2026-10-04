using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Hosting.DataDirectory;
using static TotallyHot.ArcRouter.Tests.Hosting.DataDirectory.WindowsDataDirectoryTestSupport;

namespace TotallyHot.ArcRouter.Tests.Hosting.DataDirectory;

/// <summary>
/// Covers <see cref="WindowsDataDirectorySecurity"/> (ADR-0024 rules 1 and 2) against real directories:
/// atomic creation with a protected DACL, each check <see cref="WindowsDataDirectorySecurity.Inspect"/>
/// makes, and the property the whole design rests on - that SQLite's re-created <c>-wal</c> file inherits
/// the protected DACL, which plan finding F5 showed a per-file ACL cannot give it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDataDirectorySecurityTests
{
    [Fact]
    public void CreateProtected_ThenInspect_IsProtected_WithOnlyThePolicyAccountsGranted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "root");
            WindowsDataDirectorySecurity.CreateProtected(root, TestPolicy);

            var inspection = WindowsDataDirectorySecurity.Inspect(root, TestPolicy);

            Assert.Equal(DataDirectoryState.Protected, inspection.State);
            Assert.True(new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected);
            Assert.All(RulesOf(root), rule => Assert.Contains(rule.IdentityReference, TestPolicy.FullControl));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void CreateProtected_WhenSomethingExists_Throws()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            Assert.Throws<IOException>(() => WindowsDataDirectorySecurity.CreateProtected(scratch, TestPolicy));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void Inspect_MissingPath_IsMissing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            Assert.Equal(DataDirectoryState.Missing,
                WindowsDataDirectorySecurity.Inspect(Path.Combine(scratch, "absent"), TestPolicy).State);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void Inspect_PlainDirectoryInheritingItsParent_IsUnprotected_ButOwnedByATrustedAccount()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            // The pre-ADR-0024 layout: a Directory.CreateDirectory, which inherits its parent's ACL.
            var root = Directory.CreateDirectory(Path.Combine(scratch, "legacy")).FullName;

            var inspection = WindowsDataDirectorySecurity.Inspect(root, TestPolicy);

            Assert.Equal(DataDirectoryState.Unprotected, inspection.State);
            Assert.True(inspection.OwnerTrusted);
            Assert.Contains("inherits", inspection.Reason);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void Inspect_ProtectedDirectoryGrantingUsers_IsUnprotected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "root");
            var security = WindowsDataDirectorySecurity.BuildSecurity(TestPolicy);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);

            var inspection = WindowsDataDirectorySecurity.Inspect(root, TestPolicy);

            Assert.Equal(DataDirectoryState.Unprotected, inspection.State);
            Assert.Contains("grants access", inspection.Reason);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void Inspect_UnderTheMachinePolicy_ADirectoryThisAccountOwns_IsNotTrusted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        Assert.SkipWhen(WindowsDataDirectorySecurity.IsElevated(), "an elevated run may own it as Administrators");
        var scratch = NewScratch();
        try
        {
            // An individual account may never own the protected root: it could rewrite the DACL at will.
            var inspection = WindowsDataDirectorySecurity.Inspect(scratch, WindowsDirectoryPolicy.Machine);

            Assert.Equal(DataDirectoryState.Unprotected, inspection.State);
            Assert.False(inspection.OwnerTrusted);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void Inspect_Junction_IsUnprotected_EvenWhenItsTargetIsProtected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var target = Path.Combine(scratch, "target");
            WindowsDataDirectorySecurity.CreateProtected(target, TestPolicy);
            var link = Path.Combine(scratch, "link");
            CreateJunction(link, target);

            var inspection = WindowsDataDirectorySecurity.Inspect(link, TestPolicy);

            Assert.Equal(DataDirectoryState.Unprotected, inspection.State);
            Assert.Contains("junction", inspection.Reason);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// <summary>
    /// Plan finding F5, the reason ADR-0024 protects the directory rather than individual files: SQLite
    /// deletes and re-creates <c>-wal</c>, so it can only ever carry what the folder hands down.
    /// </summary>
    [Fact]
    public void SqliteWalFile_CreatedInsideAProtectedDirectory_InheritsOnlyThePolicyAccounts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var root = Path.Combine(scratch, "root");
            WindowsDataDirectorySecurity.CreateProtected(root, TestPolicy);
            var databasePath = Path.Combine(root, "transcripts.db");

            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (x TEXT); INSERT INTO t VALUES ('secret');";
                command.ExecuteNonQuery();

                var walRules = RulesOf(databasePath + "-wal");
                Assert.NotEmpty(walRules);
                Assert.All(walRules, rule => Assert.Contains(rule.IdentityReference, TestPolicy.FullControl));
            }
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }
}
