using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
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

    /// <summary>The realistic MSI installer asset name a release publishes.</summary>
    private const string MsiAssetName = "TotallyHotArcRouter-1.2.3.msi";

    private static string Asset(string name) =>
        $$"""{"name": "{{name}}", "browser_download_url": "https://example.test/{{name}}"}""";

    private static string ReleasePayload(
        string tag,
        string assetName = MsiAssetName,
        bool includeChecksums = true,
        bool includeMsiAsset = true)
    {
        var assets = string.Empty;
        if (includeMsiAsset)
        {
            assets = Asset(assetName);
        }

        if (includeChecksums)
        {
            assets = assets.Length == 0 ? Asset("checksums.txt") : assets + "," + Asset("checksums.txt");
        }

        return $$"""
            {"tag_name": "{{tag}}", "assets": [{{assets}}]}
            """;
    }

    /// <summary>A well-formed <c>checksums.txt</c> body listing the MSI, in the <c>sha256sum</c> output format.</summary>
    private static string ChecksumsBody(string msiSha = "abc123def456") =>
        $"{msiSha}  {MsiAssetName}\n";

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
        Assert.Equal($"https://example.test/{MsiAssetName}", result.AssetDownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_NonMsiNonChecksumAssetsAreIgnored()
    {
        // A release's Source code (zip)/other assets must not be mistaken for the installer.
        var payload = $$"""
            {"tag_name": "v2.5.0", "assets": [{{Asset("Source code (zip)")}},{{Asset(MsiAssetName)}},{{Asset("checksums.txt")}}]}
            """;
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal)
                ? PlainText(ChecksumsBody())
                : Json(payload));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal($"https://example.test/{MsiAssetName}", result.AssetDownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_NewerButNoMsiAsset_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(_ => Json(ReleasePayload("v9.0.0", includeMsiAsset: false)));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.AssetOrChecksumMissing, result.UnavailableReason);
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
    public async Task CheckAsync_NewerButNoAssets_ReportsAssetOrChecksumMissing()
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
    public async Task CheckAsync_ChecksumsFileMissingTheMsiEntry_ReportsAssetOrChecksumMissing()
    {
        var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal)
                ? PlainText("deadbeef  some-other-file.msi\n")
                : Json(ReleasePayload("v9.0.0")));

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.AssetOrChecksumMissing, result.UnavailableReason);
        Assert.Contains(MsiAssetName, result.UnavailableDetail!, StringComparison.Ordinal);
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
    [InlineData("abc123  file.msi", "file.msi", "abc123")]
    [InlineData("ABC123  file.msi", "file.msi", "abc123")]
    [InlineData("abc123 *file.msi", "file.msi", "abc123")]
    [InlineData("abc123  other.msi", "file.msi", null)]
    public void ParseChecksum_VariousFormats_ResolvesExpected(string checksumsText, string assetName, string? expected)
    {
        var result = GitHubReleaseCheckClient.ParseChecksum(checksumsText, assetName);

        Assert.Equal(expected, result);
    }
}
