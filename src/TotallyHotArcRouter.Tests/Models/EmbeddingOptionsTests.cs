using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Covers defaults and domain validation for <see cref="EmbeddingOptions"/>.
/// </summary>
public class EmbeddingOptionsTests
{
    [Fact]
    public void Defaults_AreExpected()
    {
        var options = new EmbeddingOptions();

        Assert.Equal(1024, actual: options.EmbeddingDimension);
        Assert.Equal(512, actual: options.MaxTokens);
        Assert.False(string.IsNullOrWhiteSpace(options.ModelUrl));
        Assert.False(string.IsNullOrWhiteSpace(options.TokenizerJsonUrl));
    }

    [Fact]
    public void EnsureValid_Throws_WhenModelUrlIsNotAbsolute()
    {
        var options = new EmbeddingOptions { ModelUrl = "not-a-url" };

        Assert.Throws<ArgumentException>(() => options.EnsureValid());
    }

    [Fact]
    public void EnsureValid_Throws_WhenTokenizerJsonUrlIsNotAbsolute()
    {
        var options = new EmbeddingOptions { TokenizerJsonUrl = "not-a-url" };

        Assert.Throws<ArgumentException>(() => options.EnsureValid());
    }

    [Fact]
    public void EnsureValid_DoesNotThrow_ForDefaults()
    {
        var options = new EmbeddingOptions();

        var exception = Record.Exception(options.EnsureValid);

        Assert.Null(exception);
    }

    [Fact]
    public void ResolveModelCacheDirectory_ReturnsAbsolutePath()
    {
        var options = new EmbeddingOptions { ModelCacheDirectory = "relative-cache-dir" };

        var resolved = options.ResolveModelCacheDirectory();

        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void ResolveModelCacheDirectory_ExpandsLocalAppDataToken()
    {
        var options = new EmbeddingOptions();

        var resolved = options.ResolveModelCacheDirectory();

        Assert.DoesNotContain(expectedSubstring: "%LOCALAPPDATA%", actualString: resolved,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}