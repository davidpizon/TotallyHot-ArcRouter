using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="RouterSettingsConfigureOptions"/>'s precedence contract
/// (docs/router/self-organizing-classification-plan.md Phase T6): a stored override beats whatever
/// <c>appsettings.json</c>/the coded default already produced, and an absent stored value leaves that
/// prior value untouched rather than re-asserting the coded default a second time.
/// </summary>
public sealed class RouterSettingsConfigureOptionsTests
{
    [Fact]
    public void Configure_NoStoredOverrides_LeavesOptionsUntouched()
    {
        var store = CreateStore();
        var configure = new RouterSettingsConfigureOptions(store);
        var options = new RoutingOptions { EnableAdaptiveRouting = false, EmbeddingMemoryCapacity = 20_000 };

        configure.Configure(options);

        Assert.False(options.EnableAdaptiveRouting);
        Assert.Equal(20_000, options.EmbeddingMemoryCapacity);
    }

    [Fact]
    public void Configure_StoredAdaptiveRoutingOverride_BeatsWhateverWasAlreadyBound()
    {
        var store = CreateStore();
        store.SetBool(RouterSettingsStore.AdaptiveRoutingEnabledKey, true);
        var configure = new RouterSettingsConfigureOptions(store);
        // Simulates the appsettings.json-bound value the preceding Configure<IConfiguration> step already
        // produced - false, the coded default - which this step must overwrite.
        var options = new RoutingOptions { EnableAdaptiveRouting = false };

        configure.Configure(options);

        Assert.True(options.EnableAdaptiveRouting);
    }

    [Fact]
    public void Configure_StoredCapacityOverride_BeatsWhateverWasAlreadyBound()
    {
        var store = CreateStore();
        store.SetInt(RouterSettingsStore.EmbeddingMemoryCapacityKey, 5_000);
        var configure = new RouterSettingsConfigureOptions(store);
        // Simulates an appsettings.json-bound value of 20000 (the coded default) that the stored 5000
        // override must beat.
        var options = new RoutingOptions { EmbeddingMemoryCapacity = 20_000 };

        configure.Configure(options);

        Assert.Equal(5_000, options.EmbeddingMemoryCapacity);
    }

    [Fact]
    public void Configure_OnlyAdaptiveRoutingStored_LeavesCapacityAtWhateverWasAlreadyBound()
    {
        var store = CreateStore();
        store.SetBool(RouterSettingsStore.AdaptiveRoutingEnabledKey, true);
        var configure = new RouterSettingsConfigureOptions(store);
        // Simulates an appsettings.json-bound capacity of 7500 - no stored override exists for this key,
        // so it must survive untouched rather than being reset to the coded default (20000).
        var options = new RoutingOptions { EmbeddingMemoryCapacity = 7_500 };

        configure.Configure(options);

        Assert.True(options.EnableAdaptiveRouting);
        Assert.Equal(7_500, options.EmbeddingMemoryCapacity);
    }

    [Fact]
    public void Constructor_ThrowsOnNullStore()
    {
        Assert.Throws<ArgumentNullException>(() => new RouterSettingsConfigureOptions(null!));
    }

    private static RouterSettingsStore CreateStore()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDirectory, "router_embedding_memory.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        return new RouterSettingsStore(database, NullLogger<RouterSettingsStore>.Instance);
    }
}
