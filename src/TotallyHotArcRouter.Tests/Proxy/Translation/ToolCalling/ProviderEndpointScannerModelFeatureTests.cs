using Moq;
using System.Net;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using static TotallyHot.ArcRouter.Tests.Proxy.ModelFeatureSupportFixtures;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Covers <see cref="ProviderEndpointScanner.ScanModelFeaturesAsync"/>
/// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1): reading each model's
/// <c>capabilities</c> from an Anthropic-shaped list, following pagination, and returning nothing at all for a list
/// it could not read completely - so a failed re-scan can never replace good records with a partial set.
/// </summary>
public sealed class ProviderEndpointScannerModelFeatureTests
{
    private static readonly ProviderOptions Provider = new() { BaseUrl = "https://api.anthropic.com" };

    private static string Page(bool hasMore, string? lastId, params string[] entries)
    {
        var last = lastId is null ? "" : $""","last_id":"{lastId}" """;
        return $$"""{"data":[{{string.Join(separator: ",", values: entries)}}],"has_more":{{(hasMore ? "true" : "false")}}{{last}}}""";
    }

    private static string Entry(string id, string? capabilities)
    {
        return capabilities is null
            ? $$"""{"type":"model","id":"{{id}}"}"""
            : $$"""{"type":"model","id":"{{id}}","capabilities":{{capabilities}}}""";
    }

    private static ProviderEndpointScanner Scanner(Func<string, (HttpStatusCode Status, string Body)> respond,
        List<string>? urls = null)
    {
        var handler = new DelegatingHandlerStub(request =>
        {
            var url = request.RequestUri!.ToString();
            urls?.Add(url);
            var (status, body) = respond(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        });

        return new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());
    }

    private static Task<IReadOnlyList<ModelFeatureSupport>?> Scan(ProviderEndpointScanner scanner,
        ProviderOptions? provider = null)
    {
        return scanner.ScanModelFeaturesAsync(providerKey: "anthropic", provider: provider ?? Provider,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnePage_RecordsEveryModelWithCapabilities_AsksForTheLargestPage()
    {
        var urls = new List<string>();
        var scanner = Scanner(_ => (HttpStatusCode.OK, Page(false, null,
            Entry("claude-haiku-4-5-20251001", Haiku45Capabilities),
            Entry("claude-without-record", null),
            Entry("claude-null-record", "null"))), urls);

        var records = await Scan(scanner);

        var record = Assert.Single(records!);
        Assert.Equal(expected: "anthropic", actual: record.ProviderKey);
        Assert.Equal(expected: "claude-haiku-4-5-20251001", actual: record.ModelId);
        Assert.False(record.IsSupported("thinking", "types", "adaptive"));
        Assert.Equal(expected: "https://api.anthropic.com/v1/models?limit=1000", actual: Assert.Single(urls));
    }

    [Fact]
    public async Task UnknownFieldsInTheResponse_AreIgnored()
    {
        // The live 2026-10-02 response carried "line", which the API reference does not list.
        var scanner = Scanner(_ => (HttpStatusCode.OK, Page(false, null,
            $$"""{"type":"model","id":"claude-haiku-4-5-20251001","line":"haiku","capabilities":{{Haiku45Capabilities}}}""")));

        Assert.Single((await Scan(scanner))!);
    }

    [Fact]
    public async Task SeveralPages_AreFollowedWithAfterId()
    {
        var urls = new List<string>();
        var scanner = Scanner(url => url.Contains("after_id=page-1-last")
            ? (HttpStatusCode.OK, Page(false, null, Entry("claude-b", FullCapabilities)))
            : (HttpStatusCode.OK, Page(true, "page-1-last", Entry("claude-a", Haiku45Capabilities))), urls);

        var records = await Scan(scanner);

        Assert.Equal(expected: ["claude-a", "claude-b"], actual: records!.Select(r => r.ModelId));
        Assert.Equal(expected: "https://api.anthropic.com/v1/models?limit=1000&after_id=page-1-last", actual: urls[1]);
    }

    [Fact]
    public async Task ALaterPageThatFails_VoidsTheWholeScan()
    {
        var scanner = Scanner(url => url.Contains("after_id")
            ? (HttpStatusCode.InternalServerError, "{}")
            : (HttpStatusCode.OK, Page(true, "x", Entry("claude-a", Haiku45Capabilities))));

        Assert.Null(await Scan(scanner));
    }

    [Fact]
    public async Task MorePagesWithoutALastId_VoidsTheScan()
    {
        var scanner = Scanner(_ => (HttpStatusCode.OK, Page(true, null, Entry("claude-a", Haiku45Capabilities))));

        Assert.Null(await Scan(scanner));
    }

    [Fact]
    public async Task AListThatNeverEnds_IsAbandonedAsIncomplete()
    {
        var urls = new List<string>();
        var scanner = Scanner(_ => (HttpStatusCode.OK, Page(true, "again", Entry("claude-a", Haiku45Capabilities))),
            urls);

        Assert.Null(await Scan(scanner));
        Assert.Equal(10, actual: urls.Count);
    }

    [Theory]
    [InlineData("""{"type":"model","capabilities":{}}""")]
    [InlineData("""{"type":"model","id":42,"capabilities":{}}""")]
    [InlineData("""{"type":"model","id":"  ","capabilities":{}}""")]
    [InlineData("""{"type":"model","id":"claude-x","capabilities":"yes"}""")]
    [InlineData("\"claude-x\"")]
    public async Task OneMalformedEntry_VoidsTheScan_SoTheStoredRecordsAreKept(string malformed)
    {
        // The caller replaces the provider's whole stored set with the scan's result. Skipping a bad entry would
        // silently delete a valid record; returning null keeps the previous set (Copilot review on PR #186).
        var scanner = Scanner(_ => (HttpStatusCode.OK, Page(false, null,
            Entry("claude-haiku-4-5-20251001", Haiku45Capabilities), malformed)));

        Assert.Null(await Scan(scanner));
    }

    [Theory]
    [InlineData("""{"object":"list","data":[{"id":"gpt-5.4"}]}""")]
    [InlineData("not json")]
    [InlineData("""{"has_more":false}""")]
    public async Task ABodyThatIsNotAnAnthropicModelList_ReturnsNull(string body)
    {
        Assert.Null(await Scan(Scanner(_ => (HttpStatusCode.OK, body))));
    }

    [Fact]
    public async Task AnUnreachableEndpoint_ReturnsNull()
    {
        var handler = new DelegatingHandlerStub(_ => throw new HttpRequestException("connection refused"));
        var scanner = new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.Null(await Scan(scanner));
    }

    [Fact]
    public async Task AnInvalidBaseUrl_ReturnsNullWithoutARequest()
    {
        var urls = new List<string>();
        var scanner = Scanner(_ => (HttpStatusCode.OK, Page(false, null)), urls);

        Assert.Null(await Scan(scanner, new ProviderOptions { BaseUrl = "not a url" }));
        Assert.Empty(urls);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}
