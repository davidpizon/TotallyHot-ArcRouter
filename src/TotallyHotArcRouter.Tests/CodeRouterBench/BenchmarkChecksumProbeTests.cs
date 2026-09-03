using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="BenchmarkChecksumProbe"/> against a fake Hugging Face tree API response.</summary>
public class BenchmarkChecksumProbeTests
{
    private const string TreeJson = """
                                    [
                                      { "type": "file", "path": "models.json", "oid": "aaaa000000000000000000000000000000aaaa", "size": 1417 },
                                      { "type": "file", "path": "summary.json", "oid": "bbbb000000000000000000000000000000bbbb", "size": 1389 },
                                      { "type": "directory", "path": "raw_matrices", "oid": "cccc000000000000000000000000000000cccc", "size": 0 }
                                    ]
                                    """;

    [Fact]
    public async Task FetchAsync_ParsesFileEntries_KeyedByPath()
    {
        var probe = CreateProbe(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: TreeJson, encoding: Encoding.UTF8, mediaType: "application/json")
            };
            response.Headers.Add(name: "X-Repo-Commit", value: "abc123");
            return response;
        });

        var result =
            await probe.FetchAsync(datasetRef: "main", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "abc123", actual: result.RepoCommit);
        Assert.Equal(2, actual: result.Files.Count);
        Assert.Equal(expected: "aaaa000000000000000000000000000000aaaa",
            actual: result.Files["models.json"].PublishedOid);
        Assert.Equal(1417, actual: result.Files["models.json"].Size);
        Assert.Equal(expected: PublishedChecksumAlgorithm.GitBlobSha1, actual: result.Files["models.json"].Algorithm);
    }

    [Fact]
    public async Task FetchAsync_LfsTrackedEntry_UsesLfsOidSizeAndAlgorithm()
    {
        // A Git LFS-tracked entry's top-level "oid"/"size" describe the small pointer file, not the real
        // content - only the nested "lfs" object's oid (a SHA-256) and size are the ones that will
        // actually match a downloaded copy of the real file.
        const string treeJsonWithLfsEntry = """
                                            [
                                              {
                                                "type": "file",
                                                "path": "id_probing_results_long.csv",
                                                "oid": "aaaa000000000000000000000000000000aaaa",
                                                "size": 133,
                                                "lfs": { "oid": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "size": 52428800, "pointerSize": 133 }
                                              }
                                            ]
                                            """;
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: treeJsonWithLfsEntry, encoding: Encoding.UTF8,
                mediaType: "application/json")
        });

        var result =
            await probe.FetchAsync(datasetRef: "main", cancellationToken: TestContext.Current.CancellationToken);

        var file = result.Files["id_probing_results_long.csv"];
        Assert.Equal(expected: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            actual: file.PublishedOid);
        Assert.Equal(52428800, actual: file.Size);
        Assert.Equal(expected: PublishedChecksumAlgorithm.LfsSha256, actual: file.Algorithm);
    }

    [Fact]
    public async Task FetchAsync_SkipsNonFileEntries()
    {
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: TreeJson, encoding: Encoding.UTF8, mediaType: "application/json")
        });

        var result =
            await probe.FetchAsync(datasetRef: "main", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Files.ContainsKey("raw_matrices"));
    }

    [Fact]
    public async Task FetchAsync_NoRepoCommitHeader_FallsBackToTheRequestedRef()
    {
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: TreeJson, encoding: Encoding.UTF8, mediaType: "application/json")
        });

        var result =
            await probe.FetchAsync(datasetRef: "main", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "main", actual: result.RepoCommit);
    }

    [Fact]
    public async Task FetchAsync_NonSuccessStatus_ThrowsHttpRequestException()
    {
        var probe = CreateProbe(FakeHttpMessageHandler.AlwaysFails());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            probe.FetchAsync(datasetRef: "main", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static BenchmarkChecksumProbe CreateProbe(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        return CreateProbe(new FakeHttpMessageHandler(respond));
    }

    private static BenchmarkChecksumProbe CreateProbe(HttpMessageHandler handler)
    {
        return new BenchmarkChecksumProbe(httpClientFactory: new FakeHttpClientFactory(handler),
            logger: NullLogger<BenchmarkChecksumProbe>.Instance);
    }
}