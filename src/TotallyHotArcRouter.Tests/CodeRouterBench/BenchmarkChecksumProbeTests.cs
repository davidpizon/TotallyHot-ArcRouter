using System.Net;
using System.Text;
using TotallyHot.ArcRouter.CodeRouterBench;
using Microsoft.Extensions.Logging.Abstractions;

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
                Content = new StringContent(TreeJson, Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-Repo-Commit", "abc123");
            return response;
        });

        var result = await probe.FetchAsync("main", TestContext.Current.CancellationToken);

        Assert.Equal("abc123", result.RepoCommit);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal("aaaa000000000000000000000000000000aaaa", result.Files["models.json"].PublishedOid);
        Assert.Equal(1417, result.Files["models.json"].Size);
    }

    [Fact]
    public async Task FetchAsync_SkipsNonFileEntries()
    {
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TreeJson, Encoding.UTF8, "application/json"),
        });

        var result = await probe.FetchAsync("main", TestContext.Current.CancellationToken);

        Assert.False(result.Files.ContainsKey("raw_matrices"));
    }

    [Fact]
    public async Task FetchAsync_NoRepoCommitHeader_FallsBackToTheRequestedRef()
    {
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TreeJson, Encoding.UTF8, "application/json"),
        });

        var result = await probe.FetchAsync("main", TestContext.Current.CancellationToken);

        Assert.Equal("main", result.RepoCommit);
    }

    [Fact]
    public async Task FetchAsync_NonSuccessStatus_ThrowsHttpRequestException()
    {
        var probe = CreateProbe(FakeHttpMessageHandler.AlwaysFails());

        await Assert.ThrowsAsync<HttpRequestException>(
            () => probe.FetchAsync("main", TestContext.Current.CancellationToken));
    }

    private static BenchmarkChecksumProbe CreateProbe(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        CreateProbe(new FakeHttpMessageHandler(respond));

    private static BenchmarkChecksumProbe CreateProbe(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<BenchmarkChecksumProbe>.Instance);
}
