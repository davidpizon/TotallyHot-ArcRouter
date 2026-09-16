using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using TotallyHot.ArcRouter.Update;

namespace TotallyHot.ArcRouter.Tests.Update;

/// <summary>
/// Covers <see cref="GitHubReleaseCheckClient"/>'s version-comparison and checksum-resolution edge cases
/// against a faked <see cref="HttpMessageHandler"/> - no real network calls.
/// </summary>
/// <remarks>
/// <see cref="GitHubReleaseCheckClient.MatchesCurrentPlatform"/> branches on the OS/architecture this
/// process is actually running on (web GUI migration plan Phase P9/P10), so every asset name below is
/// resolved by <see cref="PlatformAssetName"/> - a test-side mirror of that same branching - rather than
/// a hardcoded <c>.msi</c> name. A hardcoded MSI name is exactly what this file originally used, on the
/// assumption every test host is Windows; that assumption broke the first time this suite actually ran
/// on this repo's own Linux CI job, failing five tests that all expected an MSI-shaped asset to be
/// selected/rejected on a runner that (correctly, per production logic) expects a
/// <c>totallyhotarcrouter-&lt;rid&gt;.tar.gz</c> instead.
/// </remarks>
public sealed class GitHubReleaseCheckClientTests
{
    /// <summary>The MSI installer asset name a Windows release publishes - Windows-specific, not
    /// necessarily this test host's own platform asset; see <see cref="PlatformAssetName"/> for that.</summary>
    private const string MsiAssetName = "TotallyHotArcRouter-1.2.3.msi";

    /// <summary>Every platform-specific asset name a release might publish, MSI included.</summary>
    private static readonly string[] AllPlatformAssetNames =
    [
        MsiAssetName,
        "totallyhotarcrouter-1.2.3-linux-x64.tar.gz",
        "totallyhotarcrouter-1.2.3-linux-arm64.tar.gz",
        "totallyhotarcrouter-1.2.3-osx-arm64.tar.gz",
    ];

    /// <summary>
    /// The asset name <see cref="GitHubReleaseCheckClient.MatchesCurrentPlatform"/> would select on
    /// *this* test-running process's own OS/architecture - mirrors that method's branching exactly, so
    /// this suite passes on any CI runner (Windows, Linux x64/arm64, macOS) rather than only Windows.
    /// </summary>
    private static readonly string PlatformAssetName = ResolvePlatformAssetName();

    private static string ResolvePlatformAssetName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return MsiAssetName;

        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "osx-arm64"
            : RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64";

        return $"totallyhotarcrouter-1.2.3-{rid}.tar.gz";
    }

    private static GitHubReleaseCheckClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHttpMessageHandler(respond);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.test") };
        var options = Options.Create(new UpdateOptions { GitHubApiBaseUrl = "https://api.github.test" });
        return new GitHubReleaseCheckClient(httpClient: httpClient, options: options,
            logger: NullLogger<GitHubReleaseCheckClient>.Instance);
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(content: body, encoding: Encoding.UTF8, mediaType: "application/json") };
    }

    private static HttpResponseMessage PlainText(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(content: body, encoding: Encoding.UTF8, mediaType: "text/plain") };
    }

    private static string Asset(string name)
    {
        return $$"""{"name": "{{name}}", "browser_download_url": "https://example.test/{{name}}"}""";
    }

    private static string ReleasePayload(
        string tag,
        string? assetName = null,
        bool includeChecksums = true,
        bool includePlatformAsset = true)
    {
        var assets = string.Empty;
        if (includePlatformAsset) assets = Asset(assetName ?? PlatformAssetName);

        if (includeChecksums)
            assets = assets.Length == 0 ? Asset("checksums.txt") : assets + "," + Asset("checksums.txt");

        return $$"""
                 {"tag_name": "{{tag}}", "assets": [{{assets}}]}
                 """;
    }

    /// <summary>A well-formed <c>checksums.txt</c> body listing the platform asset, in the <c>sha256sum</c> output format.</summary>
    private static string ChecksumsBody(string sha = "abc123def456")
    {
        return $"{sha}  {PlatformAssetName}\n";
    }

    [Fact]
    public async Task CheckAsync_LatestEqualsCurrent_NoUpdateAvailable()
    {
        var client = CreateClient(_ => Json(ReleasePayload("v1.0.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.None, actual: result.UnavailableReason);
        Assert.Equal(expected: "1.0.0", actual: result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_LatestOlderThanCurrent_NoUpdateAvailable()
    {
        var client = CreateClient(_ => Json(ReleasePayload("v0.9.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_LatestNewerWithChecksum_UpdateAvailable()
    {
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith(value: "checksums.txt", comparisonType: StringComparison.Ordinal)
                ? PlainText(ChecksumsBody())
                : Json(ReleasePayload("v2.5.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(expected: "2.5.0", actual: result.LatestVersion);
        Assert.Equal(expected: "abc123def456", actual: result.AssetSha256);
        Assert.Equal(expected: $"https://example.test/{PlatformAssetName}", actual: result.AssetDownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_NonMsiNonChecksumAssetsAreIgnored()
    {
        // A release's Source code (zip)/other assets must not be mistaken for the installer.
        var payload = $$"""
                        {"tag_name": "v2.5.0", "assets": [{{Asset("Source code (zip)")}},{{Asset(PlatformAssetName)}},{{Asset("checksums.txt")}}]}
                        """;
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith(value: "checksums.txt", comparisonType: StringComparison.Ordinal)
                ? PlainText(ChecksumsBody())
                : Json(payload));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(expected: $"https://example.test/{PlatformAssetName}", actual: result.AssetDownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_NewerButNoPlatformAsset_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(_ => Json(ReleasePayload(tag: "v9.0.0", includePlatformAsset: false)));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.AssetOrChecksumMissing, actual: result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_ReleaseHasOnlyOtherPlatformAssets_ReportsAssetOrChecksumMissing()
    {
        // Phase P9's platform-aware selection: a release publishing only assets for OTHER platforms must
        // not be mistaken for an installable release just because *an* asset besides checksums.txt
        // exists. Deliberately excludes this test host's own PlatformAssetName from the set, so the
        // assertion holds on every CI runner (Windows, Linux x64/arm64, macOS) rather than only Windows.
        var otherPlatformAssets = AllPlatformAssetNames.Where(name => name != PlatformAssetName)
            .Select(Asset);
        var assets = string.Join(',', otherPlatformAssets) + "," + Asset("checksums.txt");
        var client = CreateClient(_ => Json($$"""{"tag_name": "v9.0.0", "assets": [{{assets}}]}"""));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.AssetOrChecksumMissing, actual: result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_ReleaseHasPlatformAssetAlongsideOtherPlatformAssets_SelectsOnlyTheMatchingOne()
    {
        // Every platform's asset sits right next to this host's own in the same release (the real shape
        // once P10's multi-platform matrix ships) - selection must still land on the matching asset's own
        // checksum, not get confused by the other assets' similarly-shaped names.
        var otherPlatformAsset = AllPlatformAssetNames.First(name => name != PlatformAssetName);
        var assets = $"{Asset(PlatformAssetName)},{Asset(otherPlatformAsset)},{Asset("checksums.txt")}";
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith(value: "checksums.txt", comparisonType: StringComparison.Ordinal)
                ? PlainText($"""
                             deadbeef00000000000000000000000000000000000000000000000000  {PlatformAssetName}
                             1111111111111111111111111111111111111111111111111111111111  {otherPlatformAsset}
                             """)
                : Json($$"""{"tag_name": "v9.0.0", "assets": [{{assets}}]}"""));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(expected: "deadbeef00000000000000000000000000000000000000000000000000",
            actual: result.AssetSha256);
        Assert.Contains(expectedSubstring: PlatformAssetName, actualString: result.AssetDownloadUrl!,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_MalformedTag_ReportsUnavailable()
    {
        var client = CreateClient(_ => Json(ReleasePayload("not-a-version")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.MalformedTag, actual: result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NoReleasesPublished_ReportsUnavailable()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.NoReleasesPublished, actual: result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NewerButNoAssets_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(_ => Json("""{"tag_name": "v9.0.0", "assets": []}"""));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.AssetOrChecksumMissing, actual: result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NewerButNoChecksumsAsset_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(_ => Json(ReleasePayload(tag: "v9.0.0", includeChecksums: false)));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.AssetOrChecksumMissing, actual: result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_ChecksumsFileMissingThePlatformAssetEntry_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith(value: "checksums.txt", comparisonType: StringComparison.Ordinal)
                ? PlainText("deadbeef  some-other-file.msi\n")
                : Json(ReleasePayload("v9.0.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.AssetOrChecksumMissing, actual: result.UnavailableReason);
        Assert.Contains(expectedSubstring: PlatformAssetName, actualString: result.UnavailableDetail!,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_ReportsUnavailable()
    {
        var client = CreateClient(_ => throw new HttpRequestException("connection refused"));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.NetworkOrApiFailure, actual: result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NonSuccessStatusCode_ReportsUnavailable()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(expected: ReleaseCheckUnavailableReason.NetworkOrApiFailure, actual: result.UnavailableReason);
    }

    [Theory]
    [InlineData("abc123  file.msi", "file.msi", "abc123")]
    [InlineData("ABC123  file.msi", "file.msi", "abc123")]
    [InlineData("abc123 *file.msi", "file.msi", "abc123")]
    [InlineData("abc123  other.msi", "file.msi", null)]
    public void ParseChecksum_VariousFormats_ResolvesExpected(string checksumsText, string assetName, string? expected)
    {
        var result = GitHubReleaseCheckClient.ParseChecksum(checksumsText: checksumsText, assetName: assetName);

        Assert.Equal(expected: expected, actual: result);
    }
}
