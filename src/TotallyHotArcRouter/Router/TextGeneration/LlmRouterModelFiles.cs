namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// The fixed set of files the llm_router ONNX voter needs, shared by
/// <see cref="LlmRouterModelOverrideStore"/>'s seed validation, <see cref="LlmRouterModelSyncService"/>'s
/// download loop, and <see cref="LlmRouterModelAdminGrpcService"/>'s status projection, so the file list
/// can never drift between them.
/// </summary>
public static class LlmRouterModelFiles
{
    /// <summary>Every file's name, in fixed download order (config and tokenizer metadata before the graph and its weights).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "genai_config.json",
        "tokenizer.json",
        "tokenizer_config.json",
        "model.onnx",
        "model.onnx.data",
    ];
}
