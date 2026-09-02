using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="PriceSourceRepository"/>'s source-toggle CRUD (enable/disable, rank, and the D4 fresh-price count).</summary>
public class PriceSourceRepositoryTests
{
    [Fact]
    public void CountFreshPrices_ExcludesRowsOwnedByADisabledSource()
    {
        // Without this filter a disabled source's rows would suppress the zero-fresh-prices Error (D4),
        // reporting a healthy feed while nothing usable is actually being served.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        Assert.Equal(1, sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));

        sourceRepository.SetSourceEnabled("litellm", enabled: false);

        Assert.Equal(0, sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetSourceStates_ListsDisabledSourcesToo()
    {
        // The opposite of PriceRepository.GetFreshPrice: this describes the sources themselves, so a
        // disabled one must still be listed - otherwise the panel could never switch it back on.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        sourceRepository.SetSourceEnabled("litellm", enabled: false);

        var source = sourceRepository.GetSourceStates().Single(s => s.Name == "litellm");

        Assert.False(source.Enabled);
        Assert.Equal(1, source.PriceCount);
    }

    [Fact]
    public void SetSourceEnabled_UnknownSource_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();

        // openpipe, not openrouter: openrouter is a real, seeded source now.
        Assert.False(repository.SetSourceEnabled("openpipe", enabled: true));
    }

    [Fact]
    public void ReorderSources_RewritesContiguousScoresFromListPosition()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository(); // seeds litellm (0) and openrouter (-10)

        var reordered = repository.ReorderSources(
            [PriceCatalogOptions.OpenRouterSourceName, PriceCatalogOptions.LiteLlmSourceName]);

        Assert.True(reordered);
        var states = repository.GetSourceStates();
        var openRouter = states.Single(s => s.Name == PriceCatalogOptions.OpenRouterSourceName);
        var liteLlm = states.Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName);
        Assert.Equal(1, openRouter.PriorityScore);
        Assert.Equal(0, liteLlm.PriorityScore);
    }

    [Fact]
    public void ReorderSources_MissingASource_RejectsAndChangesNothing()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();
        var before = repository.GetSourceStates().ToDictionary(s => s.Name, s => s.PriorityScore);

        var reordered = repository.ReorderSources([PriceCatalogOptions.LiteLlmSourceName]);

        // A partial list would leave the unlisted source's rank stale relative to the ones that moved -
        // rejected outright rather than applied best-effort.
        Assert.False(reordered);
        var after = repository.GetSourceStates().ToDictionary(s => s.Name, s => s.PriorityScore);
        Assert.Equal(before, after);
    }

    [Fact]
    public void ReorderSources_UnknownSourceName_Rejects()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();

        var reordered = repository.ReorderSources(
            [PriceCatalogOptions.LiteLlmSourceName, PriceCatalogOptions.OpenRouterSourceName, "openpipe"]);

        Assert.False(reordered);
    }

    [Fact]
    public void ReorderSources_DuplicateName_Rejects()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();

        var reordered = repository.ReorderSources(
            [PriceCatalogOptions.LiteLlmSourceName, PriceCatalogOptions.LiteLlmSourceName]);

        Assert.False(reordered);
    }

    private static TotallyHot.ArcRouter.PriceCatalog.Sources.NormalizedPrice Price(string model, string provider, decimal input, decimal output) =>
        new(model, provider, input, output, CachedInputPrice: null, BatchInputPrice: null, BatchOutputPrice: null);
}
