using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Integration coverage for the <c>/admin/usage/*</c> query surface (see
/// <c>TotallyHot.ArcRouter.Proxy.Management.UsageAdminEndpoints</c>): drives the real endpoints over HTTP
/// against a booted <see cref="ProxyServer"/>, mirroring <see cref="ProviderAdminEndpointsTests"/>.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait(name: "Category", value: "Integration")]
public sealed class UsageAdminEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelRoutingOptions SeedOptions()
    {
        return new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { BaseUrl = "https://api.openai.com", AuthHeaderName = "Authorization" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }
            ]
        };
    }

    private static ProxyServer BuildServer(
        IProviderConfigStore store,
        IUsageRollupStore? rollupStore = null,
        string? managementToken = null)
    {
        var environment = Mock.Of<IEnvironmentVariableProvider>();
        var resolver = new ModelRouteResolver(store: store, environment: environment);
        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance, interceptor: interceptor);

        return new ProxyServer(
            logger: NullLogger<ProxyServer>.Instance,
            proxyMiddleware: middleware,
            0,
            0,
            dependencies: new ProxyServerDependencies
            {
                ManagementToken = managementToken,
                ManagementApi = new ManagementApiDependencies(store)
                {
                    Environment = environment,
                    UsageRollupStore = rollupStore
                }
            });
    }

    private static string BaseAddress(ProxyServer server)
    {
        return server.Addresses.Single(a => a.StartsWith(value: "http://", comparisonType: StringComparison.Ordinal))
            .TrimEnd('/');
    }

    [Fact]
    public async Task GetSummary_NoRollupStoreWired_Returns503()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(requestUri: $"{BaseAddress(server)}/admin/usage/summary?window=day",
                cancellationToken: Ct);

            Assert.Equal(expected: HttpStatusCode.ServiceUnavailable, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task GetSummary_WithData_ReturnsTotals()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);
        await ledger.RecordAsync(
            entry: new UsageLedgerEntry(
                SessionId: "sess-1", 1, Provider: "openai", RequestedModel: "gpt-5.4", ResolvedModel: "gpt-5.4",
                100, 50, null, null, 2m, CostConfidence: CostConfidence.Catalog,
                OccurredAtUtc: DateTimeOffset.UtcNow.AddDays(-2), RequestId: Guid.NewGuid().ToString("N")),
            cancellationToken: Ct);

        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, rollupStore: rollup);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync(requestUri: $"{BaseAddress(server)}/admin/usage/summary?window=week",
                cancellationToken: Ct);
            using var document = JsonDocument.Parse(json);

            Assert.Equal(1, actual: document.RootElement.GetProperty("requests").GetInt32());
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task GetRollup_InvalidFrom_Returns400()
    {
        using var temp = new TempDatabase();
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, rollupStore: temp.CreateRollupStore());
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var response =
                await client.GetAsync(
                    requestUri: $"{BaseAddress(server)}/admin/usage/rollup?from=not-a-date&to=2026-01-01T00:00:00Z",
                    cancellationToken: Ct);

            Assert.Equal(expected: HttpStatusCode.BadRequest, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task GetRollup_WithData_ReturnsGroupedBucket()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);
        var occurredAt = DateTimeOffset.UtcNow.AddDays(-2);
        await ledger.RecordAsync(
            entry: new UsageLedgerEntry(
                SessionId: "sess-1", 1, Provider: "openai", RequestedModel: "gpt-5.4", ResolvedModel: "gpt-5.4",
                100, 50, null, null, 2m, CostConfidence: CostConfidence.Catalog,
                OccurredAtUtc: occurredAt, RequestId: Guid.NewGuid().ToString("N")),
            cancellationToken: Ct);

        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, rollupStore: rollup);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var dayStart = new DateTimeOffset(dateTime: occurredAt.Date, offset: TimeSpan.Zero);
            var url = $"{BaseAddress(server)}/admin/usage/rollup?from={Uri.EscapeDataString(dayStart.ToString("O"))}" +
                      $"&to={Uri.EscapeDataString(dayStart.AddDays(1).ToString("O"))}&width=day&groupBy=model";
            var json = await client.GetStringAsync(requestUri: url, cancellationToken: Ct);
            using var document = JsonDocument.Parse(json);

            var bucket = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(expected: "gpt-5.4", actual: bucket.GetProperty("groupKey").GetString());
            Assert.Equal(1, actual: bucket.GetProperty("requests").GetInt32());
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Export_CsvFormat_ReturnsCsvBody()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);
        var occurredAt = DateTimeOffset.UtcNow.AddDays(-2);
        await ledger.RecordAsync(
            entry: new UsageLedgerEntry(
                SessionId: "sess-1", 1, Provider: "openai", RequestedModel: "gpt-5.4", ResolvedModel: "gpt-5.4",
                100, 50, null, null, 2m, CostConfidence: CostConfidence.Catalog,
                OccurredAtUtc: occurredAt, RequestId: Guid.NewGuid().ToString("N")),
            cancellationToken: Ct);

        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, rollupStore: rollup);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var dayStart = new DateTimeOffset(dateTime: occurredAt.Date, offset: TimeSpan.Zero);
            var url = $"{BaseAddress(server)}/admin/usage/export?from={Uri.EscapeDataString(dayStart.ToString("O"))}" +
                      $"&to={Uri.EscapeDataString(dayStart.AddDays(1).ToString("O"))}&width=day&groupBy=model&format=csv";
            var response = await client.GetAsync(requestUri: url, cancellationToken: Ct);
            var csv = await response.Content.ReadAsStringAsync(Ct);

            Assert.Equal(expected: "text/csv", actual: response.Content.Headers.ContentType?.MediaType);
            var lines = csv.Split(separator: "\r\n", options: StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, actual: lines.Length);
            Assert.StartsWith(expectedStartString: "BucketStartUtc,", actualString: lines[0],
                comparisonType: StringComparison.Ordinal);
            Assert.Contains(expectedSubstring: "gpt-5.4", actualString: lines[1],
                comparisonType: StringComparison.Ordinal);
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Export_DefaultFormat_ReturnsJson()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, rollupStore: rollup);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var url =
                $"{BaseAddress(server)}/admin/usage/export?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}" +
                $"&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}";
            var response = await client.GetAsync(requestUri: url, cancellationToken: Ct);

            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
            Assert.Equal(expected: "application/json", actual: response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Export_InvalidFormat_Returns400()
    {
        using var temp = new TempDatabase();
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, rollupStore: temp.CreateRollupStore());
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var url =
                $"{BaseAddress(server)}/admin/usage/export?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}" +
                $"&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&format=xml";
            var response = await client.GetAsync(requestUri: url, cancellationToken: Ct);

            Assert.Equal(expected: HttpStatusCode.BadRequest, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task GetSummary_WithManagementToken_RequiresToken()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, managementToken: "secret-token");
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(requestUri: $"{BaseAddress(server)}/admin/usage/summary?window=day",
                cancellationToken: Ct);

            Assert.Equal(expected: HttpStatusCode.Unauthorized, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }
}