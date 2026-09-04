using System.Collections.ObjectModel;

namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Defines shared routing constants used by configuration and decision contracts.
/// </summary>
public static class RouterConstants
{
    /// <summary>
    /// Gets the default backend model identifier.
    /// </summary>
    public const string DefaultModel = "kimi-k2.5";

    /// <summary>
    /// Gets the standard rationale value used for fallback decisions.
    /// </summary>
    public const string FallbackReason = "fallback";

    /// <summary>
    /// Gets the ordered set of supported candidate models.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedModels = new ReadOnlyCollection<string>(
    [
        "claude-opus-4.6",
        "claude-sonnet-4.6",
        "gpt-5.4",
        "qwen3-max",
        "qwen3.5-plus",
        "glm-5",
        "kimi-k2.5",
        "minimax-m2.7",
        "gemini-2.5-pro",
        "gemini-2.5-flash"
    ]);
}