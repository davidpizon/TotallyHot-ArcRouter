namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Runtime (not appsettings-bound) configuration for the Local Proxy CLI: when
/// <see cref="ForcedModelName"/> is set (via the <c>--model &lt;name&gt;</c> command-line flag - see
/// <c>Program.CreateHostBuilder</c>), every request is routed to that one already-configured model
/// (a <c>ModelRouting:ModelList[].ModelName</c> entry) regardless of what <c>model</c> field the client
/// sends, mirroring LiteLLM's <c>litellm --model provider/name</c> single-model serving mode.
/// <see
///     langword="null"/>
/// (the default - no CLI flag given) means normal multi-model routing, unaffected.
/// </summary>
public sealed class SingleModelServingOptions
{
    /// <summary>
    /// Gets the client-facing model name (matching a configured <c>ModelRouting:ModelList[].ModelName</c>
    /// entry) every request is force-routed to, or <see langword="null"/> for normal multi-model routing.
    /// </summary>
    public string? ForcedModelName { get; init; }
}