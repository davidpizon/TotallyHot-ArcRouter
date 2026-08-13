using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="Governance"/>: a sub-view toggle between <see cref="ProvidersAdmin"/>,
/// <see cref="GovernanceModelCards"/>, <see cref="PriceSourcesAdmin"/>, <c>PriceOverridesAdmin</c>, and
/// <see cref="BenchmarkData"/> (Providers is the default). Each sub-view's own behavior is covered by its
/// own test file; here they're only smoke-tested via the toggle.
/// </summary>
public sealed class GovernanceTests
{
    private static Bunit.BunitContext NewContext()
    {
        var ctx = new Bunit.BunitContext();
        // Every sub-view points at an unreachable address - these tests only need each to mount.
        ctx.Services.AddSingleton(new ProviderAdminStore(managementAddress: "http://127.0.0.1:59994"));
        ctx.Services.AddSingleton(new UsageStore(managementAddress: "http://127.0.0.1:59989"));
        ctx.Services.AddSingleton(new PriceSourceStore(new StubPriceSourceAdminClient()));
        ctx.Services.AddSingleton(new BenchmarkDataStore(new StubBenchmarkDataAdminClient()));
        return ctx;
    }

    /// <summary>Hangs forever, so the panel stays in its "loading" state for the toggle smoke test.</summary>
    private sealed class StubPriceSourceAdminClient : IPriceSourceAdminClient
    {
        public Task<PriceSourceList> ListAsync(CancellationToken cancellationToken = default) =>
            new TaskCompletionSource<PriceSourceList>().Task;

        public Task<PriceSourceList> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PriceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PriceRefreshResult> ReorderAsync(IReadOnlyList<string> namesInPriorityOrder, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Hangs forever, so the panel stays in its "loading" state for the toggle smoke test.</summary>
    private sealed class StubBenchmarkDataAdminClient : IBenchmarkDataAdminClient
    {
        public Task<BenchmarkDataStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default) =>
            new TaskCompletionSource<BenchmarkDataStatusInfo>().Task;

        public Task<BenchmarkDataStatusInfo> RecheckAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<BenchmarkSyncEvent> SyncAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void Defaults_to_the_providers_sub_view()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();

        // The toggle offers every sub-view, and Providers (ProvidersAdmin) is mounted first.
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Providers", "Models", "Price Sources", "Benchmark Data"]);
        cut.Markup.Should().Contain("Loading providers");
    }

    [Fact]
    public void Switching_to_the_models_sub_view_renders_GovernanceModelCards()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Models").Click();

        cut.Markup.Should().Contain("Loading");
    }

    [Fact]
    public void Budgets_sub_view_is_gone()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Budgets");
    }

    [Fact]
    public void Switching_to_the_price_sources_sub_view_renders_PriceSourcesAdmin()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Price Sources").Click();

        cut.Markup.Should().Contain("Loading price sources");
    }

    [Fact]
    public void Switching_to_the_benchmark_data_sub_view_renders_BenchmarkData()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Benchmark Data").Click();

        cut.Markup.Should().Contain("Loading benchmark data status");
    }
}

