namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// The fixed set of files the llm_router ONNX voter needs, shared by
/// <see cref="LlmRouterModelOverrideStore"/>'s seed validation, <see cref="LlmRouterModelSyncService"/>'s
/// download loop, and <see cref="LlmRouterModelAdminGrpcService"/>'s status projection, so the file list
/// can never drift between them.
/// </summary>
public static class LlmRouterModelFiles
{
    /// <summary>The external-weights file name; see <see cref="IsOptional"/> for why it alone may be absent.</summary>
    public const string ModelOnnxDataFileName = "model.onnx.data";

    /// <summary>Every file's name, in fixed download order (config and tokenizer metadata before the graph and its weights).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "genai_config.json",
        "tokenizer.json",
        "tokenizer_config.json",
        "model.onnx",
        ModelOnnxDataFileName,
    ];

    /// <summary>
    /// Whether a 404 for <paramref name="fileName"/> is an expected, non-fatal outcome rather than a
    /// download/sync failure. Only <see cref="ModelOnnxDataFileName"/> qualifies: some exports inline all
    /// weights directly in <c>model.onnx</c>'s protobuf and simply never publish a separate external-data
    /// file (mirroring <see cref="TotallyHot.ArcRouter.Models.LlmRouterOptions.ModelOnnxDataUrl"/>'s own
    /// nullability for the options-configured, non-override path).
    /// </summary>
    /// <param name="fileName">One of the entries in <see cref="All"/>.</param>
    public static bool IsOptional(string fileName) =>
        string.Equals(fileName, ModelOnnxDataFileName, StringComparison.Ordinal);
}
