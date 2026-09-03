using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="StorageOptions.ResolveDatabasePath"/> and <see cref="StorageOptions.ResolveBenchmarkDatabasePath"/>'s
/// shared cross-platform hardening.
/// </summary>
public class StorageOptionsTests
{
    [Fact]
    public void ResolveDatabasePath_Default_IsRootedWithNoUnexpandedTokens()
    {
        // The default uses %PROGRAMDATA% and backslashes. On Linux (the Docker default) PROGRAMDATA is
        // unset and backslashes are literal, so a naive resolve would leave a "%PROGRAMDATA%" filename in
        // an unrooted path. The fallback + separator normalization must produce a real absolute path.
        var resolved = new StorageOptions().ResolveDatabasePath();

        Assert.True(Path.IsPathRooted(resolved));
        Assert.DoesNotContain('%', collection: resolved);
        // No doubled separator from an empty token substitution. (A leading separator on a POSIX absolute
        // path is expected and not checked here.)
        var doubledSeparator = new string(c: Path.DirectorySeparatorChar, 2);
        Assert.DoesNotContain(expectedSubstring: doubledSeparator, actualString: resolved);
    }

    [Fact]
    public void AllFiveDefaults_ResolveIntoTheOneMachineSharedDirectory()
    {
        // The move to %ProgramData% also collapsed a folder-name split: DatabasePath was pinned under
        // "TotallyHotArcRouter" by appsettings.json while the other four defaults used
        // "TotallyHot.ArcRouter", so one install wrote two sibling directories. Assert they cannot drift
        // apart again.
        var options = new StorageOptions();
        var expected = StorageOptions.ResolveMachineSharedDirectory();

        string[] resolved =
        [
            options.ResolveDatabasePath(),
            options.ResolveBenchmarkDatabasePath(),
            options.ResolveTranscriptDatabasePath(),
            options.ResolveLogRegModelPath(),
            options.ResolveClusterModelPath()
        ];

        Assert.All(collection: resolved,
            action: path => Assert.Equal(expected: expected, actual: Path.GetDirectoryName(path)));
    }

    [Fact]
    public void ResolveLegacyDirectories_CoverBothPreMoveSpellings()
    {
        var legacy = StorageOptions.ResolveLegacyDirectories();

        Assert.Equal(2, actual: legacy.Count);
        Assert.Contains(collection: legacy, filter: path => Path.GetFileName(path) == "TotallyHot.ArcRouter");
        Assert.Contains(collection: legacy, filter: path => Path.GetFileName(path) == "TotallyHotArcRouter");
        Assert.All(collection: legacy, action: path => Assert.True(Path.IsPathRooted(path)));
    }

    [Fact]
    public void ResolveDatabasePath_LegacyLocalAppDataToken_IsStillExpanded()
    {
        // An operator upgrading from a build that predates the move may have %LOCALAPPDATA% pinned in
        // their own appsettings.json. That must keep resolving rather than producing a literal
        // "%LOCALAPPDATA%" directory.
        var resolved = new StorageOptions { DatabasePath = @"%LOCALAPPDATA%\Pinned\prices.db" }.ResolveDatabasePath();

        Assert.True(Path.IsPathRooted(resolved));
        Assert.DoesNotContain('%', collection: resolved);
        Assert.EndsWith(expectedEndString: $"Pinned{Path.DirectorySeparatorChar}prices.db", actualString: resolved);
    }

    [Fact]
    public void ResolveDatabasePath_BackslashRelativePath_NormalizesToPlatformSeparator()
    {
        var resolved = new StorageOptions { DatabasePath = @"data\prices.db" }.ResolveDatabasePath();

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(expectedEndString: $"data{Path.DirectorySeparatorChar}prices.db", actualString: resolved);
    }

    [Fact]
    public void ResolveDatabasePath_AbsolutePath_IsReturnedAsIs()
    {
        var absolute = Path.Combine(path1: Path.GetTempPath(), path2: "explicit.db");

        var resolved = new StorageOptions { DatabasePath = absolute }.ResolveDatabasePath();

        Assert.Equal(expected: absolute, actual: resolved);
    }

    [Fact]
    public void ResolveBenchmarkDatabasePath_Default_IsRootedWithNoUnexpandedTokens()
    {
        var resolved = new StorageOptions().ResolveBenchmarkDatabasePath();

        Assert.True(Path.IsPathRooted(resolved));
        Assert.DoesNotContain('%', collection: resolved);
        var doubledSeparator = new string(c: Path.DirectorySeparatorChar, 2);
        Assert.DoesNotContain(expectedSubstring: doubledSeparator, actualString: resolved);
    }

    [Fact]
    public void ResolveBenchmarkDatabasePath_BackslashRelativePath_NormalizesToPlatformSeparator()
    {
        var resolved = new StorageOptions { BenchmarkDatabasePath = @"data\coderouterbench.db" }
            .ResolveBenchmarkDatabasePath();

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(expectedEndString: $"data{Path.DirectorySeparatorChar}coderouterbench.db",
            actualString: resolved);
    }

    [Fact]
    public void ResolveBenchmarkDatabasePath_AbsolutePath_IsReturnedAsIs()
    {
        var absolute = Path.Combine(path1: Path.GetTempPath(), path2: "explicit-benchmark.db");

        var resolved = new StorageOptions { BenchmarkDatabasePath = absolute }.ResolveBenchmarkDatabasePath();

        Assert.Equal(expected: absolute, actual: resolved);
    }
}