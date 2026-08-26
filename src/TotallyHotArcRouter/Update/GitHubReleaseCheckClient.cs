using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Checks <c>GET {GitHubApiBaseUrl}/repos/{owner}/{repo}/releases/latest</c> for a Router release newer
/// than the one currently running (docs/router/auto-update-plan.md Phase 2). Owner/repo default to
/// <c>Directory.Build.props</c>' <c>UpdateGitHubOwner</c>/<c>UpdateGitHubRepo</c>, compiled into this
/// assembly's <see cref="AssemblyMetadataAttribute"/>s (see <c>TotallyHotArcRouter.csproj</c>'s
/// <c>ItemGroup</c>) so they can never drift from the single source of truth; overridable via
/// <see cref="UpdateOptions"/> for testability.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checksum-publishing convention.</b> A release must publish three assets for this client to
/// consider it installable: one <b>Updater</b> zip, one <b>Router</b> zip, and one asset named exactly
/// <see cref="ChecksumsAssetName"/> (<c>checksums.txt</c>) containing one <c>&lt;sha256 hex&gt;
/// &lt;two spaces&gt; &lt;filename&gt;</c> line per released asset (the conventional <c>sha256sum</c>
/// output format) - a single checksums file covering both zips, not one per asset. A release missing any
/// of the three, or missing a checksum line for either zip, is reported as
/// <see cref="ReleaseCheckUnavailableReason.AssetOrChecksumMissing"/> - see
/// docs/router/auto-update-plan.md's Phase 2 section for why this convention was chosen over a per-asset
/// <c>.sha256</c> sidecar file, and for what CI automation is not yet in place to publish these files
/// on tag push.
/// </para>
/// <para>
/// <b>Asset classification is most-specific-first</b> (see <see cref="ClassifyAsset"/>), and that ordering
/// is load-bearing rather than stylistic: the product name <c>TotallyHotArcRouter</c> contains
/// <c>"router"</c>, so an Updater asset named <c>TotallyHotArcRouter-Updater-win-x64.zip</c> matches a
/// naive <c>"router"</c> test as well. Testing for <c>"updater"</c> first is what keeps the two apart;
/// reversing it would silently misclassify one zip as the other depending on release-asset enumeration
/// order.
/// </para>
/// </remarks>
public sealed class GitHubReleaseCheckClient : IReleaseCheckClient
{
    /// <summary>The asset GitHub Releases must publish alongside the Router zip: one <c>sha256sum</c>-format line per asset.</summary>
    public const string ChecksumsAssetName = "checksums.txt";

    private const string UserAgent = "TotallyHotArcRouter-Updater";

    private readonly HttpClient _httpClient;
    private readonly UpdateOptions _options;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _currentVersion;
    private readonly ILogger<GitHubReleaseCheckClient> _logger;

    /// <summary>Initializes a new instance of the <see cref="GitHubReleaseCheckClient"/> class.</summary>
    /// <param name="httpClient">The HTTP client used to call the GitHub API.</param>
    /// <param name="options">Auto-update configuration, including the API base URL override.</param>
    /// <param name="logger">The logger.</param>
    public GitHubReleaseCheckClient(HttpClient httpClient, IOptions<UpdateOptions> options, ILogger<GitHubReleaseCheckClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        var assembly = Assembly.GetExecutingAssembly();
        _owner = ReadMetadata(assembly, "UpdateGitHubOwner") ?? "davidpizon";
        _repo = ReadMetadata(assembly, "UpdateGitHubRepo") ?? "TotallyHot-ArcRouter";

        // The SDK appends "+<git-commit-sha>" (IncludeSourceRevisionInInformationalVersion, on by
        // default for a git checkout) onto InformationalVersion, which System.Version cannot parse at
        // all. Strip it - Directory.Build.props' Version is always the plain "<major>.<minor>.<patch>"
        // this comparison needs.
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        _currentVersion = plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }

    /// <inheritdoc />
    public async Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_options.GitHubApiBaseUrl.TrimEnd('/')}/repos/{_owner}/{_repo}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GitHub release check failed to reach the API.");
            return ReleaseCheckResult.Unavailable(_currentVersion, ReleaseCheckUnavailableReason.NetworkOrApiFailure, ex.Message);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // GitHub returns 404 for /releases/latest on a repo with zero published releases.
                return ReleaseCheckResult.Unavailable(
                    _currentVersion,
                    ReleaseCheckUnavailableReason.NoReleasesPublished,
                    "No releases have been published yet.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ReleaseCheckResult.Unavailable(
                    _currentVersion,
                    ReleaseCheckUnavailableReason.NetworkOrApiFailure,
                    $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            JsonDocument document;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "GitHub release check received a malformed response body.");
                return ReleaseCheckResult.Unavailable(_currentVersion, ReleaseCheckUnavailableReason.NetworkOrApiFailure, ex.Message);
            }

            using (document)
            {
                return await ParseReleaseAsync(document.RootElement, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Parses one <c>/releases/latest</c> JSON payload into a result, never throwing on a malformed shape.</summary>
    private async Task<ReleaseCheckResult> ParseReleaseAsync(JsonElement release, CancellationToken cancellationToken)
    {
        if (!release.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
        {
            return ReleaseCheckResult.Unavailable(_currentVersion, ReleaseCheckUnavailableReason.MalformedTag, "Release has no tag_name.");
        }

        var tag = tagElement.GetString() ?? string.Empty;
        var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

        if (!Version.TryParse(versionText, out var latestVersion))
        {
            return ReleaseCheckResult.Unavailable(
                _currentVersion,
                ReleaseCheckUnavailableReason.MalformedTag,
                $"Release tag '{tag}' is not a parseable 'v<version>'.");
        }

        if (!Version.TryParse(_currentVersion, out var currentVersion))
        {
            // The running app's own version is always Directory.Build.props' Version, which is always
            // well-formed; this branch exists only so a corrupted build metadata attribute degrades to
            // "unavailable" rather than throwing.
            return ReleaseCheckResult.Unavailable(
                _currentVersion,
                ReleaseCheckUnavailableReason.MalformedTag,
                $"The running version '{_currentVersion}' is not a parseable version.");
        }

        var isNewer = latestVersion > currentVersion;

        if (!release.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return ReleaseCheckResult.Unavailable(
                _currentVersion,
                ReleaseCheckUnavailableReason.AssetOrChecksumMissing,
                "Release has no assets array.");
        }

        string? routerAssetUrl = null;
        string? routerAssetName = null;
        string? updaterAssetUrl = null;
        string? updaterAssetName = null;
        string? checksumsUrl = null;

        foreach (var asset in assetsElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString() ?? string.Empty;
            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;

            if (downloadUrl is null)
            {
                continue;
            }

            if (string.Equals(name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
            {
                checksumsUrl = downloadUrl;
                continue;
            }

            switch (ClassifyAsset(name))
            {
                case ReleaseAssetKind.Updater:
                    updaterAssetUrl = downloadUrl;
                    updaterAssetName = name;
                    break;
                case ReleaseAssetKind.Router:
                    routerAssetUrl = downloadUrl;
                    routerAssetName = name;
                    break;
                default:
                    break;
            }
        }

        if (routerAssetUrl is null || updaterAssetUrl is null || checksumsUrl is null)
        {
            return ReleaseCheckResult.Unavailable(
                _currentVersion,
                ReleaseCheckUnavailableReason.AssetOrChecksumMissing,
                $"Release '{tag}' does not publish a Router zip asset, an Updater zip asset, and '{ChecksumsAssetName}'.");
        }

        if (!isNewer)
        {
            return ReleaseCheckResult.Resolved(_currentVersion, versionText, isUpdateAvailable: false, assetDownloadUrl: null, assetSha256: null);
        }

        return await ResolveWithChecksumsAsync(
                versionText,
                routerAssetUrl,
                routerAssetName!,
                updaterAssetUrl,
                updaterAssetName!,
                checksumsUrl,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Downloads <c>checksums.txt</c> once and looks up both zips' published SHA256s, completing the result only when every piece an apply needs is known.</summary>
    private async Task<ReleaseCheckResult> ResolveWithChecksumsAsync(
        string versionText,
        string assetUrl,
        string assetName,
        string updaterAssetUrl,
        string updaterAssetName,
        string checksumsUrl,
        CancellationToken cancellationToken)
    {
        string checksumsText;
        try
        {
            checksumsText = await _httpClient.GetStringAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download {ChecksumsAssetName} for release {Version}.", ChecksumsAssetName, versionText);
            return ReleaseCheckResult.Unavailable(_currentVersion, ReleaseCheckUnavailableReason.NetworkOrApiFailure, ex.Message);
        }

        var sha256 = ParseChecksum(checksumsText, assetName);
        var updaterSha256 = ParseChecksum(checksumsText, updaterAssetName);
        if (sha256 is null || updaterSha256 is null)
        {
            var missing = sha256 is null ? assetName : updaterAssetName;
            return ReleaseCheckResult.Unavailable(
                _currentVersion,
                ReleaseCheckUnavailableReason.AssetOrChecksumMissing,
                $"'{ChecksumsAssetName}' does not list a checksum for '{missing}'.");
        }

        return ReleaseCheckResult.Resolved(
            _currentVersion,
            versionText,
            isUpdateAvailable: true,
            assetDownloadUrl: assetUrl,
            assetSha256: sha256,
            updaterAssetDownloadUrl: updaterAssetUrl,
            updaterAssetSha256: updaterSha256);
    }

    /// <summary>Parses <c>&lt;sha256&gt;  &lt;filename&gt;</c> lines (the <c>sha256sum</c> output format) looking for <paramref name="assetName"/>.</summary>
    internal static string? ParseChecksum(string checksumsText, string assetName)
    {
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            // sha256sum output may prefix the filename with "*" for binary mode.
            var fileName = parts[1].TrimStart('*');
            if (string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>Which of the release's two installable zips an asset name denotes, if either.</summary>
    /// <remarks>
    /// Public rather than internal so <see cref="ClassifyAsset"/> is directly theory-testable
    /// (<c>GitHubReleaseCheckClientTests.ClassifyAsset_RealisticNames_ResolveToTheExpectedKind</c>) without
    /// <c>InternalsVisibleTo</c> hitting CS0051 on the public xUnit theory method's parameter type.
    /// </remarks>
    public enum ReleaseAssetKind
    {
        /// <summary>Not one of the two installable zips (e.g. <c>checksums.txt</c>, or a GUI/source archive).</summary>
        Other = 0,

        /// <summary>The Router zip that replaces <c>...\Router\</c>.</summary>
        Router,

        /// <summary>The Updater zip that replaces <c>...\Updater\</c>.</summary>
        Updater,
    }

    /// <summary>
    /// Classifies a release asset name, testing the more specific <c>"updater"</c> substring <b>before</b>
    /// the broader <c>"router"</c> one. The order is required for correctness, not tidiness: the product
    /// name <c>TotallyHotArcRouter</c> embeds <c>"router"</c>, so <c>TotallyHotArcRouter-Updater-win-x64.zip</c>
    /// satisfies a plain <c>"router"</c> test too and would otherwise be picked up as the Router zip.
    /// </summary>
    internal static ReleaseAssetKind ClassifyAsset(string name)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseAssetKind.Other;
        }

        if (name.Contains("updater", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseAssetKind.Updater;
        }

        return name.Contains("router", StringComparison.OrdinalIgnoreCase)
            ? ReleaseAssetKind.Router
            : ReleaseAssetKind.Other;
    }

    /// <summary>Reads one named value out of this assembly's compiled-in <see cref="AssemblyMetadataAttribute"/>s.</summary>
    private static string? ReadMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;
}
