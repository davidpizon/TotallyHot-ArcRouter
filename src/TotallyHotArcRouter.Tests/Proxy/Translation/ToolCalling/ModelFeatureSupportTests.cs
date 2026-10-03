using TotallyHot.ArcRouter.Tests.PriceCatalog;
using static TotallyHot.ArcRouter.Tests.Proxy.ModelFeatureSupportFixtures;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Covers the per-model capability record (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c>
/// Amendment 1): reading a flag by key path, and the store's persistence - replaced as a set per provider, keyed
/// case-insensitively by provider and upstream model id, and served from the snapshot after a reload.
/// </summary>
public sealed class ModelFeatureSupportTests
{
    // ----- IsSupported -----

    [Fact]
    public void IsSupported_ReadsNestedFlags()
    {
        var haiku = Haiku45();

        Assert.False(haiku.IsSupported("thinking", "types", "adaptive"));
        Assert.True(haiku.IsSupported("thinking", "types", "enabled"));
        Assert.False(haiku.IsSupported("effort"));
        Assert.False(haiku.IsSupported("effort", "xhigh"));
        Assert.True(haiku.IsSupported("context_management", "clear_thinking_20251015"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("thinking", "types", "missing")]
    [InlineData("thinking", "supported")]
    public void IsSupported_IsNullForAPathTheRecordDoesNotDescribe(params string[] path)
    {
        Assert.Null(Haiku45().IsSupported(path));
    }

    [Fact]
    public void IsSupported_IsNullForANonBooleanFlag()
    {
        Assert.Null(Record("""{"effort":{"supported":1}}""").IsSupported("effort"));
    }

    // ----- Store round trip -----

    [Fact]
    public void GetModelFeatureSupport_ReturnsNull_WhenNeverScanned()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        Assert.Null(store.GetModelFeatureSupport(providerKey: "anthropic", modelId: "claude-haiku-4-5-20251001"));
    }

    [Fact]
    public void SetModelFeatureSupport_RoundTripsTheRawRecord_CaseInsensitively()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.SetModelFeatureSupport(providerKey: "anthropic", records: [Haiku45()]);

        var stored = store.GetModelFeatureSupport(providerKey: "Anthropic", modelId: "CLAUDE-HAIKU-4-5-20251001");
        Assert.NotNull(stored);
        Assert.False(stored.IsSupported("thinking", "types", "adaptive"));
        Assert.True(stored.IsSupported("structured_outputs"));
        Assert.Equal(expected: Haiku45().ScannedAtUtc, actual: stored.ScannedAtUtc);
    }

    [Fact]
    public void SetModelFeatureSupport_ReplacesTheProvidersWholeSet_AndLeavesOtherProvidersAlone()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();
        store.SetModelFeatureSupport(providerKey: "anthropic", records: [Haiku45(), Capable("claude-old")]);
        store.SetModelFeatureSupport(providerKey: "gateway",
            records: [Record(capabilitiesJson: FullCapabilities, modelId: "claude-old", providerKey: "gateway")]);

        store.SetModelFeatureSupport(providerKey: "anthropic", records: [Capable()]);

        Assert.Null(store.GetModelFeatureSupport(providerKey: "anthropic", modelId: "claude-haiku-4-5-20251001"));
        Assert.Null(store.GetModelFeatureSupport(providerKey: "anthropic", modelId: "claude-old"));
        Assert.NotNull(store.GetModelFeatureSupport(providerKey: "anthropic", modelId: "claude-sonnet-5"));
        Assert.NotNull(store.GetModelFeatureSupport(providerKey: "gateway", modelId: "claude-old"));
    }

    [Fact]
    public void Records_SurviveARestart()
    {
        using var temp = new TempDatabase();
        temp.CreateToolCallCapabilityStore().SetModelFeatureSupport(providerKey: "anthropic", records: [Haiku45()]);

        var reopened = temp.CreateToolCallCapabilityStore();
        reopened.Reload();

        Assert.NotNull(reopened.GetModelFeatureSupport(providerKey: "anthropic", modelId: "claude-haiku-4-5-20251001"));
    }

    [Fact]
    public void SetModelFeatureSupport_RaisesChanged_ForAWriteAndForClearingRealRecords()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();
        var raised = 0;
        store.Changed += () => raised++;

        store.SetModelFeatureSupport(providerKey: "anthropic", records: [Haiku45()]);
        store.SetModelFeatureSupport(providerKey: "anthropic", records: []);

        Assert.Equal(2, actual: raised);
        Assert.Null(store.GetModelFeatureSupport(providerKey: "anthropic", modelId: "claude-haiku-4-5-20251001"));
    }

    [Fact]
    public void ClearingAProviderWithNoRecords_IsANoOp()
    {
        // Every scan of an OpenAI-shaped provider asks for this, so it must not write, log or notify.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();
        store.SetModelFeatureSupport(providerKey: "gateway",
            records: [Record(capabilitiesJson: FullCapabilities, modelId: "claude-x", providerKey: "gateway")]);
        var raised = 0;
        store.Changed += () => raised++;

        store.SetModelFeatureSupport(providerKey: "openai", records: []);

        Assert.Equal(0, actual: raised);
        Assert.NotNull(store.GetModelFeatureSupport(providerKey: "gateway", modelId: "claude-x"));
    }
}
