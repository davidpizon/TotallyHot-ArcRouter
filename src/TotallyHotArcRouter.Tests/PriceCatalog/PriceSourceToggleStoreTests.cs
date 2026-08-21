using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="PriceSourceToggleStore"/> - D6's toggle, its persistence, and the per-source
/// cancellation that makes disabling take effect on an in-flight fetch.
/// </summary>
public class PriceSourceToggleStoreTests
{
    [Fact]
    public void Reload_SeededDatabase_ReportsBothKnownSourcesEnabled()
    {
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();

        Assert.True(store.IsEnabled(PriceCatalogOptions.LiteLlmSourceName));
        Assert.True(store.IsEnabled(PriceCatalogOptions.OpenRouterSourceName));
        var names = store.List().Select(s => s.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains(PriceCatalogOptions.LiteLlmSourceName, names);
        Assert.Contains(PriceCatalogOptions.OpenRouterSourceName, names);
    }

    [Fact]
    public void IsEnabled_BeforeReload_ReportsDisabled()
    {
        // The store is constructed while the host graph is built, before the startup check has created the
        // schema. Reporting "disabled" until it reloads is the safe direction: nothing polls in that window.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        using var store = new PriceSourceToggleStore(repository, NullLogger<PriceSourceToggleStore>.Instance);

        Assert.False(store.IsEnabled(PriceCatalogOptions.LiteLlmSourceName));
    }

    [Fact]
    public void List_ReflectsPriceRowsWrittenWithoutAReload()
    {
        // The panel's "Pull Now" writes prices and then re-renders this list. Serving it from the toggle
        // cache showed the pre-pull count and age - which reads exactly like the pull having failed - so the
        // list reads through to the database while only the toggle stays cached.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        using var store = temp.CreateToggleStore(repository);

        Assert.Equal(0, store.List().Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName).PriceCount);

        repository.UpsertPrices(
            PriceCatalogOptions.LiteLlmSourceName,
            priorityScore: 0,
            [new TotallyHot.ArcRouter.PriceCatalog.Sources.NormalizedPrice("gpt-4o", "openai", 2.5m, 10m, null, null, null)],
            DateTimeOffset.UtcNow);

        var source = store.List().Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName);
        Assert.Equal(1, source.PriceCount);
    }

    [Fact]
    public void IsEnabled_UnknownSource_ReportsDisabled()
    {
        // Inverts the old config default of "absent means enabled": now every source with a client is seeded
        // a row, so absence means the source has no client and cannot poll. openrouter is a real, seeded
        // source now, so this uses openpipe - a source that was investigated and rejected entirely.
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();

        Assert.False(store.IsEnabled("openpipe"));
    }

    [Fact]
    public void SetEnabled_PersistsAcrossAFreshStore()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        using (var store = temp.CreateToggleStore(repository))
        {
            Assert.True(store.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: false));
        }

        // A second store over the same database stands in for a restart: SQLite owns the flag, so the choice
        // must survive without any configuration involved.
        using var reopened = temp.CreateToggleStore(repository);
        Assert.False(reopened.IsEnabled(PriceCatalogOptions.LiteLlmSourceName));
    }

    [Fact]
    public void SetEnabled_UnknownSource_ReturnsFalseAndChangesNothing()
    {
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();
        var raised = false;
        store.Changed += () => raised = true;

        Assert.False(store.SetEnabled("openpipe", enabled: true));
        Assert.False(raised);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void SetEnabled_RaisesChanged()
    {
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();
        var raised = 0;
        store.Changed += () => raised++;

        store.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: false);

        Assert.Equal(1, raised);
        Assert.False(store.List().Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName).Enabled);
    }

    [Fact]
    public void SetEnabled_Disabling_CancelsTheSourceToken()
    {
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();
        var token = store.GetSourceToken(PriceCatalogOptions.LiteLlmSourceName);

        Assert.False(token.IsCancellationRequested);
        store.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: false);

        // This is what aborts a fetch already in flight rather than waiting for the next cycle (D6).
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void SetEnabled_Enabling_DoesNotCancelTheSourceToken()
    {
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();
        var token = store.GetSourceToken(PriceCatalogOptions.LiteLlmSourceName);

        store.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: true);

        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void ReEnabling_IssuesAFreshUncancelledToken()
    {
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();
        var original = store.GetSourceToken(PriceCatalogOptions.LiteLlmSourceName);

        store.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: false);
        store.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: true);
        var reissued = store.GetSourceToken(PriceCatalogOptions.LiteLlmSourceName);

        // A cancelled token never resets. Handing the old one back would leave the source reading as enabled
        // in the panel while never fetching again - a silent lie, and the worst outcome available here.
        Assert.True(original.IsCancellationRequested);
        Assert.False(reissued.IsCancellationRequested);
    }

    [Fact]
    public void List_OrdersByRankThenName()
    {
        using var temp = new TempDatabase();
        temp.SeedExtraSource("aaa-low-rank", priorityScore: 1);
        temp.SeedExtraSource("zzz-high-rank", priorityScore: 5);
        using var store = temp.CreateToggleStore();

        var names = store.List().Select(s => s.Name).ToList();

        Assert.Equal("zzz-high-rank", names[0]);
    }

    [Fact]
    public void Reorder_RewritesRankAndRaisesChanged()
    {
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();
        var raised = 0;
        store.Changed += () => raised++;

        var reordered = store.Reorder([PriceCatalogOptions.OpenRouterSourceName, PriceCatalogOptions.LiteLlmSourceName]);

        Assert.True(reordered);
        Assert.Equal(1, raised);
        // List() orders by rank descending, so the submitted order should now be reflected in read order too.
        Assert.Equal(PriceCatalogOptions.OpenRouterSourceName, store.List()[0].Name);
    }

    [Fact]
    public void Reorder_AloneDoesNotRecomputeServedPrices()
    {
        // Reorder only owns the rank; re-resolving contested cells under the new order is the caller's job
        // (the gRPC service, the MCP tool) via PriceCatalogIngestionService.RecomputeWinnersAsync - this store
        // must not call it itself.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("high", enabled: true, priorityScore: 10);
        var repository = temp.CreateRepository();
        using var store = temp.CreateToggleStore(repository);
        repository.UpsertPrices("high", 10, [new TotallyHot.ArcRouter.PriceCatalog.Sources.NormalizedPrice("gpt-4o", "openai", 2.50m, 10.00m, null, null, null)], DateTimeOffset.UtcNow);
        repository.UpsertPrices(PriceCatalogOptions.LiteLlmSourceName, 0, [new TotallyHot.ArcRouter.PriceCatalog.Sources.NormalizedPrice("gpt-4o", "openai", 999m, 999m, null, null, null)], DateTimeOffset.UtcNow);

        Assert.True(store.Reorder([PriceCatalogOptions.LiteLlmSourceName, "high", PriceCatalogOptions.OpenRouterSourceName]));

        // Still "high"'s number - the reorder itself did not touch model_prices.
        Assert.Equal(2.50m, repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24))!.InputPerMillionTokens);
    }

    [Fact]
    public void Reorder_InvalidSet_ReturnsFalseAndDoesNotRaiseChanged()
    {
        using var temp = new TempDatabase();
        using var store = temp.CreateToggleStore();
        var raised = false;
        store.Changed += () => raised = true;

        var reordered = store.Reorder([PriceCatalogOptions.LiteLlmSourceName]);

        Assert.False(reordered);
        Assert.False(raised);
    }

    [Fact]
    public void GetSourceToken_AfterDispose_Throws()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToggleStore();
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.GetSourceToken(PriceCatalogOptions.LiteLlmSourceName));
    }
}

