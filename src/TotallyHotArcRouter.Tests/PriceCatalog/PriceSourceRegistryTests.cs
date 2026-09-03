using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;

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
        using var registry = Build(options: new PriceCatalogOptions(), toggleStore: toggleStore);

        var names = registry.EnabledClients.Select(c => c.Name).ToList();
        Assert.Equal(2, actual: names.Count);
        Assert.Contains(expected: PriceCatalogOptions.LiteLlmSourceName, collection: names);
        Assert.Contains(expected: PriceCatalogOptions.OpenRouterSourceName, collection: names);
    }

    [Fact]
    public void DisablingOneSource_LeavesTheOtherEnabled()
    {
        using var temp = new TempDatabase();
        using var toggleStore = temp.CreateToggleStore();
        using var registry = Build(options: new PriceCatalogOptions(), toggleStore: toggleStore);

        toggleStore.SetEnabled(sourceName: PriceCatalogOptions.LiteLlmSourceName, false);

        // A disabled source is absent from the loop, not a skipped rung (D6) - the survivor must still poll.
        var client = Assert.Single(registry.EnabledClients);
        Assert.Equal(expected: PriceCatalogOptions.OpenRouterSourceName, actual: client.Name);
    }

    [Fact]
    public void DisablingBothSources_YieldsNoClients()
    {
        using var temp = new TempDatabase();
        using var toggleStore = temp.CreateToggleStore();
        using var registry = Build(options: new PriceCatalogOptions(), toggleStore: toggleStore);

        toggleStore.SetEnabled(sourceName: PriceCatalogOptions.LiteLlmSourceName, false);
        toggleStore.SetEnabled(sourceName: PriceCatalogOptions.OpenRouterSourceName, false);

        Assert.Empty(registry.EnabledClients);
    }

    [Fact]
    public void EnabledClients_ReflectsToggleFlippedAfterConstruction()
    {
        // The whole point of the panel: the registry used to read the toggle once in its constructor, so a
        // change needed a restart. It must now be evaluated per read.
        using var temp = new TempDatabase();
        using var toggleStore = temp.CreateToggleStore();
        using var registry = Build(options: new PriceCatalogOptions(), toggleStore: toggleStore);

        Assert.Equal(2, actual: registry.EnabledClients.Count);

        toggleStore.SetEnabled(sourceName: PriceCatalogOptions.LiteLlmSourceName, false);
        Assert.Single(registry.EnabledClients);

        toggleStore.SetEnabled(sourceName: PriceCatalogOptions.LiteLlmSourceName, true);
        Assert.Equal(2, actual: registry.EnabledClients.Count);
    }

    [Fact]
    public void UnloadedToggleStore_YieldsNoClients()
    {
        // A store that has not reloaded yet (the window before the startup check runs) reports everything
        // disabled. Nothing should poll in that window - erring toward "don't fetch" is the safe direction.
        using var temp = new TempDatabase();
        var repository = temp.CreateSourceRepository();
        using var toggleStore =
            new PriceSourceToggleStore(repository: repository, logger: NullLogger<PriceSourceToggleStore>.Instance);
        using var registry = Build(options: new PriceCatalogOptions(), toggleStore: toggleStore);

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
            options: new PriceCatalogOptions
            {
                Sources = new Dictionary<string, PriceSourceOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openpipe"] = new()
                }
            },
            toggleStore: toggleStore));
    }

    private static PriceSourceRegistry Build(PriceCatalogOptions options, PriceSourceToggleStore toggleStore)
    {
        return new PriceSourceRegistry(options: Options.Create(options), toggleStore: toggleStore,
            loggerFactory: NullLoggerFactory.Instance);
    }
}