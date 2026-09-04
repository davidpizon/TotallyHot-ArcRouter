using System.ComponentModel.DataAnnotations;

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
    public string ModelCacheDirectory { get; init; } = @"%LOCALAPPDATA%\TotallyHot.ArcRouter\models\bge-large-en-v1.5";

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
    /// the same <c>%LOCALAPPDATA%</c> token for the price catalog's SQLite file.
    /// </summary>
    public string ResolveModelCacheDirectory()
    {
        var expanded = Environment.ExpandEnvironmentVariables(ModelCacheDirectory);

        if (expanded.Contains(value: LocalAppDataToken, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData)) localAppData = AppContext.BaseDirectory;

            localAppData = localAppData.TrimEnd('/', '\\');
            expanded = expanded.Replace(oldValue: LocalAppDataToken, newValue: localAppData,
                comparisonType: StringComparison.OrdinalIgnoreCase);
        }

        expanded = expanded.Replace('\\', newChar: Path.DirectorySeparatorChar);

        return Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(path1: AppContext.BaseDirectory, path2: expanded);
    }
}