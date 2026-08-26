using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using TotallyHot.ArcRouter.Update;

namespace TotallyHot.ArcRouter.Tests.Update;

/// <summary>
/// Covers <see cref="GitHubReleaseCheckClient"/>'s version-comparison and checksum-resolution edge cases
/// against a faked <see cref="HttpMessageHandler"/> - no real network calls.
/// </summary>
public sealed class GitHubReleaseCheckClientTests
{
    private static GitHubReleaseCheckClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHttpMessageHandler(respond);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.test") };
        var options = Options.Create(new UpdateOptions { GitHubApiBaseUrl = "https://api.github.test" });
        return new GitHubReleaseCheckClient(httpClient, options, NullLogger<GitHubReleaseCheckClient>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage PlainText(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    /// <summary>The realistic Router asset name a release publishes.</summary>
    private const string RouterAssetName = "TotallyHotArcRouter-Router-win-x64.zip";

    /// <summary>
    /// The realistic Updater asset name a release publishes. Deliberately the real product name, which
    /// embeds "router" inside "TotallyHotArcRouter" - the exact collision that made a naive
    /// Contains("router") classification misfile this asset as the Router zip.
    /// </summary>
    private const string UpdaterAssetName = "TotallyHotArcRouter-Updater-win-x64.zip";

    private static string Asset(string name) =>
        $$"""{"name": "{{name}}", "browser_download_url": "https://example.test/{{name}}"}""";

    private static string ReleasePayload(
        string tag,
        string assetName = RouterAssetName,
        bool includeChecksums = true,
        bool includeUpdaterAsset = true)
    {
        var assets = Asset(assetName);
        if (includeUpdaterAsset)
        {
            assets += "," + Asset(UpdaterAssetName);
        }

        if (includeChecksums)
        {
            assets += "," + Asset("checksums.txt");
        }

        return $$"""
            {"tag_name": "{{tag}}", "assets": [{{assets}}]}
            """;
    }

    /// <summary>A well-formed <c>checksums.txt</c> body listing both zips, in the <c>sha256sum</c> output format.</summary>
    private static string ChecksumsBody(string routerSha = "abc123def456", string updaterSha = "feed0000cafe") =>
        $"{routerSha}  {RouterAssetName}\n{updaterSha}  {UpdaterAssetName}\n";

    [Fact]
    public async Task CheckAsync_LatestEqualsCurrent_NoUpdateAvailable()
    {
        var client = CreateClient(_ => Json(ReleasePayload("v1.0.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.None, result.UnavailableReason);
        Assert.Equal("1.0.0", result.LatestVersion);
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
            request.RequestUri!.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal)
                ? PlainText(ChecksumsBody())
                : Json(ReleasePayload("v2.5.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("2.5.0", result.LatestVersion);
        Assert.Equal("abc123def456", result.AssetSha256);
        Assert.Equal($"https://example.test/{RouterAssetName}", result.AssetDownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_RealisticRouterAndUpdaterAssetNames_ClassifiesEachToTheCorrectSlot()
    {
        // Regression guard: "TotallyHotArcRouter-Updater-win-x64.zip" also contains "router", so a
        // Contains("router")-first classification would file it as the Router zip (or, depending on
        // enumeration order, let it overwrite the real one) and hand the Router swap a copy of the Updater.
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal)
                ? PlainText(ChecksumsBody())
                : Json(ReleasePayload("v2.5.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal($"https://example.test/{RouterAssetName}", result.AssetDownloadUrl);
        Assert.Equal("abc123def456", result.AssetSha256);
        Assert.Equal($"https://example.test/{UpdaterAssetName}", result.UpdaterAssetDownloadUrl);
        Assert.Equal("feed0000cafe", result.UpdaterAssetSha256);
    }

    [Fact]
    public async Task CheckAsync_UpdaterAssetListedFirst_StillClassifiesEachToTheCorrectSlot()
    {
        // Same pair, reversed enumeration order - the classification must not depend on which asset the
        // release happens to list first.
        var payload = $$"""
            {"tag_name": "v2.5.0", "assets": [{{Asset(UpdaterAssetName)}},{{Asset(RouterAssetName)}},{{Asset("checksums.txt")}}]}
            """;
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal)
                ? PlainText(ChecksumsBody())
                : Json(payload));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal($"https://example.test/{RouterAssetName}", result.AssetDownloadUrl);
        Assert.Equal($"https://example.test/{UpdaterAssetName}", result.UpdaterAssetDownloadUrl);
    }

    [Theory]
    [InlineData(RouterAssetName, GitHubReleaseCheckClient.ReleaseAssetKind.Router)]
    [InlineData(UpdaterAssetName, GitHubReleaseCheckClient.ReleaseAssetKind.Updater)]
    [InlineData("checksums.txt", GitHubReleaseCheckClient.ReleaseAssetKind.Other)]
    [InlineData("TotallyHotArcRouter-Router-win-x64.zip.sig", GitHubReleaseCheckClient.ReleaseAssetKind.Other)]
    [InlineData("Source code (zip)", GitHubReleaseCheckClient.ReleaseAssetKind.Other)]
    public void ClassifyAsset_RealisticNames_ResolveToTheExpectedKind(string name, GitHubReleaseCheckClient.ReleaseAssetKind expected) =>
        Assert.Equal(expected, GitHubReleaseCheckClient.ClassifyAsset(name));

    [Fact]
    public async Task CheckAsync_NewerButNoUpdaterAsset_ReportsAssetOrChecksumMissing()
    {
        // Strict by design: an apply always refreshes the Updater first, so a release without one cannot
        // be applied - and an update that cannot be applied is never reported as available.
        var client = CreateClient(_ => Json(ReleasePayload("v9.0.0", includeUpdaterAsset: false)));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.AssetOrChecksumMissing, result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_ChecksumsFileMissingTheUpdaterEntry_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal)
                ? PlainText($"abc123def456  {RouterAssetName}\n")
                : Json(ReleasePayload("v9.0.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.AssetOrChecksumMissing, result.UnavailableReason);
        Assert.Contains(UpdaterAssetName, result.UnavailableDetail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_MalformedTag_ReportsUnavailable()
    {
        var client = CreateClient(_ => Json(ReleasePayload("not-a-version")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.MalformedTag, result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NoReleasesPublished_ReportsUnavailable()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.NoReleasesPublished, result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NewerButNoZipAsset_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(_ => Json("""{"tag_name": "v9.0.0", "assets": []}"""));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.AssetOrChecksumMissing, result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NewerButNoChecksumsAsset_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(_ => Json(ReleasePayload("v9.0.0", includeChecksums: false)));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.AssetOrChecksumMissing, result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_ChecksumsFileMissingTheAssetsEntry_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal)
                ? PlainText($"deadbeef  some-other-file.zip\nfeed0000cafe  {UpdaterAssetName}\n")
                : Json(ReleasePayload("v9.0.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.AssetOrChecksumMissing, result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_ReportsUnavailable()
    {
        var client = CreateClient(_ => throw new HttpRequestException("connection refused"));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.NetworkOrApiFailure, result.UnavailableReason);
    }

    [Fact]
    public async Task CheckAsync_NonSuccessStatusCode_ReportsUnavailable()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.NetworkOrApiFailure, result.UnavailableReason);
    }

    [Theory]
    [InlineData("abc123  file.zip", "file.zip", "abc123")]
    [InlineData("ABC123  file.zip", "file.zip", "abc123")]
    [InlineData("abc123 *file.zip", "file.zip", "abc123")]
    [InlineData("abc123  other.zip", "file.zip", null)]
    public void ParseChecksum_VariousFormats_ResolvesExpected(string checksumsText, string assetName, string? expected)
    {
        var result = GitHubReleaseCheckClient.ParseChecksum(checksumsText, assetName);

        Assert.Equal(expected, result);
    }
}
