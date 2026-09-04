using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="ModelAliasOverrideStore"/>'s CRUD over <c>model_alias_overrides</c>.</summary>
public sealed class ModelAliasOverrideStoreTests
{
    [Fact]
    public void GetAll_Empty_ReturnsEmptyList()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Upsert_ThenGetAll_ReturnsIt()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        store.Upsert(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle", modelName: "gpt-5.4");

        var o = Assert.Single(store.GetAll());
        Assert.Equal(
            expected: new ModelAliasOverride(SourceName: "LiteLLM", AggregatorModelKey: "big-pickle",
                ModelName: "gpt-5.4"), actual: o);
    }

    [Fact]
    public void Upsert_SameKeyTwice_ReplacesRatherThanDuplicating()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        store.Upsert(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle", modelName: "gpt-5.4");
        store.Upsert(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle", modelName: "gpt-6");

        var o = Assert.Single(store.GetAll());
        Assert.Equal(expected: "gpt-6", actual: o.ModelName);
    }

    [Fact]
    public void TryGetModelName_IsCaseInsensitiveOnBothKeyParts()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();
        store.Upsert(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle", modelName: "gpt-5.4");

        Assert.Equal(expected: "gpt-5.4",
            actual: store.TryGetModelName(sourceName: "litellm", aggregatorModelKey: "BIG-PICKLE"));
    }

    [Fact]
    public void TryGetModelName_NoMatch_ReturnsNull()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        Assert.Null(store.TryGetModelName(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle"));
    }

    [Fact]
    public void Remove_Existing_ReturnsTrueAndRemovesIt()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();
        store.Upsert(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle", modelName: "gpt-5.4");

        Assert.True(store.Remove(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle"));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Remove_NoMatch_ReturnsFalse()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        Assert.False(store.Remove(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle"));
    }

    [Fact]
    public void Upsert_DifferentSourcesSameKey_AreIndependentRows()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        store.Upsert(sourceName: "LiteLLM", aggregatorModelKey: "big-pickle", modelName: "gpt-5.4");
        store.Upsert(sourceName: "OpenRouter", aggregatorModelKey: "big-pickle", modelName: "gpt-6");

        Assert.Equal(2, actual: store.GetAll().Count);
    }
}