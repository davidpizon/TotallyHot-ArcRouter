using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementAccessToken"/>: generation, persistence in <see cref="ProtectedSecretStore"/>,
/// the one-time legacy-file import (web GUI migration plan Phase P9), and constant-time verification.
/// </summary>
public sealed class ManagementAccessTokenTests
{
    [Fact]
    public void GetOrCreate_NoExistingSecret_GeneratesAStrongToken()
    {
        var store = new ProtectedSecretStore(TempStorePath());

        var token = ManagementAccessToken.GetOrCreate(store);

        Assert.False(string.IsNullOrWhiteSpace(token));
        // 32 random bytes, base64url-encoded without padding, is at least 42 characters.
        Assert.True(condition: token.Length >= 40,
            userMessage: $"Expected a long random token; got '{token}' ({token.Length} chars).");
        Assert.True(store.Exists(ManagementAccessToken.SecretName));
    }

    [Fact]
    public void GetOrCreate_CalledTwice_ReturnsTheSamePersistedToken()
    {
        var store = new ProtectedSecretStore(TempStorePath());

        var first = ManagementAccessToken.GetOrCreate(store);
        var second = ManagementAccessToken.GetOrCreate(store);

        Assert.Equal(expected: first, actual: second);
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentFirstCalls_AllReturnTheSameToken()
    {
        // Simulates two processes racing to create the token at the same moment (e.g. two router
        // instances starting together). ProtectedSecretStore's own per-path Mutex must serialize the
        // underlying read-modify-write cycles so exactly one token is generated and every caller
        // observes it - not each generating its own competing one.
        var path = TempStorePath();
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ManagementAccessToken.GetOrCreate(new ProtectedSecretStore(path))))
            .ToArray();

        var tokens = await Task.WhenAll(tasks);

        Assert.Single(tokens.Distinct());
    }

    [Fact]
    public void Regenerate_ReturnsADifferentTokenAndPersistsIt()
    {
        var store = new ProtectedSecretStore(TempStorePath());
        var original = ManagementAccessToken.GetOrCreate(store);

        var regenerated = ManagementAccessToken.Regenerate(store);

        Assert.NotEqual(expected: original, actual: regenerated);
        Assert.Equal(expected: regenerated, actual: ManagementAccessToken.GetOrCreate(store));
    }

    [Fact]
    public void Regenerate_NoExistingSecret_StillCreatesOne()
    {
        var store = new ProtectedSecretStore(TempStorePath());

        var token = ManagementAccessToken.Regenerate(store);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(store.Exists(ManagementAccessToken.SecretName));
    }

    [Fact]
    public void GetOrCreate_LegacyFileExists_ImportsItAndDeletesTheFile()
    {
        var store = new ProtectedSecretStore(TempStorePath());
        var legacyPath = TempLegacyTokenPath();
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(path: legacyPath, contents: "legacy-token-value");

        var token = ManagementAccessToken.GetOrCreate(store: store, legacyTokenPath: legacyPath);

        Assert.Equal(expected: "legacy-token-value", actual: token);
        Assert.True(store.TryRead(name: ManagementAccessToken.SecretName, value: out var stored));
        Assert.Equal(expected: "legacy-token-value", actual: stored);
        Assert.False(File.Exists(legacyPath), "the legacy file must be deleted once its token has been imported");
    }

    [Fact]
    public void GetOrCreate_LegacyFileExists_ButSecretAlreadyStored_IgnoresTheLegacyFile()
    {
        var store = new ProtectedSecretStore(TempStorePath());
        ManagementAccessToken.GetOrCreate(store);
        var legacyPath = TempLegacyTokenPath();
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(path: legacyPath, contents: "legacy-token-value");

        var token = ManagementAccessToken.GetOrCreate(store: store, legacyTokenPath: legacyPath);

        Assert.NotEqual(expected: "legacy-token-value", actual: token);
        Assert.True(File.Exists(legacyPath), "an already-migrated store must never touch a leftover legacy file");
    }

    [Fact]
    public void GetOrCreate_LegacyFileExistsButCannotBeRead_ThrowsRatherThanMintingAReplacement()
    {
        // Regression coverage for a real bug: silently swallowing a read failure and falling through to
        // GenerateToken() would mint and persist a brand-new token while the still-there, still-unread
        // legacy one becomes permanently orphaned - invalidating every MCP client already configured
        // with it, which is exactly what importing this file exists to prevent. An exclusive lock on the
        // file (not deleting/corrupting it - the file is perfectly readable once unlocked) simulates any
        // "exists but not readable right now" failure without depending on real OS permission plumbing.
        var store = new ProtectedSecretStore(TempStorePath());
        var legacyPath = TempLegacyTokenPath();
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(path: legacyPath, contents: "legacy-token-value");

        using (new FileStream(path: legacyPath, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() =>
                ManagementAccessToken.GetOrCreate(store: store, legacyTokenPath: legacyPath));
        }

        Assert.False(store.Exists(ManagementAccessToken.SecretName),
            "a read failure must not fall through to minting and persisting a replacement token");
        Assert.True(File.Exists(legacyPath), "the legacy file must be left untouched when it could not be read");
    }

    [Fact]
    public void GetOrCreate_NoLegacyFile_GeneratesAFreshTokenNormally()
    {
        var store = new ProtectedSecretStore(TempStorePath());

        var token = ManagementAccessToken.GetOrCreate(store: store, legacyTokenPath: TempLegacyTokenPath());

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void Verify_MatchingToken_ReturnsTrue()
    {
        Assert.True(ManagementAccessToken.Verify(presented: "abc123", expected: "abc123"));
    }

    [Fact]
    public void Verify_WrongToken_ReturnsFalse()
    {
        Assert.False(ManagementAccessToken.Verify(presented: "wrong", expected: "abc123"));
    }

    [Fact]
    public void Verify_TruncatedToken_ReturnsFalse()
    {
        Assert.False(ManagementAccessToken.Verify(presented: "abc", expected: "abc123"));
    }

    [Fact]
    public void Verify_NullOrEmptyPresented_ReturnsFalse()
    {
        Assert.False(ManagementAccessToken.Verify(null, expected: "abc123"));
        Assert.False(ManagementAccessToken.Verify(presented: string.Empty, expected: "abc123"));
    }

    [Fact]
    public void Verify_EmptyExpected_Throws()
    {
        Assert.Throws<ArgumentException>(() => ManagementAccessToken.Verify(presented: "abc", expected: string.Empty));
    }

    private static string TempStorePath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests", path3: Guid.NewGuid().ToString("N"),
            path4: "secrets.dat");
    }

    private static string TempLegacyTokenPath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests", path3: Guid.NewGuid().ToString("N"),
            path4: "management-token.txt");
    }
}
