using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Router.TextGeneration;

/// <summary>
/// Covers <see cref="LlmRouterModelChecksumProbe"/>: URL parsing into the Hugging Face models tree API
/// call, and graceful (never-throwing) fallback when the URL isn't recognized or the API call fails.
/// </summary>
public sealed class LlmRouterModelChecksumProbeTests
{
    private const string TreeJson = """
        [
          { "type": "file", "path": "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/genai_config.json", "oid": "aaaa000000000000000000000000000000aaaa", "size": 1417 },
          { "type": "file", "path": "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/model.onnx", "oid": "bbbb000000000000000000000000000000bbbb", "size": 512000 },
          { "type": "directory", "path": "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/subdir", "oid": "cccc000000000000000000000000000000cccc", "size": 0 }
        ]
        """;

    private const string BaseUrl =
        "https://huggingface.co/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";

    [Fact]
    public async Task TryFetchAsync_RecognizedUrl_ParsesFileEntries_KeyedByFileName()
    {
        HttpRequestMessage? capturedRequest = null;
        var probe = CreateProbe(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TreeJson, Encoding.UTF8, "application/json"),
            };
        });

        var result = await probe.TryFetchAsync(BaseUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal("aaaa000000000000000000000000000000aaaa", result.Files["genai_config.json"].PublishedOid);
        Assert.Equal(1417, result.Files["genai_config.json"].Size);
        Assert.Equal(PublishedChecksumAlgorithm.GitBlobSha1, result.Files["genai_config.json"].Algorithm);
        Assert.NotNull(capturedRequest);
        Assert.Equal(
            "https://huggingface.co/api/models/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/tree/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
            capturedRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task TryFetchAsync_LfsTrackedEntry_UsesLfsOidSizeAndAlgorithm()
    {
        // model.onnx and model.onnx.data are almost always Git LFS-tracked - their top-level "oid"/"size"
        // describe the small pointer file, not the real content; only the nested "lfs" object's oid (a
        // SHA-256) and size will actually match a downloaded copy.
        const string treeJsonWithLfsEntry = """
            [
              {
                "type": "file",
                "path": "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/model.onnx",
                "oid": "bbbb000000000000000000000000000000bbbb",
                "size": 134,
                "lfs": { "oid": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", "size": 512000, "pointerSize": 134 }
              }
            ]
            """;
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(treeJsonWithLfsEntry, Encoding.UTF8, "application/json"),
        });

        var result = await probe.TryFetchAsync(BaseUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var file = result.Files["model.onnx"];
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", file.PublishedOid);
        Assert.Equal(512000, file.Size);
        Assert.Equal(PublishedChecksumAlgorithm.LfsSha256, file.Algorithm);
    }

    [Fact]
    public async Task TryFetchAsync_SkipsNonFileEntries()
    {
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TreeJson, Encoding.UTF8, "application/json"),
        });

        var result = await probe.TryFetchAsync(BaseUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.Files.ContainsKey("subdir"));
    }

    [Fact]
    public async Task TryFetchAsync_NonHuggingFaceUrl_ReturnsNull_WithoutCallingHttp()
    {
        var called = false;
        var probe = CreateProbe(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await probe.TryFetchAsync("https://example.com/some/model/folder", TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(called);
    }

    [Fact]
    public async Task TryFetchAsync_UrlNotResolveShaped_ReturnsNull()
    {
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await probe.TryFetchAsync("https://huggingface.co/some-org/some-repo/blob/main/file.json", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryFetchAsync_NonSuccessStatus_ReturnsNull_DoesNotThrow()
    {
        var probe = CreateProbe(FakeHttpMessageHandler.AlwaysFails());

        var result = await probe.TryFetchAsync(BaseUrl, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryFetchAsync_SameLeafNameInSubdirectory_DoesNotOverwriteFolderLevelEntry()
    {
        const string treeJsonWithSubdirCollision = """
            [
              { "type": "file", "path": "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/model.onnx", "oid": "aaaa000000000000000000000000000000aaaa", "size": 512000 },
              { "type": "file", "path": "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/subdir/model.onnx", "oid": "bbbb000000000000000000000000000000bbbb", "size": 999999 }
            ]
            """;
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(treeJsonWithSubdirCollision, Encoding.UTF8, "application/json"),
        });

        var result = await probe.TryFetchAsync(BaseUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("aaaa000000000000000000000000000000aaaa", result.Files["model.onnx"].PublishedOid);
    }

    [Fact]
    public async Task TryFetchAsync_RefContainsEncodedSlash_EscapesExactlyOnce()
    {
        HttpRequestMessage? capturedRequest = null;
        var probe = CreateProbe(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TreeJson, Encoding.UTF8, "application/json"),
            };
        });

        // A ref like "refs/heads/feature/x" arrives from Uri.AbsolutePath already percent-encoded
        // (refs%2Fheads%2Ffeature%2Fx) as a single path segment. Escaping it again would turn "%2F" into
        // "%252F" and 404 against the real API.
        var baseUrl = "https://huggingface.co/some-org/some-repo/resolve/refs%2Fheads%2Ffeature%2Fx/sub dir";

        var result = await probe.TryFetchAsync(baseUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(capturedRequest);
        Assert.Equal(
            "https://huggingface.co/api/models/some-org/some-repo/tree/refs%2Fheads%2Ffeature%2Fx/sub%20dir",
            capturedRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task TryFetchAsync_FolderNameContainsUrlEscapedCharacters_StillMatchesDecodedTreePaths()
    {
        const string treeJsonWithEscapedFolder = """
            [
              { "type": "file", "path": "cpu_and_mobile/sub dir/genai_config.json", "oid": "aaaa000000000000000000000000000000aaaa", "size": 1417 }
            ]
            """;
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(treeJsonWithEscapedFolder, Encoding.UTF8, "application/json"),
        });

        // "sub dir" arrives from Uri.AbsolutePath already percent-encoded as "sub%20dir", but the tree
        // API's JSON path values are decoded repo paths ("sub dir"). Comparing the still-encoded prefix
        // against the decoded entry path must not silently drop every file in the folder.
        var baseUrl = "https://huggingface.co/some-org/some-repo/resolve/main/cpu_and_mobile/sub dir";

        var result = await probe.TryFetchAsync(baseUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Single(result.Files);
        Assert.Equal("aaaa000000000000000000000000000000aaaa", result.Files["genai_config.json"].PublishedOid);
    }

    private static LlmRouterModelChecksumProbe CreateProbe(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        CreateProbe(new FakeHttpMessageHandler(respond));

    private static LlmRouterModelChecksumProbe CreateProbe(HttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<LlmRouterModelChecksumProbe>.Instance);
}
