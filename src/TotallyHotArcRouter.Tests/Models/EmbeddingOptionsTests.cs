using TotallyHot.ArcRouter.Hosting;
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

        Assert.Throws<ArgumentException>(options.EnsureValid);
    }

    [Fact]
    public void EnsureValid_Throws_WhenTokenizerJsonUrlIsNotAbsolute()
    {
        var options = new EmbeddingOptions { TokenizerJsonUrl = "not-a-url" };

        Assert.Throws<ArgumentException>(options.EnsureValid);
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

    /// <summary>
    /// The regression test for the ordering bug: the token must expand to the <em>machine-shared</em>
    /// directory, not the per-user one. Asserting only that the token is gone - which
    /// <see cref="ResolveModelCacheDirectory_ExpandsLocalAppDataToken"/> did alone - passes either way,
    /// which is how the bug survived: on Windows
    /// <see cref="Environment.ExpandEnvironmentVariables"/> consumed the genuine <c>LOCALAPPDATA</c>
    /// variable first, so every OS account silently kept its own ~2.1 GB copy of the model artifacts.
    /// </summary>
    [Fact]
    public void ResolveModelCacheDirectory_ExpandsTokenToTheMachineSharedDirectory_NotThePerUserOne()
    {
        var options = new EmbeddingOptions { ModelCacheDirectory = @"%LOCALAPPDATA%\models\bge" };

        var resolved = options.ResolveModelCacheDirectory();

        Assert.StartsWith(expectedStartString: AppDataPaths.ResolveMachineSharedDirectory(),
            actualString: resolved, comparisonType: StringComparison.OrdinalIgnoreCase);

        // And specifically not the per-user folder the real environment variable names, so this fails if
        // the substitution is ever reordered back after the expander.
        var perUser = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(perUser)
            && !AppDataPaths.ResolveMachineSharedDirectory()
                .StartsWith(value: perUser, comparisonType: StringComparison.OrdinalIgnoreCase))
            Assert.DoesNotContain(expectedSubstring: perUser, actualString: resolved,
                comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="LlmRouterOptions"/> carries its own copy of the same expansion, so it needs its own
    /// guard - the two drifted apart once already, which is the whole reason this pair of tests exists.
    /// </summary>
    [Fact]
    public void LlmRouterOptions_ResolvesModelCacheToTheMachineSharedDirectory()
    {
        var options = new LlmRouterOptions { ModelCacheDirectory = @"%LOCALAPPDATA%\models\qwen" };

        Assert.StartsWith(expectedStartString: AppDataPaths.ResolveMachineSharedDirectory(),
            actualString: options.ResolveModelCacheDirectory(),
            comparisonType: StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(expectedStartString: AppDataPaths.ResolveMachineSharedDirectory(),
            actualString: LlmRouterOptions.ResolveModelsRootDirectory(),
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}