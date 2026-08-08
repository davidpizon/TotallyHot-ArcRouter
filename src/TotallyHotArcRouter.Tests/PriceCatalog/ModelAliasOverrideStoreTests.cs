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

        store.Upsert("LiteLLM", "big-pickle", "gpt-5.4");

        var o = Assert.Single(store.GetAll());
        Assert.Equal(new ModelAliasOverride("LiteLLM", "big-pickle", "gpt-5.4"), o);
    }

    [Fact]
    public void Upsert_SameKeyTwice_ReplacesRatherThanDuplicating()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        store.Upsert("LiteLLM", "big-pickle", "gpt-5.4");
        store.Upsert("LiteLLM", "big-pickle", "gpt-6");

        var o = Assert.Single(store.GetAll());
        Assert.Equal("gpt-6", o.ModelName);
    }

    [Fact]
    public void TryGetModelName_IsCaseInsensitiveOnBothKeyParts()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();
        store.Upsert("LiteLLM", "big-pickle", "gpt-5.4");

        Assert.Equal("gpt-5.4", store.TryGetModelName("litellm", "BIG-PICKLE"));
    }

    [Fact]
    public void TryGetModelName_NoMatch_ReturnsNull()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        Assert.Null(store.TryGetModelName("LiteLLM", "big-pickle"));
    }

    [Fact]
    public void Remove_Existing_ReturnsTrueAndRemovesIt()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();
        store.Upsert("LiteLLM", "big-pickle", "gpt-5.4");

        Assert.True(store.Remove("LiteLLM", "big-pickle"));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Remove_NoMatch_ReturnsFalse()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        Assert.False(store.Remove("LiteLLM", "big-pickle"));
    }

    [Fact]
    public void Upsert_DifferentSourcesSameKey_AreIndependentRows()
    {
        using var db = new TempDatabase();
        var store = db.CreateOverrideStore();

        store.Upsert("LiteLLM", "big-pickle", "gpt-5.4");
        store.Upsert("OpenRouter", "big-pickle", "gpt-6");

        Assert.Equal(2, store.GetAll().Count);
    }
}
