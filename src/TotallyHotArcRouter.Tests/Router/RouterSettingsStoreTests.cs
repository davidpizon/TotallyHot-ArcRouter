using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="RouterSettingsStore"/> (docs/router/self-organizing-classification-plan.md Phase T6):
/// the round-trip get/set surface backing the <c>router_settings</c> table, and the "absence means no
/// override" contract <see cref="RouterSettingsConfigureOptions"/> relies on.
/// </summary>
public sealed class RouterSettingsStoreTests
{
    [Fact]
    public void TryGetBool_NoStoredValue_ReturnsFalseAndDefaultOut()
    {
        var store = CreateStore();

        var found = store.TryGetBool(key: RouterSettingsStore.AdaptiveRoutingEnabledKey, value: out var value);

        Assert.False(found);
        Assert.False(value);
    }

    [Fact]
    public void TryGetInt_NoStoredValue_ReturnsFalseAndDefaultOut()
    {
        var store = CreateStore();

        var found = store.TryGetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, value: out var value);

        Assert.False(found);
        Assert.Equal(0, actual: value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetBool_ThenTryGetBool_RoundTrips(bool storedValue)
    {
        var store = CreateStore();

        store.SetBool(key: RouterSettingsStore.AdaptiveRoutingEnabledKey, value: storedValue);
        var found = store.TryGetBool(key: RouterSettingsStore.AdaptiveRoutingEnabledKey, value: out var value);

        Assert.True(found);
        Assert.Equal(expected: storedValue, actual: value);
    }

    [Fact]
    public void SetInt_ThenTryGetInt_RoundTrips()
    {
        var store = CreateStore();

        store.SetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, 12_345);
        var found = store.TryGetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, value: out var value);

        Assert.True(found);
        Assert.Equal(12_345, actual: value);
    }

    [Fact]
    public void SetInt_CalledTwice_OverwritesPriorValue()
    {
        var store = CreateStore();

        store.SetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, 1_000);
        store.SetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, 2_000);
        store.TryGetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, value: out var value);

        Assert.Equal(2_000, actual: value);
    }

    [Fact]
    public void SecondStore_OverSameDatabaseFile_SeesTheFirstStoresWrites()
    {
        var tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(path1: tempDirectory, path2: "router_embedding_memory.db");

        var first = new RouterSettingsStore(
            database: new RouterMemoryDatabase(Options.Create(new RoutingOptions
                { EmbeddingMemoryDatabasePath = dbPath })),
            logger: NullLogger<RouterSettingsStore>.Instance);
        first.SetBool(key: RouterSettingsStore.AdaptiveRoutingEnabledKey, true);

        var second = new RouterSettingsStore(
            database: new RouterMemoryDatabase(Options.Create(new RoutingOptions
                { EmbeddingMemoryDatabasePath = dbPath })),
            logger: NullLogger<RouterSettingsStore>.Instance);
        var found = second.TryGetBool(key: RouterSettingsStore.AdaptiveRoutingEnabledKey, value: out var value);

        Assert.True(found);
        Assert.True(value);
    }

    private static RouterSettingsStore CreateStore()
    {
        var tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(path1: tempDirectory, path2: "router_embedding_memory.db");
        var database =
            new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        return new RouterSettingsStore(database: database, logger: NullLogger<RouterSettingsStore>.Instance);
    }
}