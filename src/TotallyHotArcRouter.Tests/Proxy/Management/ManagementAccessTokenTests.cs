using System.Runtime.InteropServices;
using System.Security.AccessControl;
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

    [Fact]
    public void GetOrCreate_OnWindows_RestrictsFileToCurrentUser()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var path = TempTokenPath();
        try
        {
            ManagementAccessToken.GetOrCreate(path);

            var security = new FileInfo(path).GetAccessControl();
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.NTAccount));

            // Inheritance from the parent directory must be broken - otherwise "restricted" is a lie and
            // whatever ACL the parent (or its parent, up to the drive root) happens to carry still applies.
            Assert.True(security.AreAccessRulesProtected);
            Assert.NotEmpty(rules.Cast<System.Security.AccessControl.FileSystemAccessRule>());
        }
        finally
        {
            CleanUp(path);
        }
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

