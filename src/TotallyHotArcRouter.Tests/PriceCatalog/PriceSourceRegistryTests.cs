using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="PriceSourceRegistry"/>'s enabled-client filtering, which reads the database-backed
/// toggle (D6) rather than configuration.
/// </summary>
public class PriceSourceRegistryTests
{
    [Fact]
    public void SeededDatabase_EnablesBothKnownSourcesByDefault()
    {
        using var temp = new TempDatabase();
        using var toggleStore = temp.CreateToggleStore();
        using var registry = Build(new PriceCatalogOptions(), toggleStore);

        var names = registry.EnabledClients.Select(c => c.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains(PriceCatalogOptions.LiteLlmSourceName, names);
        Assert.Contains(PriceCatalogOptions.OpenRouterSourceName, names);
    }

    [Fact]
    public void DisablingOneSource_LeavesTheOtherEnabled()
    {
        using var temp = new TempDatabase();
        using var toggleStore = temp.CreateToggleStore();
        using var registry = Build(new PriceCatalogOptions(), toggleStore);

        toggleStore.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: false);

        // A disabled source is absent from the loop, not a skipped rung (D6) - the survivor must still poll.
        var client = Assert.Single(registry.EnabledClients);
        Assert.Equal(PriceCatalogOptions.OpenRouterSourceName, client.Name);
    }

    [Fact]
    public void DisablingBothSources_YieldsNoClients()
    {
        using var temp = new TempDatabase();
        using var toggleStore = temp.CreateToggleStore();
        using var registry = Build(new PriceCatalogOptions(), toggleStore);

        toggleStore.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: false);
        toggleStore.SetEnabled(PriceCatalogOptions.OpenRouterSourceName, enabled: false);

        Assert.Empty(registry.EnabledClients);
    }

    [Fact]
    public void EnabledClients_ReflectsToggleFlippedAfterConstruction()
    {
        // The whole point of the panel: the registry used to read the toggle once in its constructor, so a
        // change needed a restart. It must now be evaluated per read.
        using var temp = new TempDatabase();
        using var toggleStore = temp.CreateToggleStore();
        using var registry = Build(new PriceCatalogOptions(), toggleStore);

        Assert.Equal(2, registry.EnabledClients.Count);

        toggleStore.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: false);
        Assert.Single(registry.EnabledClients);

        toggleStore.SetEnabled(PriceCatalogOptions.LiteLlmSourceName, enabled: true);
        Assert.Equal(2, registry.EnabledClients.Count);
    }

    [Fact]
    public void UnloadedToggleStore_YieldsNoClients()
    {
        // A store that has not reloaded yet (the window before the startup check runs) reports everything
        // disabled. Nothing should poll in that window - erring toward "don't fetch" is the safe direction.
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();
        using var toggleStore = new PriceSourceToggleStore(repository, NullLogger<PriceSourceToggleStore>.Instance);
        using var registry = Build(new PriceCatalogOptions(), toggleStore);

        Assert.Empty(registry.EnabledClients);
    }

    [Fact]
    public void UnknownSourceName_Throws()
    {
        using var temp = new TempDatabase();
        using var toggleStore = temp.CreateToggleStore();

        // openpipe, not openrouter: openrouter is now a known, recognized source (it has a client), so it
        // no longer exercises this path - see PriceCatalogOptionsTests for that coverage.
        Assert.Throws<OptionsValidationException>(() => Build(
            new PriceCatalogOptions
            {
                Sources = new Dictionary<string, PriceSourceOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openpipe"] = new PriceSourceOptions(),
                },
            },
            toggleStore));
    }

    private static PriceSourceRegistry Build(PriceCatalogOptions options, PriceSourceToggleStore toggleStore) =>
        new(Options.Create(options), toggleStore, NullLoggerFactory.Instance);
}

