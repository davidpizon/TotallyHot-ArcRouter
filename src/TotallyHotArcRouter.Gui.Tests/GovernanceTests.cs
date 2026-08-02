using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Bunit;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="Governance"/> after budgets moved onto each provider's card: the tab is now just a
/// two-way toggle between the <see cref="ProvidersAdmin"/> and <see cref="PriceSourcesAdmin"/> sub-views
/// (Providers is the default). Those sub-views' own behavior is covered by
/// <see cref="ProvidersAdminTests"/> and <see cref="PriceSourcesAdminTests"/>; here they're only
/// smoke-tested via the toggle.
/// </summary>
public sealed class GovernanceTests
{
    private static Bunit.BunitContext NewContext()
    {
        var ctx = new Bunit.BunitContext();
        // Both sub-views point at unreachable addresses - these tests only need each to mount.
        ctx.Services.AddSingleton(new ProviderAdminStore(managementAddress: "http://127.0.0.1:59994"));
        ctx.Services.AddSingleton(new PriceSourceStore(new StubPriceSourceAdminClient()));
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

    [Fact]
    public void Defaults_to_the_providers_sub_view()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();

        // The toggle offers exactly the two remaining views, and Providers (ProvidersAdmin) is mounted first.
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Providers", "Price Sources"]);
        cut.Markup.Should().Contain("Loading providers");
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
}

