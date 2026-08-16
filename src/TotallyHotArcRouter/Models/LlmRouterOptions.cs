using System.ComponentModel.DataAnnotations;

namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Configures the local, in-process ONNX GenAI text-generation model that backs the <c>llm_router</c>
/// voter (PLAN.md Phase L; <see cref="Router.Orchestrator.LlmRouterVoter"/>). Bound from the
/// <c>LlmRouter</c> section.
/// </summary>
/// <remarks>
/// <para>
/// The default artifact URLs point at a community-maintained Hugging Face export
/// (<c>xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai</c>), not an official Microsoft-published one -
/// the paper's own fine-tuned Qwen3.5-0.8B checkpoint was never published, so this voter substitutes
/// a small, off-the-shelf, prompted instruct model instead (an explicit, documented scope narrowing,
/// not the paper's design). If these defaults ever stop resolving, the voter degrades to a permanent,
/// harmless abstention - the same graceful-degrade path <see cref="Router.Orchestrator.DimBestVoter"/>
/// already takes when its benchmark database isn't synced - so a stale or wrong URL here is a
/// capability loss, not a build or startup failure. Override any URL to point at a verified mirror.
/// </para>
/// <para>
/// That repository publishes <em>nothing at its root</em>: every artifact lives under a per-execution-
/// provider subfolder (<c>cpu_and_mobile/…</c>, <c>cuda/…</c>, <c>directml/…</c>), so each URL below
/// must name one build explicitly. These defaults pin the CPU build
/// (<c>cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4</c>, int4 RTN block-32 weights with int8
/// accumulation) because this voter runs in-process on whatever machine hosts the router, with no
/// GPU assumed. To use the CUDA or DirectML build instead, override all five URLs - and
/// <see cref="ModelCacheDirectory"/>, so the two builds' artifacts never mix in one folder.
/// </para>
/// </remarks>
public sealed class LlmRouterOptions
{
    /// <summary>Gets the configuration section name used for llm_router model settings.</summary>
    public const string SectionName = "LlmRouter";

    // Matches EmbeddingOptions.LocalAppDataToken - the same %LOCALAPPDATA% expansion convention used
    // throughout this repo's model-cache-directory options.
    private const string LocalAppDataToken = "%LOCALAPPDATA%";

    // The shared parent of every llm_router model's cache directory. ModelCacheDirectory's default value
    // above is this root plus this build's own subpath; LlmRouterModelOverrideStore resolves a distinct
    // leaf directory per model URL under this same root, so switching models never mixes one model's
    // files with another's.
    private const string ModelsRootDirectory = @"%LOCALAPPDATA%\TotallyHot.ArcRouter\models";

    // The execution-provider build the default URLs below pin. Kept as a single constant so the five
    // artifact URLs can never drift onto different builds - mixing, say, a cuda-fp16 model.onnx with a
    // cpu genai_config.json produces a load failure that reads like a corrupt download.
    private const string ModelVariantPath =
        "https://huggingface.co/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";

    /// <summary>
    /// Gets the directory the ONNX GenAI model artifacts are cached in after first download. May
    /// contain environment-variable tokens (e.g. <c>%LOCALAPPDATA%</c>); a relative path is resolved
    /// against the application base directory. The default path names the execution-provider build the
    /// artifact URLs pin, so pointing them at a different build (CUDA, DirectML) and re-pointing this
    /// directory keeps each build's artifacts in a cache folder of its own.
    /// </summary>
    [Required]
    public string ModelCacheDirectory { get; init; } =
        @"%LOCALAPPDATA%\TotallyHot.ArcRouter\models\qwen2.5-0.5b-instruct-onnx-genai\cpu-int4-rtn-block-32-acc-level-4";

    /// <summary>
    /// Gets the URL <c>genai_config.json</c> (the ONNX Runtime GenAI model/generation configuration) is
    /// downloaded from on first use, if not already present in <see cref="ModelCacheDirectory"/>.
    /// </summary>
    [Required]
    public string GenAiConfigUrl { get; init; } = $"{ModelVariantPath}/genai_config.json";

    /// <summary>
    /// Gets the URL the Hugging Face <c>tokenizer.json</c> is downloaded from on first use, if not
    /// already present in <see cref="ModelCacheDirectory"/>.
    /// </summary>
    [Required]
    public string TokenizerJsonUrl { get; init; } = $"{ModelVariantPath}/tokenizer.json";

    /// <summary>
    /// Gets the URL <c>tokenizer_config.json</c> is downloaded from on first use, if not already
    /// present in <see cref="ModelCacheDirectory"/>. ONNX Runtime GenAI's tokenizer reads chat-template
    /// and special-token metadata from this file alongside <see cref="TokenizerJsonUrl"/>.
    /// </summary>
    [Required]
    public string TokenizerConfigJsonUrl { get; init; } = $"{ModelVariantPath}/tokenizer_config.json";

    /// <summary>
    /// Gets the URL the ONNX model graph is downloaded from on first use, if not already present in
    /// <see cref="ModelCacheDirectory"/>. This file holds the graph only (a few hundred kilobytes); the
    /// weights it references live in <see cref="ModelOnnxDataUrl"/>.
    /// </summary>
    [Required]
    public string ModelOnnxUrl { get; init; } = $"{ModelVariantPath}/model.onnx";

    /// <summary>
    /// Gets the URL the ONNX model's external weights data file is downloaded from, if not already
    /// present in <see cref="ModelCacheDirectory"/>. Every build in the default repository stores its
    /// weights externally - <c>model.onnx</c> references them by the relative filename
    /// <c>model.onnx.data</c>, so this download is required, not optional, and the cached file must keep
    /// that exact name beside the graph. Set to <see langword="null"/> only when pointing
    /// <see cref="ModelOnnxUrl"/> at an export that inlines all weights in its protobuf, which skips
    /// this download rather than failing on a 404.
    /// </summary>
    public string? ModelOnnxDataUrl { get; init; } = $"{ModelVariantPath}/model.onnx.data";

    /// <summary>
    /// Gets the maximum number of new tokens generated per routing decision. Defaults to research-doc
    /// §B.2's decoding configuration (<c>max_new_tokens=96</c>) for the paper's own router prompt.
    /// </summary>
    [Range(1, 4096)]
    public int MaxNewTokens { get; init; } = 96;

    /// <summary>
    /// Gets the maximum time, in milliseconds, <see cref="Router.Orchestrator.LlmRouterVoter"/> waits
    /// for a generation to finish before abstaining. The routing hot path must never block on this
    /// voter indefinitely - a slow or hung generation degrades to the same abstention as a missing model
    /// artifact, letting the other three voters still decide.
    /// </summary>
    [Range(1, 60_000)]
    public int GenerationTimeoutMs { get; init; } = 8_000;

    /// <summary>
    /// Performs domain-level validation that is not fully expressible through data annotations.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when a required URL is not an absolute URI.</exception>
    public void EnsureValid()
    {
        EnsureAbsoluteUri(GenAiConfigUrl, nameof(GenAiConfigUrl));
        EnsureAbsoluteUri(TokenizerJsonUrl, nameof(TokenizerJsonUrl));
        EnsureAbsoluteUri(TokenizerConfigJsonUrl, nameof(TokenizerConfigJsonUrl));
        EnsureAbsoluteUri(ModelOnnxUrl, nameof(ModelOnnxUrl));

        if (ModelOnnxDataUrl is not null)
        {
            EnsureAbsoluteUri(ModelOnnxDataUrl, nameof(ModelOnnxDataUrl));
        }
    }

    private static void EnsureAbsoluteUri(string value, string propertyName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new ArgumentException($"'{propertyName}' must be an absolute URI.", propertyName);
        }
    }

    /// <summary>
    /// Expands environment-variable tokens in <see cref="ModelCacheDirectory"/>, normalizes
    /// separators, and returns an absolute path, mirroring
    /// <see cref="EmbeddingOptions.ResolveModelCacheDirectory"/>'s handling of the same
    /// <c>%LOCALAPPDATA%</c> token.
    /// </summary>
    public string ResolveModelCacheDirectory() => ResolvePath(ModelCacheDirectory);

    /// <summary>
    /// Expands and returns the shared parent directory every llm_router model's cache directory lives
    /// under, mirroring <see cref="ResolveModelCacheDirectory"/>'s <c>%LOCALAPPDATA%</c> handling. A
    /// per-model override resolves its own cache directory as a subdirectory of this root.
    /// </summary>
    public static string ResolveModelsRootDirectory() => ResolvePath(ModelsRootDirectory);

    private static string ResolvePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);

        if (expanded.Contains(LocalAppDataToken, StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = AppContext.BaseDirectory;
            }

            localAppData = localAppData.TrimEnd('/', '\\');
            expanded = expanded.Replace(LocalAppDataToken, localAppData, StringComparison.OrdinalIgnoreCase);
        }

        expanded = expanded.Replace('\\', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded);
    }
}
