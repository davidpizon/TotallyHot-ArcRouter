using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="OpenAiCompatibleDropIn"/>: default URLs follow listener options, the copy-paste
/// snippets name those URLs and <c>auto</c>, and README / release-notes / GUI sources stay in lockstep
/// with the class so a port or model-name change cannot silently leave the install UX behind.
/// </summary>
public sealed class OpenAiCompatibleDropInTests
{
    [Fact]
    public void BaseUrl_uses_the_default_proxy_port_and_the_openai_v1_prefix()
    {
        var port = new ProxyListenerOptions().Port;

        Assert.Equal(expected: $"https://localhost:{port}/v1", actual: OpenAiCompatibleDropIn.BaseUrl);
        Assert.Equal(expected: OpenAiCompatibleDropIn.FormatBaseUrl(port), actual: OpenAiCompatibleDropIn.BaseUrl);
        Assert.Equal(expected: $"{OpenAiCompatibleDropIn.BaseUrl}/chat/completions",
            actual: OpenAiCompatibleDropIn.ChatCompletionsUrl);
    }

    [Fact]
    public void DashboardUrl_uses_the_default_web_interface_port()
    {
        var port = new WebInterfaceOptions().Port;

        Assert.Equal(expected: $"https://localhost:{port}", actual: OpenAiCompatibleDropIn.DashboardUrl);
        Assert.Equal(expected: OpenAiCompatibleDropIn.FormatDashboardUrl(port),
            actual: OpenAiCompatibleDropIn.DashboardUrl);
    }

    [Fact]
    public void Model_is_the_reserved_auto_select_name()
    {
        Assert.Equal(expected: RequestInterceptor.AutoSelectModelName, actual: OpenAiCompatibleDropIn.Model);
        Assert.Equal(expected: "auto", actual: OpenAiCompatibleDropIn.Model);
    }

    [Fact]
    public void Formatters_honor_an_explicit_port_so_the_default_is_not_baked_into_the_format()
    {
        Assert.Equal(expected: "https://localhost:12345/v1", actual: OpenAiCompatibleDropIn.FormatBaseUrl(12345));
        Assert.Equal(expected: "https://localhost:23456", actual: OpenAiCompatibleDropIn.FormatDashboardUrl(23456));
    }

    [Fact]
    public void Environment_python_and_curl_snippets_are_copy_paste_bytes_of_the_canonical_values()
    {
        var env = OpenAiCompatibleDropIn.BuildEnvironmentExports();
        var python = OpenAiCompatibleDropIn.BuildPythonExample();
        var curl = OpenAiCompatibleDropIn.BuildCurlExample();

        Assert.Equal(
            expected:
            $"export OPENAI_BASE_URL={OpenAiCompatibleDropIn.BaseUrl}\nexport OPENAI_API_KEY={OpenAiCompatibleDropIn.PlaceholderApiKey}",
            actual: env);
        Assert.DoesNotContain("\r", env);

        Assert.Contains($"base_url=\"{OpenAiCompatibleDropIn.BaseUrl}\"", python);
        Assert.Contains($"api_key=\"{OpenAiCompatibleDropIn.PlaceholderApiKey}\"", python);
        Assert.Contains($"model=\"{OpenAiCompatibleDropIn.Model}\"", python);

        Assert.Contains(OpenAiCompatibleDropIn.ChatCompletionsUrl, curl);
        Assert.Contains($"\"model\":\"{OpenAiCompatibleDropIn.Model}\"", curl);
    }

    [Fact]
    public void BuildMarkdown_rejects_blank_lead_in_or_tls_link()
    {
        Assert.Throws<ArgumentException>(() =>
            OpenAiCompatibleDropIn.BuildMarkdown(leadIn: " ", tlsLink: "[tls](docs/x.md)"));
        Assert.Throws<ArgumentException>(() =>
            OpenAiCompatibleDropIn.BuildMarkdown(leadIn: "Go", tlsLink: " "));
    }

    [Fact]
    public void Readme_contains_the_generated_drop_in_section_verbatim()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepoRoot(), "README.md"));

        Assert.Contains(OpenAiCompatibleDropIn.BuildReadmeMarkdown(), readme);
        Assert.Contains(OpenAiCompatibleDropIn.LatestReleaseUrl, readme);
    }

    [Fact]
    public void Release_notes_file_matches_the_generated_preamble_byte_for_byte()
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "install", "openai-compatible-drop-in.md");
        var onDisk = File.ReadAllText(path).Replace(oldValue: "\r\n", newValue: "\n",
            comparisonType: StringComparison.Ordinal);

        Assert.Equal(expected: OpenAiCompatibleDropIn.BuildReleaseNotesMarkdown(), actual: onDisk);
    }

    [Fact]
    public void Release_workflow_prepends_the_drop_in_file_without_rebuilding_assets()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("body_path: docs/install/openai-compatible-drop-in.md", workflow);
        Assert.Contains("generate_release_notes: true", workflow);
        Assert.Contains("TotallyHotArcRouter-$env:RELEASE_VERSION.msi", workflow);
        Assert.Contains("totallyhotarcrouter-${{ matrix.rid }}.tar.gz", workflow);
    }

    [Fact]
    public void Gui_drop_in_constants_stay_in_lockstep_with_the_router()
    {
        var guiSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src",
            "TotallyHotArcRouter.Gui.Components", "Services", "OpenAiCompatibleDropIn.cs"));

        Assert.Contains($"public const string BaseUrl = \"{OpenAiCompatibleDropIn.BaseUrl}\"", guiSource);
        Assert.Contains($"public const string DashboardUrl = \"{OpenAiCompatibleDropIn.DashboardUrl}\"", guiSource);
        Assert.Contains($"public const string Model = \"{OpenAiCompatibleDropIn.Model}\"", guiSource);
        Assert.Contains($"public const string PlaceholderApiKey = \"{OpenAiCompatibleDropIn.PlaceholderApiKey}\"",
            guiSource);
    }

    [Fact]
    public void Src_readme_verify_example_uses_the_drop_in_model_and_base_url()
    {
        var srcReadme = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "README.md"));

        Assert.Contains(OpenAiCompatibleDropIn.BaseUrl, srcReadme);
        Assert.Contains($"\"model\":\"{OpenAiCompatibleDropIn.Model}\"", srcReadme);
    }

    /// <summary>
    /// Walks up from the test binary to the repository root (the directory that contains both
    /// <c>README.md</c> and <c>src/</c>) so these lockstep checks do not depend on the process
    /// working directory, which differs between `dotnet test` and a direct test-host launch.
    /// </summary>
    /// <returns>The repository root path.</returns>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find the repository root above {AppContext.BaseDirectory}.");
    }
}
