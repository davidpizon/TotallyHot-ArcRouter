using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Text.Json;
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
[Trait("Category", "Integration")]
public sealed class UsageAdminEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelRoutingOptions SeedOptions() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new ProviderOptions { BaseUrl = "https://api.openai.com", AuthHeaderName = "Authorization" }
        },
        ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }]
    };

    private static ProxyServer BuildServer(
        IProviderConfigStore store,
        IUsageRollupStore? rollupStore = null,
        string? managementToken = null)
    {
        var environment = Mock.Of<IEnvironmentVariableProvider>();
        var resolver = new ModelRouteResolver(store, environment);
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(NullLogger<ProxyMiddleware>.Instance, interceptor);

        return new ProxyServer(
            NullLogger<ProxyServer>.Instance,
            middleware,
            port: 0,
            grpcPort: 0,
            dependencies: new ProxyServerDependencies
            {
                ManagementToken = managementToken,
                ManagementApi = new ManagementApiDependencies(store)
                {
                    Environment = environment,
                    UsageRollupStore = rollupStore,
                },
            });
    }

    private static string BaseAddress(ProxyServer server) =>
        server.Addresses.Single(a => a.StartsWith("http://", StringComparison.Ordinal)).TrimEnd('/');

    [Fact]
    public async Task GetSummary_NoRollupStoreWired_Returns503()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"{BaseAddress(server)}/admin/usage/summary?window=day", Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
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
            new UsageLedgerEntry(
                "sess-1", 1, "openai", "gpt-5.4", "gpt-5.4",
                100, 50, null, null, 2m, CostConfidence.Catalog,
                DateTimeOffset.UtcNow.AddDays(-2), Guid.NewGuid().ToString("N")),
            Ct);

        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store, rollupStore: rollup);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync($"{BaseAddress(server)}/admin/usage/summary?window=week", Ct);
            using var document = JsonDocument.Parse(json);

            Assert.Equal(1, document.RootElement.GetProperty("requests").GetInt32());
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
        using var server = BuildServer(store, rollupStore: temp.CreateRollupStore());
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"{BaseAddress(server)}/admin/usage/rollup?from=not-a-date&to=2026-01-01T00:00:00Z", Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
            new UsageLedgerEntry(
                "sess-1", 1, "openai", "gpt-5.4", "gpt-5.4",
                100, 50, null, null, 2m, CostConfidence.Catalog,
                occurredAt, Guid.NewGuid().ToString("N")),
            Ct);

        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store, rollupStore: rollup);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var dayStart = new DateTimeOffset(occurredAt.Date, TimeSpan.Zero);
            var url = $"{BaseAddress(server)}/admin/usage/rollup?from={Uri.EscapeDataString(dayStart.ToString("O"))}" +
                      $"&to={Uri.EscapeDataString(dayStart.AddDays(1).ToString("O"))}&width=day&groupBy=model";
            var json = await client.GetStringAsync(url, Ct);
            using var document = JsonDocument.Parse(json);

            var bucket = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("gpt-5.4", bucket.GetProperty("groupKey").GetString());
            Assert.Equal(1, bucket.GetProperty("requests").GetInt32());
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
            new UsageLedgerEntry(
                "sess-1", 1, "openai", "gpt-5.4", "gpt-5.4",
                100, 50, null, null, 2m, CostConfidence.Catalog,
                occurredAt, Guid.NewGuid().ToString("N")),
            Ct);

        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store, rollupStore: rollup);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var dayStart = new DateTimeOffset(occurredAt.Date, TimeSpan.Zero);
            var url = $"{BaseAddress(server)}/admin/usage/export?from={Uri.EscapeDataString(dayStart.ToString("O"))}" +
                      $"&to={Uri.EscapeDataString(dayStart.AddDays(1).ToString("O"))}&width=day&groupBy=model&format=csv";
            var response = await client.GetAsync(url, Ct);
            var csv = await response.Content.ReadAsStringAsync(Ct);

            Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
            var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("BucketStartUtc,", lines[0], StringComparison.Ordinal);
            Assert.Contains("gpt-5.4", lines[1], StringComparison.Ordinal);
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
        using var server = BuildServer(store, rollupStore: rollup);
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var url = $"{BaseAddress(server)}/admin/usage/export?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}" +
                      $"&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}";
            var response = await client.GetAsync(url, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
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
        using var server = BuildServer(store, rollupStore: temp.CreateRollupStore());
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var url = $"{BaseAddress(server)}/admin/usage/export?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}" +
                      $"&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&format=xml";
            var response = await client.GetAsync(url, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        using var server = BuildServer(store, managementToken: "secret-token");
        await server.StartAsync(Ct);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"{BaseAddress(server)}/admin/usage/summary?window=day", Ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await server.StopAsync(Ct);
        }
    }
}
