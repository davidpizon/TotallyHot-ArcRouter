namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Dashboard copy of the OpenAI-compatible drop-in (one base URL, <c>model: auto</c>) shown on the
/// Sessions empty state and in System Settings. Values are the default loopback install — the same
/// numbers <c>TotallyHot.ArcRouter.Proxy.OpenAiCompatibleDropIn</c> builds from
/// <c>ProxyListenerOptions</c>/<c>WebInterfaceOptions</c>. The Gui.Components library cannot
/// reference the router assembly (the router hosts this WASM dashboard), so the lockstep is a unit
/// test in the router project that greps this file for the router's formatted URLs rather than a
/// project reference.
/// </summary>
public static class OpenAiCompatibleDropIn
{
    /// <summary>
    /// OpenAI-compatible base URL for a default loopback install, including the <c>/v1</c> prefix
    /// OpenAI SDKs and editors expect.
    /// </summary>
    public const string BaseUrl = "https://localhost:47101/v1";

    /// <summary>Dashboard URL for a default loopback install.</summary>
    public const string DashboardUrl = "https://localhost:47104";

    /// <summary>
    /// Reserved client-facing model name that asks the router to choose. Same token the proxy
    /// accepts as <c>RequestInterceptor.AutoSelectModelName</c>.
    /// </summary>
    public const string Model = "auto";

    /// <summary>
    /// Placeholder API key copied into client config. LLM forwarding is not authenticated; the
    /// value only satisfies clients that refuse an empty key.
    /// </summary>
    public const string PlaceholderApiKey = "not-needed";

    /// <summary>
    /// Two-line environment block most OpenAI-compatible CLIs accept. Newlines are <c>\n</c> so a
    /// copy from any host still pastes into a Unix shell.
    /// </summary>
    /// <returns>The <c>OPENAI_BASE_URL</c> and <c>OPENAI_API_KEY</c> export lines.</returns>
    public static string BuildEnvironmentExports() =>
        $"export OPENAI_BASE_URL={BaseUrl}\nexport OPENAI_API_KEY={PlaceholderApiKey}";
}
