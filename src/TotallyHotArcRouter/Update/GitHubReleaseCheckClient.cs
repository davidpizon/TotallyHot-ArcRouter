using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Checks <c>GET {GitHubApiBaseUrl}/repos/{owner}/{repo}/releases/latest</c> for a Router release newer
/// than the one currently running (docs/router/auto-update-plan.md Phase 2, packaging superseded by
/// docs/router/packaging-and-distribution.md). Owner/repo default to <c>Directory.Build.props</c>'
/// <c>UpdateGitHubOwner</c>/<c>UpdateGitHubRepo</c>, compiled into this assembly's
/// <see cref="AssemblyMetadataAttribute"/>s (see <c>TotallyHotArcRouter.csproj</c>'s <c>ItemGroup</c>) so
/// they can never drift from the single source of truth; overridable via <see cref="UpdateOptions"/> for
/// testability.
/// </summary>
/// <remarks>
/// <b>Checksum-publishing convention.</b> A release must publish exactly two assets for this client to
/// consider it installable: one <c>.msi</c> installer asset, and one asset named exactly
/// <see cref="ChecksumsAssetName"/> (<c>checksums.txt</c>) containing a
/// <c>&lt;sha256 hex&gt; &lt;two spaces&gt; &lt;filename&gt;</c> line for it (the conventional
/// <c>sha256sum</c> output format). A release missing either, or missing the MSI's checksum line, is
/// reported as <see cref="ReleaseCheckUnavailableReason.AssetOrChecksumMissing"/>.
/// </remarks>
public sealed class GitHubReleaseCheckClient : IReleaseCheckClient
{
    /// <summary>The asset GitHub Releases must publish alongside the MSI: one <c>sha256sum</c>-format line for it.</summary>
    public const string ChecksumsAssetName = "checksums.txt";

    private const string UserAgent = "TotallyHotArcRouter-Router";
    private readonly string _currentVersion;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubReleaseCheckClient> _logger;
    private readonly UpdateOptions _options;
    private readonly string _owner;
    private readonly string _repo;

    /// <summary>Initializes a new instance of the <see cref="GitHubReleaseCheckClient"/> class.</summary>
    /// <param name="httpClient">The HTTP client used to call the GitHub API.</param>
    /// <param name="options">Auto-update configuration, including the API base URL override.</param>
    /// <param name="logger">The logger.</param>
    public GitHubReleaseCheckClient(HttpClient httpClient, IOptions<UpdateOptions> options,
        ILogger<GitHubReleaseCheckClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        var assembly = Assembly.GetExecutingAssembly();
        _owner = ReadMetadata(assembly: assembly, key: "UpdateGitHubOwner") ?? "davidpizon";
        _repo = ReadMetadata(assembly: assembly, key: "UpdateGitHubRepo") ?? "TotallyHot-ArcRouter";

        // The SDK appends "+<git-commit-sha>" (IncludeSourceRevisionInInformationalVersion, on by
        // default for a git checkout) onto InformationalVersion, which System.Version cannot parse at
        // all. Strip it - Directory.Build.props' Version is always the plain "<major>.<minor>.<patch>"
        // this comparison needs.
        var informationalVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plusIndex = informationalVersion.IndexOf('+', comparisonType: StringComparison.Ordinal);
        _currentVersion = plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }

    /// <inheritdoc/>
    public async Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            method: HttpMethod.Get,
            requestUri: $"{_options.GitHubApiBaseUrl.TrimEnd('/')}/repos/{_owner}/{_repo}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(productName: UserAgent, productVersion: "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request: request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(exception: ex, message: "GitHub release check failed to reach the API.");
            return ReleaseCheckResult.Unavailable(currentVersion: _currentVersion,
                reason: ReleaseCheckUnavailableReason.NetworkOrApiFailure, detail: ex.Message);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                // GitHub returns 404 for /releases/latest on a repo with zero published releases.
                return ReleaseCheckResult.Unavailable(
                    currentVersion: _currentVersion,
                    reason: ReleaseCheckUnavailableReason.NoReleasesPublished,
                    detail: "No releases have been published yet.");

            if (!response.IsSuccessStatusCode)
                return ReleaseCheckResult.Unavailable(
                    currentVersion: _currentVersion,
                    reason: ReleaseCheckUnavailableReason.NetworkOrApiFailure,
                    detail: $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.");

            JsonDocument document;
            try
            {
                await using var stream =
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                document = await JsonDocument.ParseAsync(utf8Json: stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(exception: ex, message: "GitHub release check received a malformed response body.");
                return ReleaseCheckResult.Unavailable(currentVersion: _currentVersion,
                    reason: ReleaseCheckUnavailableReason.NetworkOrApiFailure, detail: ex.Message);
            }

            using (document)
            {
                return await ParseReleaseAsync(release: document.RootElement, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Parses one <c>/releases/latest</c> JSON payload into a result, never throwing on a malformed shape.</summary>
    private async Task<ReleaseCheckResult> ParseReleaseAsync(JsonElement release, CancellationToken cancellationToken)
    {
        if (!release.TryGetProperty(propertyName: "tag_name", value: out var tagElement) ||
            tagElement.ValueKind != JsonValueKind.String)
            return ReleaseCheckResult.Unavailable(currentVersion: _currentVersion,
                reason: ReleaseCheckUnavailableReason.MalformedTag, detail: "Release has no tag_name.");

        var tag = tagElement.GetString() ?? string.Empty;
        var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

        if (!Version.TryParse(input: versionText, result: out var latestVersion))
            return ReleaseCheckResult.Unavailable(
                currentVersion: _currentVersion,
                reason: ReleaseCheckUnavailableReason.MalformedTag,
                detail: $"Release tag '{tag}' is not a parseable 'v<version>'.");

        if (!Version.TryParse(input: _currentVersion, result: out var currentVersion))
            // The running app's own version is always Directory.Build.props' Version, which is always
            // well-formed; this branch exists only so a corrupted build metadata attribute degrades to
            // "unavailable" rather than throwing.
            return ReleaseCheckResult.Unavailable(
                currentVersion: _currentVersion,
                reason: ReleaseCheckUnavailableReason.MalformedTag,
                detail: $"The running version '{_currentVersion}' is not a parseable version.");

        var isNewer = latestVersion > currentVersion;

        if (!release.TryGetProperty(propertyName: "assets", value: out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
            return ReleaseCheckResult.Unavailable(
                currentVersion: _currentVersion,
                reason: ReleaseCheckUnavailableReason.AssetOrChecksumMissing,
                detail: "Release has no assets array.");

        string? msiAssetUrl = null;
        string? msiAssetName = null;
        string? checksumsUrl = null;

        foreach (var asset in assetsElement.EnumerateArray())
        {
            if (!asset.TryGetProperty(propertyName: "name", value: out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String) continue;

            var name = nameElement.GetString() ?? string.Empty;
            var downloadUrl = asset.TryGetProperty(propertyName: "browser_download_url", value: out var urlElement) &&
                              urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;

            if (downloadUrl is null) continue;

            if (string.Equals(a: name, b: ChecksumsAssetName, comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                checksumsUrl = downloadUrl;
                continue;
            }

            if (name.EndsWith(value: ".msi", comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                msiAssetUrl = downloadUrl;
                msiAssetName = name;
            }
        }

        if (msiAssetUrl is null || checksumsUrl is null)
            return ReleaseCheckResult.Unavailable(
                currentVersion: _currentVersion,
                reason: ReleaseCheckUnavailableReason.AssetOrChecksumMissing,
                detail: $"Release '{tag}' does not publish an installer .msi asset and '{ChecksumsAssetName}'.");

        if (!isNewer)
            return ReleaseCheckResult.Resolved(currentVersion: _currentVersion, latestVersion: versionText, false, null,
                null);

        return await ResolveWithChecksumAsync(versionText: versionText, assetUrl: msiAssetUrl, assetName: msiAssetName!,
            checksumsUrl: checksumsUrl, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads <c>checksums.txt</c> once and looks up the MSI's published SHA256, completing the result only when
    /// both pieces an apply needs are known.
    /// </summary>
    private async Task<ReleaseCheckResult> ResolveWithChecksumAsync(
        string versionText,
        string assetUrl,
        string assetName,
        string checksumsUrl,
        CancellationToken cancellationToken)
    {
        string checksumsText;
        try
        {
            checksumsText = await _httpClient
                .GetStringAsync(requestUri: checksumsUrl, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(exception: ex, message: "Failed to download {ChecksumsAssetName} for release {Version}.",
                ChecksumsAssetName, versionText);
            return ReleaseCheckResult.Unavailable(currentVersion: _currentVersion,
                reason: ReleaseCheckUnavailableReason.NetworkOrApiFailure, detail: ex.Message);
        }

        var sha256 = ParseChecksum(checksumsText: checksumsText, assetName: assetName);
        if (sha256 is null)
            return ReleaseCheckResult.Unavailable(
                currentVersion: _currentVersion,
                reason: ReleaseCheckUnavailableReason.AssetOrChecksumMissing,
                detail: $"'{ChecksumsAssetName}' does not list a checksum for '{assetName}'.");

        return ReleaseCheckResult.Resolved(
            currentVersion: _currentVersion,
            latestVersion: versionText,
            true,
            assetDownloadUrl: assetUrl,
            assetSha256: sha256);
    }

    /// <summary>
    /// Parses <c>&lt;sha256&gt;  &lt;filename&gt;</c> lines (the <c>sha256sum</c> output format) looking for
    /// <paramref name="assetName"/>.
    /// </summary>
    internal static string? ParseChecksum(string checksumsText, string assetName)
    {
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(separator: (char[]?)null, options: StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;

            // sha256sum output may prefix the filename with "*" for binary mode.
            var fileName = parts[1].TrimStart('*');
            if (string.Equals(a: fileName, b: assetName, comparisonType: StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }

        return null;
    }

    /// <summary>Reads one named value out of this assembly's compiled-in <see cref="AssemblyMetadataAttribute"/>s.</summary>
    private static string? ReadMetadata(Assembly assembly, string key)
    {
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(a: attribute.Key, b: key, comparisonType: StringComparison.Ordinal))
            ?.Value;
    }
}