using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Coverage for the dialect-driven tool-call matcher (<c>docs/router/tool-call-normalization.md</c>
/// §3.1, Phase 0). These are pure string-in/result-out tests with no I/O and no streaming harness -
/// which is the point of keeping <see cref="DialectMatcher"/> message-shaped rather than folding it
/// into the stateful SSE translator.
///
/// <para>
/// The cases that matter most are the ones the shipping single-dialect echo guard gets wrong: a
/// Llama-family payload keyed <c>parameters</c> instead of <c>arguments</c>, a Mistral payload with no
/// closing token, and a capable model's prose *example* of a tool call, which must survive as text.
/// </para>
/// </summary>
public class DialectMatcherTests
{
    // ----- Per-dialect happy paths -----

    [Fact]
    public void Hermes_SingleCall_IsExtracted_AndResidualIsEmpty()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<tool_call>{"name": "get_time", "arguments": {"tz": "UTC"}}</tool_call>""",
            ToolCallDialectRegistry.Hermes);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("get_time", call.Name);
        Assert.Equal("""{"tz":"UTC"}""", call.ArgumentsJson);
        Assert.Equal(string.Empty, result.ResidualContent);
    }

    [Fact]
    public void Hermes_ToolsTagVariant_IsAlsoRecognized()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<tools>{"name": "get_time", "arguments": {}}</tools>""",
            ToolCallDialectRegistry.Hermes);

        Assert.Single(result.ToolCalls);
        Assert.Equal("get_time", result.ToolCalls[0].Name);
    }

    [Fact]
    public void Mistral_NullCloseDelimiter_ConsumesToEndOfMessage()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """[TOOL_CALLS] [{"name": "search", "arguments": {"q": "cats"}}]""",
            ToolCallDialectRegistry.Mistral);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("search", call.Name);
        Assert.Equal("""{"q":"cats"}""", call.ArgumentsJson);
    }

    [Fact]
    public void Mistral_ArrayPayloadWithSeveralCalls_YieldsEachInOrder()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """[TOOL_CALLS] [{"name": "first", "arguments": {}}, {"name": "second", "arguments": {}}]""",
            ToolCallDialectRegistry.Mistral);

        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("first", result.ToolCalls[0].Name);
        Assert.Equal("second", result.ToolCalls[1].Name);
    }

    // This is the case a hardcoded "arguments" lookup silently drops: the region is found, the JSON
    // parses, the name is present - and the call is still discarded because the key is "parameters".
    [Fact]
    public void Llama3Json_ParametersKey_IsExtracted()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<|python_tag|>{"name": "get_weather", "parameters": {"city": "Oslo"}}""",
            ToolCallDialectRegistry.Llama3Json);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("""{"city":"Oslo"}""", call.ArgumentsJson);
    }

    [Fact]
    public void Llama3Json_ArgumentsKey_IsNotAccepted_BecauseTheDialectKeysOnParameters()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<|python_tag|>{"name": "get_weather", "arguments": {"city": "Oslo"}}""",
            ToolCallDialectRegistry.Llama3Json);

        Assert.Empty(result.ToolCalls);
    }

    // ----- Argument encoding -----

    [Fact]
    public void ArgumentsAlreadySerializedAsString_ArePassedThroughWithoutDoubleEncoding()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<tool_call>{"name": "get_time", "arguments": "{\"tz\":\"UTC\"}"}</tool_call>""",
            ToolCallDialectRegistry.Hermes);

        Assert.Equal("""{"tz":"UTC"}""", Assert.Single(result.ToolCalls).ArgumentsJson);
    }

    [Fact]
    public void NestedObjectArguments_KeepBraceBalanceAndSerializeWhole()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<tool_call>{"name": "write", "arguments": {"file": {"path": "a.txt", "mode": "w"}}}</tool_call>""",
            ToolCallDialectRegistry.Hermes);

        Assert.Equal("""{"file":{"path":"a.txt","mode":"w"}}""", Assert.Single(result.ToolCalls).ArgumentsJson);
    }

    [Fact]
    public void BracesInsideQuotedStringArguments_DoNotMiscountDepth()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<tool_call>{"name": "echo", "arguments": {"text": "a } b { c"}}</tool_call>""",
            ToolCallDialectRegistry.Hermes);

        Assert.Equal("""{"text":"a } b { c"}""", Assert.Single(result.ToolCalls).ArgumentsJson);
    }

    // ----- Residual content -----

    [Fact]
    public void ContentBeforeAndAfterACall_IsPreservedInOrder()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """Let me check. <tool_call>{"name": "get_time", "arguments": {}}</tool_call> Done.""",
            ToolCallDialectRegistry.Hermes);

        Assert.Single(result.ToolCalls);
        Assert.Equal("Let me check.  Done.", result.ResidualContent);
    }

    [Fact]
    public void SeveralSequentialCalls_AreAllExtracted()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<tool_call>{"name": "a", "arguments": {}}</tool_call><tool_call>{"name": "b", "arguments": {}}</tool_call>""",
            ToolCallDialectRegistry.Hermes);

        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("a", result.ToolCalls[0].Name);
        Assert.Equal("b", result.ToolCalls[1].Name);
    }

    // ----- Fail-open paths: nothing is ever dropped or thrown on -----

    [Fact]
    public void EchoedSchemaDocumentation_IsNotMistakenForACall_AndSurvivesAsText()
    {
        // The shape the <tools> tag documents in the system prompt has no top-level name/arguments.
        const string content = """<tools>{"type": "function", "function": {"name": "get_time"}}</tools>""";

        var result = DialectMatcher.ExtractToolCalls(content, ToolCallDialectRegistry.Hermes);

        Assert.Empty(result.ToolCalls);
        Assert.Equal(content, result.ResidualContent);
    }

    [Fact]
    public void MalformedJsonInsideARegion_FailsOpenWithDelimitersIntact()
    {
        const string content = """<tool_call>{"name": "broken", "arguments":</tool_call>""";

        var result = DialectMatcher.ExtractToolCalls(content, ToolCallDialectRegistry.Hermes);

        Assert.Empty(result.ToolCalls);
        Assert.Equal(content, result.ResidualContent);
    }

    [Fact]
    public void UnterminatedRegion_ForwardsEverythingFromTheOpenerVerbatim()
    {
        const string content = """Here goes <tool_call>{"name": "cut_off", "arg""";

        var result = DialectMatcher.ExtractToolCalls(content, ToolCallDialectRegistry.Hermes);

        Assert.Empty(result.ToolCalls);
        Assert.Equal(content, result.ResidualContent);
    }

    [Fact]
    public void EmptyName_IsRejected()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<tool_call>{"name": "", "arguments": {}}</tool_call>""",
            ToolCallDialectRegistry.Hermes);

        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public void MissingArgumentsKey_IsRejected()
    {
        var result = DialectMatcher.ExtractToolCalls(
            """<tool_call>{"name": "get_time"}</tool_call>""",
            ToolCallDialectRegistry.Hermes);

        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public void PlainProseWithNoDelimiters_PassesThroughUnchanged()
    {
        const string content = "Just a normal answer with no tool call in it.";

        var result = DialectMatcher.ExtractToolCalls(content, ToolCallDialectRegistry.Hermes);

        Assert.Empty(result.ToolCalls);
        Assert.Equal(content, result.ResidualContent);
    }

    [Fact]
    public void EmptyContent_IsHandled()
    {
        var result = DialectMatcher.ExtractToolCalls(string.Empty, ToolCallDialectRegistry.Hermes);

        Assert.Empty(result.ToolCalls);
        Assert.Equal(string.Empty, result.ResidualContent);
    }

    // ----- The native sentinel is never scanned -----

    [Fact]
    public void OpenAiNativeDialect_NeverRewritesAnything_EvenWhenTextLooksLikeACall()
    {
        const string content = """<tool_call>{"name": "get_time", "arguments": {}}</tool_call>""";

        var result = DialectMatcher.ExtractToolCalls(content, ToolCallDialectRegistry.OpenAiNative);

        Assert.Empty(result.ToolCalls);
        Assert.Equal(content, result.ResidualContent);
    }

    // ----- MatchAny: the union scan Phase 4 arms for an unclassified model -----

    [Theory]
    [InlineData("""<tool_call>{"name": "t", "arguments": {}}</tool_call>""", "hermes")]
    [InlineData("""[TOOL_CALLS] [{"name": "t", "arguments": {}}]""", "mistral")]
    [InlineData("""<|python_tag|>{"name": "t", "parameters": {}}""", "llama3-json")]
    public void MatchAny_AttributesEachDialectCorrectly(string content, string expectedDialect)
    {
        var result = DialectMatcher.MatchAny(content, ToolCallDialectRegistry.ScannableDialects);

        Assert.NotNull(result);
        Assert.Equal(expectedDialect, result.Dialect.Name);
        Assert.Equal("t", Assert.Single(result.ToolCalls).Name);
    }

    [Fact]
    public void MatchAny_ReturnsNull_WhenNoDialectMatches()
    {
        var result = DialectMatcher.MatchAny(
            "A perfectly ordinary sentence.",
            ToolCallDialectRegistry.ScannableDialects);

        Assert.Null(result);
    }

    [Fact]
    public void MatchAny_ReturnsNull_WhenADelimiterMatchesButFramesNoRealCall()
    {
        // Framing alone must not classify a model - otherwise a capable model that merely *mentions*
        // a tag would be permanently mislabeled and scanned forever after.
        var result = DialectMatcher.MatchAny(
            """<tools>{"type": "function", "function": {"name": "x"}}</tools>""",
            ToolCallDialectRegistry.ScannableDialects);

        Assert.Null(result);
    }

    // ----- Delimiter selection -----

    [Fact]
    public void EarliestOpener_WinsRegardlessOfDeclarationOrder()
    {
        // <tools> is declared second in the Hermes dialect but appears first in the text.
        var result = DialectMatcher.ExtractToolCalls(
            """<tools>{"name": "first", "arguments": {}}</tools><tool_call>{"name": "second", "arguments": {}}</tool_call>""",
            ToolCallDialectRegistry.Hermes);

        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("first", result.ToolCalls[0].Name);
        Assert.Equal("second", result.ToolCalls[1].Name);
    }
}

