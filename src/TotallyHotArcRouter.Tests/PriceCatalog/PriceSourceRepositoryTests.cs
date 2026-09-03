using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="PriceSourceRepository"/>'s source-toggle CRUD (enable/disable, rank, and the D4 fresh-price
/// count).
/// </summary>
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
        repository.UpsertPrices(sourceName: "litellm", 0,
            prices: new[] { Price(model: "gpt-4o", provider: "openai", 2.50m, 10.00m) },
            asOfUtc: DateTimeOffset.UtcNow);

        Assert.Equal(1, actual: sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));

        sourceRepository.SetSourceEnabled(sourceName: "litellm", false);

        Assert.Equal(0, actual: sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetSourceStates_ListsDisabledSourcesToo()
    {
        // The opposite of PriceRepository.GetFreshPrice: this describes the sources themselves, so a
        // disabled one must still be listed - otherwise the panel could never switch it back on.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        repository.UpsertPrices(sourceName: "litellm", 0,
            prices: new[] { Price(model: "gpt-4o", provider: "openai", 2.50m, 10.00m) },
            asOfUtc: DateTimeOffset.UtcNow);
        sourceRepository.SetSourceEnabled(sourceName: "litellm", false);

        var source = sourceRepository.GetSourceStates().Single(s => s.Name == "litellm");

        Assert.False(source.Enabled);
        Assert.Equal(1, actual: source.PriceCount);
    }

    [Fact]
    public void SetSourceEnabled_UnknownSource_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();

        // openpipe, not openrouter: openrouter is a real, seeded source now.
        Assert.False(repository.SetSourceEnabled(sourceName: "openpipe", true));
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
        Assert.Equal(1, actual: openRouter.PriorityScore);
        Assert.Equal(0, actual: liteLlm.PriorityScore);
    }

    [Fact]
    public void ReorderSources_MissingASource_RejectsAndChangesNothing()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();
        var before = repository.GetSourceStates()
            .ToDictionary(keySelector: s => s.Name, elementSelector: s => s.PriorityScore);

        var reordered = repository.ReorderSources([PriceCatalogOptions.LiteLlmSourceName]);

        // A partial list would leave the unlisted source's rank stale relative to the ones that moved -
        // rejected outright rather than applied best-effort.
        Assert.False(reordered);
        var after = repository.GetSourceStates()
            .ToDictionary(keySelector: s => s.Name, elementSelector: s => s.PriorityScore);
        Assert.Equal(expected: before, actual: after);
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

    private static NormalizedPrice Price(string model, string provider, decimal input, decimal output)
    {
        return new NormalizedPrice(ModelIdentifier: model, Provider: provider, StandardInputPrice: input,
            StandardOutputPrice: output,
            null, null, null);
    }
}