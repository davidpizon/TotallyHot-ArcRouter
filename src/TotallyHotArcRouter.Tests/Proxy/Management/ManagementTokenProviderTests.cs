using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementTokenProvider"/>: it loads/creates the same way
/// <see cref="ManagementAccessToken.GetOrCreate"/> does, <see cref="IManagementTokenProvider.Regenerate"/>
/// persists a new value and bumps the generation, and a second provider reading the same path picks up
/// the rotated value.
/// </summary>
public sealed class ManagementTokenProviderTests
{
    private static string TempTokenPath()
    {
        return Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "management-token.txt");
    }

    private static void CleanUp(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public void CurrentToken_OnFirstConstruction_MatchesTheFileOnDisk()
    {
        var path = TempTokenPath();
        try
        {
            var provider = new ManagementTokenProvider(path);

            Assert.Equal(expected: File.ReadAllText(path).Trim(), actual: provider.CurrentToken);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Generation_StartsAtZero()
    {
        var path = TempTokenPath();
        try
        {
            var provider = new ManagementTokenProvider(path);

            Assert.Equal(expected: 0, actual: provider.Generation);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Regenerate_ChangesTheTokenAndPersistsIt()
    {
        var path = TempTokenPath();
        try
        {
            var provider = new ManagementTokenProvider(path);
            var original = provider.CurrentToken;

            var regenerated = provider.Regenerate();

            Assert.NotEqual(expected: original, actual: regenerated);
            Assert.Equal(expected: regenerated, actual: provider.CurrentToken);
            Assert.Equal(expected: regenerated, actual: File.ReadAllText(path).Trim());
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Regenerate_IncrementsGeneration()
    {
        var path = TempTokenPath();
        try
        {
            var provider = new ManagementTokenProvider(path);

            provider.Regenerate();
            provider.Regenerate();

            Assert.Equal(expected: 2, actual: provider.Generation);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Verify_OldTokenAfterRegenerate_ReturnsFalse()
    {
        var path = TempTokenPath();
        try
        {
            var provider = new ManagementTokenProvider(path);
            var original = provider.CurrentToken;

            provider.Regenerate();

            Assert.False(provider.Verify(original));
            Assert.True(provider.Verify(provider.CurrentToken));
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void SecondProvider_AfterRegenerate_ObservesTheRotatedToken()
    {
        var path = TempTokenPath();
        try
        {
            var first = new ManagementTokenProvider(path);
            var rotated = first.Regenerate();

            var second = new ManagementTokenProvider(path);

            Assert.Equal(expected: rotated, actual: second.CurrentToken);
        }
        finally
        {
            CleanUp(path);
        }
    }
}
