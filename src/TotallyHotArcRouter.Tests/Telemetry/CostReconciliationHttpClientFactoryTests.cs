using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Telemetry.Tokenization;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Pins the production transport for every <see cref="CostReconciliationRetryPolicy.HttpClientName"/>
/// consumer. Their behavior tests pass a stub-wrapped <see cref="HttpClient"/> directly, which never touches
/// the factory branch production actually runs, so a wrong client name or a broken factory handoff would
/// leave those tests green.
/// </summary>
public class CostReconciliationHttpClientFactoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenAiCostReconciler_FetchesThroughTheNamedClient()
    {
        var factory = JsonFactory("""{"data":[],"has_more":false}""");
        var reconciler = new OpenAiCostReconciler(httpClient: null, adminApiKey: "sk-admin-test",
            httpClientFactory: factory);

        var cost = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 1), cancellationToken: Ct);

        Assert.Equal(expected: 0m, actual: cost);
        AssertOneRequestThroughNamedClient(factory);
    }

    [Fact]
    public async Task AnthropicCostReconciler_FetchesThroughTheNamedClient()
    {
        var factory = JsonFactory("""{"data":[],"has_more":false}""");
        var reconciler = new AnthropicCostReconciler(httpClient: null, adminApiKey: "sk-ant-admin01-test",
            httpClientFactory: factory);

        var cost = await reconciler.GetReportedCostAsync(day: new DateOnly(2026, 1, 1), cancellationToken: Ct);

        Assert.Equal(expected: 0m, actual: cost);
        AssertOneRequestThroughNamedClient(factory);
    }

    [Fact]
    public async Task AnthropicTokenCountClient_CountsThroughTheNamedClient()
    {
        var factory = JsonFactory("""{"input_tokens":7}""");
        var client = new AnthropicTokenCountClient(httpClient: null, apiKey: "sk-ant-test",
            httpClientFactory: factory);

        var tokens = await client.TryCountTokensAsync(model: "claude-test", text: "hello", cancellationToken: Ct);

        Assert.Equal(expected: 7, actual: tokens);
        AssertOneRequestThroughNamedClient(factory);
    }

    [Fact]
    public async Task AnthropicUsageReportService_FetchesThroughTheNamedClient()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var factory = JsonFactory("""{"data":[],"has_more":false,"next_page":null}""");
        var service = new AnthropicUsageReportService(httpClient: null,
            repository: new ReportedUsageRepository(temp.Database), resolveAdminApiKey: () => "sk-ant-admin01-test",
            logger: NullLogger<AnthropicUsageReportService>.Instance, httpClientFactory: factory);

        await service.RunCycleAsync(Ct);

        AssertOneRequestThroughNamedClient(factory);
    }

    /// <summary>A factory answering every request with 200 and <paramref name="json"/>.</summary>
    private static RecordingHttpClientFactory JsonFactory(string json)
    {
        return new RecordingHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: json, encoding: Encoding.UTF8, mediaType: "application/json")
        });
    }

    private static void AssertOneRequestThroughNamedClient(RecordingHttpClientFactory factory)
    {
        Assert.Equal(expected: [CostReconciliationRetryPolicy.HttpClientName], actual: factory.RequestedNames);
        Assert.Single(factory.Requests);
    }
}
