namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Canonical OpenAI-compatible drop-in for a default install: one base URL and
/// <c>"model": "auto"</c>. README, GitHub Release notes, and the dashboard copy-paste panel all
/// render from these helpers so the three surfaces cannot drift. Installers themselves are unchanged
/// — this is the post-install "point a client" contract, not a packaging path.
/// </summary>
public static class OpenAiCompatibleDropIn
{
    /// <summary>
    /// Placeholder API key copied into client config. LLM forwarding is not authenticated (see
    /// <c>docs/router/mcp-endpoint.md</c>); the value only satisfies clients that refuse an empty key.
    /// </summary>
    public const string PlaceholderApiKey = "not-needed";

    /// <summary>
    /// GitHub Releases page the README and release-notes preamble send a new user to. Matches
    /// <c>Directory.Build.props</c>' <c>UpdateGitHubOwner</c>/<c>UpdateGitHubRepo</c>.
    /// </summary>
    public const string LatestReleaseUrl = "https://github.com/davidpizon/TotallyHot-ArcRouter/releases/latest";

    /// <summary>
    /// Absolute GitHub blob URL for the TLS trust doc, used on the release page where a relative
    /// <c>docs/...</c> link would 404.
    /// </summary>
    private const string ClientTlsSetupUrl =
        "https://github.com/davidpizon/TotallyHot-ArcRouter/blob/main/docs/router/client-tls-setup.md";

    /// <summary>
    /// The reserved client-facing model name that asks the router to choose. Same token
    /// <see cref="RequestInterceptor.AutoSelectModelName"/> accepts on the wire, so the copy-paste
    /// snippet cannot advertise a name the proxy would reject.
    /// </summary>
    public const string Model = RequestInterceptor.AutoSelectModelName;

    /// <summary>
    /// OpenAI-compatible base URL for a default loopback install, including the <c>/v1</c> prefix
    /// OpenAI SDKs and editors expect (they append <c>/chat/completions</c> to this value, not
    /// <c>/v1/chat/completions</c>). Built from <see cref="ProxyListenerOptions.Port"/>'s default so
    /// a port change updates every snippet.
    /// </summary>
    public static string BaseUrl { get; } = FormatBaseUrl(new ProxyListenerOptions().Port);

    /// <summary>
    /// Dashboard URL for a default loopback install, built from <see cref="WebInterfaceOptions.Port"/>'s
    /// default for the same reason <see cref="BaseUrl"/> is built from the proxy port.
    /// </summary>
    public static string DashboardUrl { get; } = FormatDashboardUrl(new WebInterfaceOptions().Port);

    /// <summary>
    /// Full chat-completions URL a raw HTTP client (curl) posts to. Derived from <see cref="BaseUrl"/>
    /// rather than restated, so the path prefix cannot drift from the SDK base URL.
    /// </summary>
    public static string ChatCompletionsUrl { get; } = $"{BaseUrl}/chat/completions";

    /// <summary>
    /// Builds the OpenAI SDK/editor base URL for an explicit proxy port. Production copy uses
    /// <see cref="BaseUrl"/>; tests pass a non-default port to prove the formatter, not the default.
    /// </summary>
    /// <param name="port">Proxy listen port, the <see cref="ProxyListenerOptions.Port"/> default in production.</param>
    /// <returns>The <c>https://localhost:{port}/v1</c> base URL.</returns>
    public static string FormatBaseUrl(int port) => $"https://localhost:{port}/v1";

    /// <summary>
    /// Builds the dashboard URL for an explicit web port. Production copy uses <see cref="DashboardUrl"/>.
    /// </summary>
    /// <param name="port">Web-interface port, the <see cref="WebInterfaceOptions.Port"/> default in production.</param>
    /// <returns>The <c>https://localhost:{port}</c> dashboard URL.</returns>
    public static string FormatDashboardUrl(int port) => $"https://localhost:{port}";

    /// <summary>
    /// Two-line environment block most OpenAI-compatible CLIs accept. Newlines are <c>\n</c> (not
    /// <see cref="Environment.NewLine"/>) so a copy from Windows still pastes into a Unix shell.
    /// </summary>
    /// <returns>The <c>OPENAI_BASE_URL</c> and <c>OPENAI_API_KEY</c> export lines.</returns>
    public static string BuildEnvironmentExports() =>
        $"export OPENAI_BASE_URL={BaseUrl}\nexport OPENAI_API_KEY={PlaceholderApiKey}";

    /// <summary>
    /// Python OpenAI SDK snippet that hits the drop-in base URL with <see cref="Model"/>.
    /// </summary>
    /// <returns>A copy-pasteable Python fragment.</returns>
    public static string BuildPythonExample() =>
        $$"""
          from openai import OpenAI

          client = OpenAI(base_url="{{BaseUrl}}", api_key="{{PlaceholderApiKey}}")
          client.chat.completions.create(
              model="{{Model}}",
              messages=[{"role": "user", "content": "hello"}],
          )
          """;

    /// <summary>
    /// curl snippet that posts one chat-completions request to <see cref="ChatCompletionsUrl"/>.
    /// </summary>
    /// <returns>A copy-pasteable curl command.</returns>
    public static string BuildCurlExample() =>
        $$"""
          curl {{ChatCompletionsUrl}} \
            -H "Content-Type: application/json" \
            -d '{"model":"{{Model}}","messages":[{"role":"user","content":"hello"}]}'
          """;

    /// <summary>
    /// README "Point a client" section. Relative links, because the README is read inside the repo.
    /// </summary>
    /// <returns>GitHub-flavored markdown for <c>README.md</c>.</returns>
    public static string BuildReadmeMarkdown() =>
        BuildMarkdown(
            leadIn: $"One OpenAI-compatible base URL. Send `\"model\": \"{Model}\"` and the router picks the model.",
            tlsLink: "[client TLS setup](docs/router/client-tls-setup.md)");

    /// <summary>
    /// GitHub Release body preamble. Absolute links, because a relative <c>docs/...</c> path 404s on
    /// the release page. <c>release.yml</c> prepends the checked-in copy of this text and then appends
    /// the generated changelog.
    /// </summary>
    /// <returns>GitHub-flavored markdown for the release notes preamble.</returns>
    public static string BuildReleaseNotesMarkdown() =>
        BuildMarkdown(
            leadIn:
            "Download the installer for your OS from the assets below, then point any OpenAI-compatible client at **one** base URL and send " +
            $"`\"model\": \"{Model}\"`.",
            tlsLink: $"[client TLS setup]({ClientTlsSetupUrl})");

    /// <summary>
    /// Shared markdown body for README and release notes. Lead-in and TLS link are the only surfaces
    /// that differ; the copy-paste fences are identical so a user who learned the snippet from one
    /// place finds the same bytes on the other.
    /// </summary>
    /// <param name="leadIn">Opening sentence under the heading.</param>
    /// <param name="tlsLink">Markdown link to the TLS trust doc, relative or absolute.</param>
    /// <returns>The full section, including the heading, with LF line endings and a trailing newline.</returns>
    public static string BuildMarkdown(string leadIn, string tlsLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leadIn);
        ArgumentException.ThrowIfNullOrWhiteSpace(tlsLink);

        var markdown =
            $"""
            ## Point a client

            {leadIn}

            **Base URL**

            ```
            {BaseUrl}
            ```

            **Copy-paste**

            ```bash
            {BuildEnvironmentExports()}
            ```

            ```python
            {BuildPythonExample().TrimEnd()}
            ```

            ```bash
            {BuildCurlExample().TrimEnd()}
            ```

            Dashboard: `{DashboardUrl}`. Windows/Linux/macOS installers already trust the local CA. Docker and browsers that ignore the OS store: {tlsLink}. The `OPENAI_API_KEY` value is a placeholder — LLM forwarding is not authenticated; it only satisfies clients that refuse an empty key.
            """;

        // Raw string literals take their line endings from this source file's checkout, so a Windows
        // autocrlf checkout would emit CRLF. The checked-in README and release notes are LF, so normalize.
        markdown = markdown.ReplaceLineEndings("\n");
        return markdown.EndsWith('\n') ? markdown : markdown + "\n";
    }
}
