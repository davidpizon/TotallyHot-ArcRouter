using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Coverage for the built-in dialect registry (<c>docs/router/tool-call-normalization.md</c> §3.1).
/// Several of these pin invariants the rest of the design depends on rather than merely describing the
/// table: dialect names are persisted to SQLite so they cannot drift, the native sentinel must stay
/// non-scannable so byte-for-byte forwarding is preserved, and the precomputed opener characters must
/// stay in sync with the delimiters they are derived from.
/// </summary>
public class ToolCallDialectRegistryTests
{
    [Fact]
    public void OpenAiNative_IsNotScannable()
    {
        // If this ever flips, every capable model's prose becomes a rewrite candidate - the exact
        // false-positive class this workstream exists to remove.
        Assert.False(ToolCallDialectRegistry.OpenAiNative.IsScannable);
    }

    [Fact]
    public void EveryScannableDialect_HasAtLeastOneDelimiter()
    {
        Assert.All(collection: ToolCallDialectRegistry.ScannableDialects,
            action: dialect => Assert.True(dialect.IsScannable));
    }

    [Fact]
    public void ScannableDialects_ExcludeTheNativeSentinelAndTheEmulatedDialect()
    {
        var names = ToolCallDialectRegistry.ScannableDialects.Select(d => d.Name).ToList();

        Assert.DoesNotContain(expected: "openai-native", collection: names);
        // Emulated shares Hermes framing; including both would make attribution a coin flip.
        Assert.DoesNotContain(expected: "emulated", collection: names);
    }

    [Fact]
    public void DialectNames_AreUnique()
    {
        var names = ToolCallDialectRegistry.All.Select(d => d.Name).ToList();

        Assert.Equal(expected: names.Count, actual: names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // These strings are written into model_tool_capabilities.dialect, so renaming one silently
    // orphans every existing row. Pinned here so a rename has to be a deliberate, visible decision.
    [Theory]
    [InlineData("openai-native")]
    [InlineData("hermes")]
    [InlineData("mistral")]
    [InlineData("llama3-json")]
    [InlineData("function-call")]
    [InlineData("constrained")]
    [InlineData("emulated")]
    public void PersistedDialectNames_AreStable(string name)
    {
        Assert.True(ToolCallDialectRegistry.TryGet(name: name, dialect: out var dialect));
        Assert.Equal(expected: name, actual: dialect.Name);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        Assert.True(ToolCallDialectRegistry.TryGet(name: "HeRmEs", dialect: out var dialect));
        Assert.Equal(expected: "hermes", actual: dialect.Name);
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsFalseAndFallsBackToNative_RatherThanThrowing()
    {
        // A capability row written by a newer build must degrade to "not scanned", not crash startup.
        Assert.False(ToolCallDialectRegistry.TryGet(name: "dialect-from-the-future", dialect: out var dialect));
        Assert.Equal(expected: "openai-native", actual: dialect.Name);
        Assert.False(dialect.IsScannable);
    }

    [Fact]
    public void TryGet_Null_ReturnsFalse()
    {
        Assert.False(ToolCallDialectRegistry.TryGet(null, dialect: out _));
    }

    [Fact]
    public void ScannableOpenerFirstChars_CoverEveryScannableOpener()
    {
        var expected = ToolCallDialectRegistry.ScannableDialects
            .SelectMany(dialect => dialect.Delimiters)
            .Select(delimiter => delimiter.Open[0])
            .Distinct()
            .OrderBy(c => c);

        Assert.Equal(expected: expected, actual: ToolCallDialectRegistry.ScannableOpenerFirstChars.OrderBy(c => c));
    }

    [Fact]
    public void OnlyTheImposedDialects_CarryASystemPrompt()
    {
        // The two modes TotallyHot.ArcRouter imposes rather than detects. `constrained` needs one despite also
        // sending a grammar: a response_format schema never enters the model's context, so without the
        // prompt the model is constrained to a well-formed reply about tools it does not know exist.
        string[] imposed = ["emulated", "constrained"];

        foreach (var dialect in ToolCallDialectRegistry.All)
            if (imposed.Contains(dialect.Name))
                Assert.NotNull(dialect.EmulationPrompt);
            else
                Assert.Null(dialect.EmulationPrompt);
    }

    [Fact]
    public void TheConstrainedDialect_HasNoDelimiters_SoItIsNeverScannedFor()
    {
        // Its reply is a whole JSON envelope parsed strictly, not a framed region found inside prose. If it
        // ever became scannable, every envelope would also be run through the delimiter matcher.
        Assert.False(ToolCallDialectRegistry.Constrained.IsScannable);
        Assert.DoesNotContain(expected: ToolCallDialectRegistry.Constrained,
            collection: ToolCallDialectRegistry.ScannableDialects);
    }

    [Fact]
    public void FunctionCallDialect_IsParsable()
    {
        // Live-observed framing from a Qwen2-architecture fine-tune that sometimes falls back to this
        // wrapper instead of a native tool_calls field or the Hermes tags its architecture tier predicts.
        var result = DialectMatcher.ExtractToolCalls(
            """<function-call>{"name": "get_weather", "arguments": {"city": "Paris"}}</function-call>""",
            dialect: ToolCallDialectRegistry.FunctionCall);

        Assert.Equal(expected: "get_weather", actual: Assert.Single(result.ToolCalls).Name);
    }

    [Fact]
    public void EmulatedDialect_IsParsableByItsOwnFraming()
    {
        // The prompt teaches a syntax; that syntax has to round-trip through the matcher, or emulation
        // would teach models to reply in a form TotallyHot.ArcRouter cannot read back.
        var result = DialectMatcher.ExtractToolCalls(
            """<tool_call>{"name": "get_time", "arguments": {"tz": "UTC"}}</tool_call>""",
            dialect: ToolCallDialectRegistry.Emulated);

        Assert.Equal(expected: "get_time", actual: Assert.Single(result.ToolCalls).Name);
    }
}