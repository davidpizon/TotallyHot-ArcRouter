using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>Covers <see cref="ManagementAccessToken"/>: generation, persistence, and constant-time verification.</summary>
public sealed class ManagementAccessTokenTests
{
    [Fact]
    public void GetOrCreate_NoExistingFile_GeneratesAStrongToken()
    {
        var path = TempTokenPath();
        try
        {
            var token = ManagementAccessToken.GetOrCreate(path);

            Assert.False(string.IsNullOrWhiteSpace(token));
            // 32 random bytes, base64url-encoded without padding, is at least 42 characters.
            Assert.True(token.Length >= 40, $"Expected a long random token; got '{token}' ({token.Length} chars).");
            Assert.True(File.Exists(path));
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void GetOrCreate_CalledTwice_ReturnsTheSamePersistedToken()
    {
        var path = TempTokenPath();
        try
        {
            var first = ManagementAccessToken.GetOrCreate(path);
            var second = ManagementAccessToken.GetOrCreate(path);

            Assert.Equal(first, second);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentFirstCalls_AllReturnTheSameToken()
    {
        // Simulates two processes racing to create the token file at the same moment (e.g. two router
        // instances starting together). The per-path Mutex in GetOrCreate must serialize them so exactly
        // one token is generated and every caller observes it - not each generating its own competing one.
        var path = TempTokenPath();
        try
        {
            var tasks = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => ManagementAccessToken.GetOrCreate(path)))
                .ToArray();

            var tokens = await Task.WhenAll(tasks);

            Assert.Single(tokens.Distinct());
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Verify_MatchingToken_ReturnsTrue()
    {
        Assert.True(ManagementAccessToken.Verify("abc123", "abc123"));
    }

    [Fact]
    public void Verify_WrongToken_ReturnsFalse()
    {
        Assert.False(ManagementAccessToken.Verify("wrong", "abc123"));
    }

    [Fact]
    public void Verify_TruncatedToken_ReturnsFalse()
    {
        Assert.False(ManagementAccessToken.Verify("abc", "abc123"));
    }

    [Fact]
    public void Verify_NullOrEmptyPresented_ReturnsFalse()
    {
        Assert.False(ManagementAccessToken.Verify(null, "abc123"));
        Assert.False(ManagementAccessToken.Verify(string.Empty, "abc123"));
    }

    [Fact]
    public void Verify_EmptyExpected_Throws()
    {
        Assert.Throws<ArgumentException>(() => ManagementAccessToken.Verify("abc", string.Empty));
    }

    /// <summary>
    /// The token is the one credential the <c>LocalSystem</c> service and the interactive-user GUI must
    /// both read, so its ACL is deliberately machine-wide rather than current-user-only: system and
    /// administrators write it, <c>Users</c> may only read it. A regression to a per-user ACL here is
    /// exactly what made the two processes mint separate tokens and every management call return 401.
    /// </summary>
    [Fact]
    public void GetOrCreate_OnWindows_GrantsSystemWriteAndUsersReadOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = TempTokenPath();
        try
        {
            ManagementAccessToken.GetOrCreate(path);
            AssertMachineSharedAcl(path);
        }
        finally
        {
            CleanUp(path);
        }
    }

    /// <summary>
    /// The Windows-only half of <see cref="GetOrCreate_OnWindows_GrantsSystemWriteAndUsersReadOnly"/>, split
    /// out so the platform annotation is on the method CA1416 actually analyzes - the analyzer doesn't treat
    /// an early-return guard in the caller as narrowing the platform.
    /// </summary>
    /// <param name="path">The token file whose ACL to assert on.</param>
    [SupportedOSPlatform("windows")]
    private static void AssertMachineSharedAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        // Inheritance from the parent directory must be broken - otherwise "restricted" is a lie and
        // whatever ACL the parent (or its parent, up to the drive root) happens to carry still applies.
        Assert.True(security.AreAccessRulesProtected);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        Assert.Contains(rules, rule =>
            rule.IdentityReference.Equals(system) &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));

        var usersRules = rules.Where(rule => rule.IdentityReference.Equals(users)).ToList();
        Assert.NotEmpty(usersRules);

        // Read, never write: the interactive user presents this credential but must not be able to
        // replace the one the service trusts.
        Assert.All(usersRules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.False(rule.FileSystemRights.HasFlag(FileSystemRights.Write));
            Assert.False(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        });
    }

    /// <summary>
    /// The path itself is the contract between the two processes: <c>ManagementTokenReader</c> and
    /// <c>TelemetryAuthClientInterceptor</c> hardcode their own copies of it across the assembly boundary,
    /// so a change here that isn't mirrored there silently breaks authentication in the installed build.
    /// </summary>
    [Fact]
    public void DefaultPath_IsMachineWideNotPerUser()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TotallyHotArcRouter",
            "management-token.txt");

        Assert.Equal(expected, ManagementAccessToken.DefaultPath());
    }

    private static string TempTokenPath() =>
        Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "management-token.txt");

    private static void CleanUp(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

