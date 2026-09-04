using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Covers the dialect-to-<c>capabilities</c> mapping <c>POST /api/show</c> publishes
/// (<c>docs/adr/0003-declare-tool-support-for-emulated-and-unclassified-models.md</c>).
/// <para>
/// Every dialect currently maps to the same pair, so most of these assertions look redundant. They are not:
/// the mapping is the seam where a future dialect meaning "cannot express a tool call at all" would land,
/// and <see cref="ForDialect_CoversEveryRegisteredDialect"/> is what forces that decision to be made
/// consciously rather than inherited.
/// </para>
/// </summary>
public class OllamaModelCapabilitiesTests
{
    public static TheoryData<string> EveryRegisteredDialect()
    {
        var data = new TheoryData<string>();
        foreach (var dialect in ToolCallDialectRegistry.All) data.Add(dialect.Name);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRegisteredDialect))]
    public void ForDialect_EveryRegisteredDialect_DeclaresCompletionAndTools(string dialectName)
    {
        Assert.Equal(expected: ["completion", "tools"], actual: OllamaModelCapabilities.ForDialect(dialectName));
    }

    // Null is the dominant state - no scan has run, or the provider cannot be probed at all - and is the
    // case that decides whether the picker fix works.
    [Fact]
    public void ForDialect_Unclassified_DeclaresTools()
    {
        Assert.Contains(expected: "tools", collection: OllamaModelCapabilities.ForDialect(null));
    }

    // A row written by a newer build naming a dialect this one has never heard of. Treated as unclassified,
    // matching how ToolCallNormalizerFactory reads the same value.
    [Fact]
    public void ForDialect_UnknownDialectName_DeclaresTools()
    {
        Assert.Contains(expected: "tools", collection: OllamaModelCapabilities.ForDialect("a-dialect-from-the-future"));
    }

    /// <summary>
    /// Fails the build when a dialect is added to <see cref="ToolCallDialectRegistry.All"/> without anyone
    /// deciding what it can do. The mapping is total today, so this passes; the value is that it stops
    /// being total silently.
    /// </summary>
    [Fact]
    public void ForDialect_CoversEveryRegisteredDialect()
    {
        foreach (var dialect in ToolCallDialectRegistry.All)
        {
            var capabilities = OllamaModelCapabilities.ForDialect(dialect.Name);

            Assert.NotEmpty(capabilities);
            Assert.Contains(expected: "completion", collection: capabilities);
        }
    }

    [Fact]
    public void Union_MergesAcrossModels_AndAlwaysIncludesCompletion()
    {
        var union = OllamaModelCapabilities.Union([["completion"], ["completion", "tools"]]);

        Assert.Equal(expected: ["completion", "tools"], actual: union);
    }

    // An alias backed by nothing routable can still complete; it just cannot promise tools.
    [Fact]
    public void Union_OfNothing_IsCompletionOnly()
    {
        Assert.Equal(expected: ["completion"], actual: OllamaModelCapabilities.Union([]));
    }

    // Order is fixed rather than derived from set enumeration, so the serialized JSON is byte-stable across
    // runs and processes.
    [Fact]
    public void Union_EmitsCanonicalOrder_RegardlessOfInputOrder()
    {
        var union = OllamaModelCapabilities.Union([["tools", "completion"], ["tools"]]);

        Assert.Equal(expected: ["completion", "tools"], actual: union);
    }
}