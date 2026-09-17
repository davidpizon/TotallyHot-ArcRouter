using System.ComponentModel.DataAnnotations;
using TotallyHot.ArcRouter.Hosting;

namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Configures the local, in-process BGE-large embedding client that backs PLAN.md Phase J's
/// task-embedding-keyed memory. Bound from the <c>Embeddings</c> section.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>Gets the configuration section name used for embedding settings.</summary>
    public const string SectionName = "Embeddings";

    // The default's leading token, matching StorageOptions.ResolveDatabasePath's handling of the same
    // token for the price catalog's SQLite file.
    private const string LocalAppDataToken = "%LOCALAPPDATA%";

    /// <summary>
    /// Gets the directory the ONNX model and tokenizer artifacts are cached in after first download.
    /// May contain environment-variable tokens (e.g. <c>%LOCALAPPDATA%</c>); a relative path is
    /// resolved against the application base directory.
    /// </summary>
    [Required]
    public string ModelCacheDirectory { get; init; } = @"%LOCALAPPDATA%\models\bge-large-en-v1.5";

    /// <summary>
    /// Gets the URL the ONNX model weights are downloaded from on first use, if not already present in
    /// <see cref="ModelCacheDirectory"/>. Defaults to the community BGE-large-en-v1.5 ONNX export.
    /// </summary>
    [Required]
    public string ModelUrl { get; init; } =
        "https://huggingface.co/Xenova/bge-large-en-v1.5/resolve/main/onnx/model.onnx";

    /// <summary>
    /// Gets the URL the Hugging Face <c>tokenizer.json</c> is downloaded from on first use, if not
    /// already present in <see cref="ModelCacheDirectory"/>.
    /// </summary>
    [Required]
    public string TokenizerJsonUrl { get; init; } =
        "https://huggingface.co/Xenova/bge-large-en-v1.5/resolve/main/tokenizer.json";

    /// <summary>
    /// Gets the dimensionality of the embedding vectors BGE-large produces. Used to validate model
    /// output shape rather than trusting it silently.
    /// </summary>
    [Range(1, 8192)]
    public int EmbeddingDimension { get; init; } = 1024;

    /// <summary>
    /// Gets the maximum number of WordPiece tokens encoded per request. BGE-large's own maximum
    /// sequence length.
    /// </summary>
    [Range(1, 512)]
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// Performs domain-level validation that is not fully expressible through data annotations.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when a required URL is not an absolute URI.</exception>
    public void EnsureValid()
    {
        if (!Uri.TryCreate(uriString: ModelUrl, uriKind: UriKind.Absolute, result: out _))
            throw new ArgumentException(message: $"'{nameof(ModelUrl)}' must be an absolute URI.",
                paramName: nameof(ModelUrl));

        if (!Uri.TryCreate(uriString: TokenizerJsonUrl, uriKind: UriKind.Absolute, result: out _))
            throw new ArgumentException(message: $"'{nameof(TokenizerJsonUrl)}' must be an absolute URI.",
                paramName: nameof(TokenizerJsonUrl));
    }

    /// <summary>
    /// Expands environment-variable tokens in <see cref="ModelCacheDirectory"/>, normalizes
    /// separators, and returns an absolute path, mirroring
    /// <see cref="TotallyHot.ArcRouter.PriceCatalog.StorageOptions.ResolveDatabasePath"/>'s handling of
    /// the same token. The literal <c>%LOCALAPPDATA%</c> spelling is kept for backward compatibility
    /// with an operator's existing <c>appsettings.json</c> override, but it now expands to the
    /// machine-shared data directory (<see cref="AppDataPaths"/>; web GUI migration plan Phase P3)
    /// rather than the per-user special folder - this cache is re-downloadable, not per-account state, so
    /// sharing it avoids a duplicate multi-hundred-megabyte download per OS account.
    /// </summary>
    public string ResolveModelCacheDirectory()
    {
        // The token is substituted BEFORE Environment.ExpandEnvironmentVariables, not after - the same
        // ordering fix StorageOptions.ResolvePath already carries (web GUI migration plan Phase P10). On
        // Windows LOCALAPPDATA is a genuine OS environment variable, so expanding first resolved it to the
        // real per-user folder and left no token for the substitution below to find: the machine-shared
        // redirection this method documents silently never happened on the one platform it matters on, and
        // every OS account kept its own ~2.1 GB copy of the model artifacts - the installed LocalSystem
        // service downloading a second full set into
        // C:\Windows\system32\config\systemprofile\AppData\Local beside the interactive user's.
        var withTokensExpanded = ModelCacheDirectory;

        if (withTokensExpanded.Contains(value: LocalAppDataToken, comparisonType: StringComparison.OrdinalIgnoreCase))
            withTokensExpanded = withTokensExpanded.Replace(oldValue: LocalAppDataToken,
                newValue: AppDataPaths.ResolveMachineSharedDirectory(),
                comparisonType: StringComparison.OrdinalIgnoreCase);

        // Still run the real expander afterward, for any other environment-variable token an operator's
        // own override might embed; our token is already gone, so this cannot re-expand it out from under
        // the substitution above.
        var expanded = Environment.ExpandEnvironmentVariables(withTokensExpanded);

        expanded = expanded.Replace('\\', newChar: Path.DirectorySeparatorChar);

        return Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(path1: AppContext.BaseDirectory, path2: expanded);
    }
}