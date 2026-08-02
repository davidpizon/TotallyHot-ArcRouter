using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// The evidence behind the emulation prompt's wording, as replies captured verbatim from a live model.
///
/// <para>
/// Every string below was emitted by a real model served by LM Studio at temperature 0 - the first two
/// sections by <c>qwen2.5.1-coder-7b-instruct</c> while tuning
/// <see cref="ToolCallDialectRegistry.Emulated"/>, the third by <c>deepseek-r1-distill-qwen-7b</c> against
/// that same finished prompt (both 2026-07-31). They are <b>captures, not fixtures someone wrote</b> - the
/// same standing as the token-by-token fragment sequence in
/// <see cref="ToolCallNormalizingTranslatorTests"/>, and for the same reason: an invented payload only
/// encodes what its author already believed, which is exactly what let the empty <c>tool_calls</c> array
/// survive a green suite and two reviews.
/// </para>
///
/// <para>
/// These run offline and always, as does <see cref="ToolCallEmulationReplayTests"/> - the other half,
/// which replays each recorded model's full transcript through the shipped rewriter and translator. The
/// two divide the evidence: that one asks whether a model can be emulated at all, while this one pins the
/// individual replies behind each wording choice, using the bytes that produced them.
/// </para>
///
/// <para>
/// The failing captures are deliberately asserted to yield <b>nothing</b>. They are not bugs awaiting a
/// fix; they are the reason the prompt reads the way it does. Anyone who later teaches the scanner to
/// rescue one of these shapes has to delete a test whose comment explains why that was refused.
/// </para>
/// </summary>
public class ToolCallEmulationCaptureTests
{
    /// <summary>Runs a captured reply through exactly the path an emulated response takes.</summary>
    private static DialectMatchResult? Match(string capturedContent) =>
        DialectMatcher.MatchAny(capturedContent, [ToolCallDialectRegistry.Emulated]);

    // ----- The shipped prompt: what it actually produces -----

    [Fact]
    public void TheShippedPrompt_ProducesASchemaTaggedCall_WhichIsExtracted()
    {
        // Reply to "What time is it in Tokyo?" under the shipped prompt. Note the tag: the model was told
        // to reply in <tool_call> and used <tools>, the tag it had just seen framing JSON in the schema
        // block. It did this on every run. Registering only <tool_call> for the emulated dialect would
        // drop this - which is the whole reason the dialect carries both.
        var match = Match(
            """
            <tools>
            {
              "name": "get_time",
              "arguments": {
                "timezone": "Asia/Tokyo"
              }
            }
            </tools>
            """);

        var call = Assert.Single(match!.ToolCalls);
        Assert.Equal("get_time", call.Name);
        Assert.Contains("Asia/Tokyo", call.ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedPrompt_ProducesTwoCallsInOneRegion_WhenTwoWereAskedFor()
    {
        // Reply to "Get me both the time in Tokyo and the weather in Paris." Two objects inside a single
        // region, not two regions - which needs no special handling, because JsonObjectScanner walks the
        // region for balanced-brace objects rather than assuming one.
        var match = Match(
            """
            <tools>
                {"name": "get_time", "arguments": {"timezone": "Asia/Tokyo"}}
                {"name": "get_weather", "arguments": {"city": "Paris", "units": "metric"}}
            </tools>
            """);

        Assert.Equal(2, match!.ToolCalls.Count);
        Assert.Equal(["get_time", "get_weather"], match.ToolCalls.Select(c => c.Name));
    }

    [Fact]
    public void TheShippedPrompt_EmitsNoBlockAtAll_WhenNoToolIsNeeded()
    {
        // The false-positive guard, and the one scenario every prompt variant passed. A model that answers
        // in prose must not have a call invented for it.
        Assert.Null(Match("The sea does not need a tool to be described. Waves fold over themselves."));
    }

    // ----- The rejected variants: why the prompt is not written in plain English -----

    [Fact]
    public void PlainEnglishInstructions_ProducedACorrectCallWithNoFraming_WhichIsUnrecoverable()
    {
        // This is the capture that decided the prompt. Told in ordinary prose to reply inside <tool_call>
        // tags - including a version stating the tags were literal and showing a worked example - the model
        // produced the right function and the right arguments with no delimiters whatsoever, on every run.
        //
        // Unrecoverable is the point. Nothing is wrong with the scanner here; there is no framing to find.
        // Rescuing it would take a dialect with no opening token, which ToolCallDialectRegistry refuses at
        // length: it would match any JSON object in any response, turning every code sample containing
        // {"name": ...} into a tool invocation.
        Assert.Null(Match(
            """
            {
              "name": "get_time",
              "arguments": {
                "timezone": "Asia/Tokyo"
              }
            }
            """));
    }

    [Fact]
    public void AFencedReply_YieldsNothing()
    {
        // Adding "do not wrap it in a code fence" to the instructions produced this. Instructing a model
        // not to do something is not the same as it not doing that thing, which is the general lesson the
        // whole tuning exercise taught.
        Assert.Null(Match(
            """
            ```json
            {
              "name": "read_file",
              "arguments": {
                "path": "/etc/hosts"
              }
            }
            ```
            """));
    }

    [Fact]
    public void StrippingTheSchemaWrapper_MadeTheModelInventItsOwnDelimiter()
    {
        // Captured with the shipped wording but schemas emitted as bare function objects rather than the
        // full {"type":"function",...} entries. The model invented <json>. This is the measurement that
        // overturned "the wrapper is pure protocol overhead carrying nothing a model needs" - it carries
        // nothing a model needs to *know*, and is still a shape the model needs to *see*.
        Assert.Null(Match(
            """
            <json>
            {
              "name": "get_weather",
              "arguments": {
                "city": "Paris",
                "units": "metric"
              }
            }
            </json>
            """));
    }

    [Fact]
    public void RemovingTheSchemaTagToAvoidBlending_MadeTheModelInventAWrapperObject()
    {
        // The obvious fix for the <tools>/<tool_call> blend is to stop putting <tools> in the prompt, so
        // only one tag remains to copy. Captured result: no tag at all, a code fence, and an invented
        // function_calls envelope whose entries are nested a level below where any dialect looks.
        Assert.Null(Match(
            """
            ```xml
            {
              "function_calls": [
                {
                  "name": "get_time",
                  "arguments": {"timezone": "Asia/Tokyo"}
                }
              ]
            }
            ```
            """));
    }

    // ----- A second model: four ways the same prompt can fail -----
    //
    // Captured from deepseek-r1-distill-qwen-7b on LM Studio at temperature 0 (2026-07-31), against the
    // identical shipped prompt that qwen2.5.1-coder answers correctly. The whole envelopes are in
    // RecordedModelTranscripts; these are the assistant texts, isolated so each failure can be named.
    //
    // Read together they answer a question the single-model captures above could not: is <tools> a
    // reasonable second delimiter to register, or the first step onto a slope? These four are the slope.
    // No two agree, none is a near-miss, and a scanner wide enough to admit all of them would be matching
    // essentially any JSON object near essentially any angle bracket.

    [Fact]
    public void AnotherModel_InventedAFunctionCallElement_WhichIsNotRegistered()
    {
        // The most tempting one to "just add", because it is a well-formed element wrapping a well-formed
        // payload with the right keys. It is refused for the same reason <json> above is: the prompt named
        // one tag, the model chose another, and the set of tags a model might choose is not enumerable.
        // Registering each newly observed spelling is how a scanner ends up matching prose.
        Assert.Null(Match(
            """
            <function-call>
                {"name": "get_time", "arguments": {"timezone": "Asia/Tokyo"}}
            </function-call>
            """));
    }

    [Fact]
    public void AnotherModel_KeyedTheFunctionAsFunctionRatherThanName_InsideAFence()
    {
        // Two independent failures stacked, which is why it is kept whole rather than split: even if the
        // fence were somehow scanned, the payload keys the function as "function" where every dialect -
        // and OpenAI itself - uses "name". Rescuing this needs both a new delimiter and a new key alias.
        Assert.Null(Match(
            """
            ```json
            {"function":"read_file","arguments":{"path":"/etc/hosts"}}
            ```
            """));
    }

    [Fact]
    public void AnotherModel_EmittedAnXmlAttributeForm_WhoseArgumentsAreNotEvenJson()
    {
        // The streamed reply to the same question the previous test captures the buffered reply to. Note
        // `arguments={...}` is an XML attribute with an unquoted JSON value - not valid XML, not valid
        // JSON, and not parseable as either without a bespoke reader. This is the capture to point at when
        // someone proposes making the scanner "a bit more forgiving".
        Assert.Null(Match(
            """
            ```xml
            <function name="read_file" arguments={"path": "/etc/hosts"} />
            ```
            """));
    }

    [Fact]
    public void AnotherModel_SerializedArgumentsAsAQueryStringInsteadOfAnObject()
    {
        // Correct function, correct values, and arguments as a *string* of ad-hoc key='value' pairs. Worth
        // keeping because it fails differently from the rest: had this been framed in a registered
        // delimiter, it would have extracted cleanly and handed the client a tool call whose arguments no
        // tool can parse - a wrong answer rather than no answer.
        Assert.Null(Match("""{"name": "get_weather", "arguments": "city='Paris', units='metric'"}"""));
    }

    [Fact]
    public void AnotherModel_InventedACallWhenAskedNotToUseTools_AndWasSavedOnlyByItsOwnMissingDelimiter()
    {
        // The false-positive scenario, and the one result in this whole exercise that should make a reader
        // uncomfortable. Asked for a haiku and explicitly told not to use tools, the model emitted a tool
        // call named after the first line of the haiku it never wrote.
        var undelimited = """{"name": "Rustling waves crash.", "arguments": {"type": "string", "value": "Rustling waves crash."}}""";

        // It yields nothing, so the shipped pipeline is safe today.
        Assert.Null(Match(undelimited));

        // But it is safe by accident, not by judgment. The identical payload inside the delimiter the
        // prompt asked for extracts a call to a "tool" that is a line of poetry - proving the guard here is
        // the model's failure to follow instructions, not a check that the name matches an offered tool.
        var framed = Match($"<tool_call>{undelimited}</tool_call>");
        Assert.Equal("Rustling waves crash.", Assert.Single(framed!.ToolCalls).Name);
    }

    // ----- The server envelope, captured from the same session -----

    [Fact]
    public void TheEmulatedDialect_StillRejectsAnEchoedSchemaBlock()
    {
        // The cost of accepting <tools> as a reply delimiter is that the schema block TotallyHot.ArcRouter itself
        // injects is wrapped in those tags, so a model echoing it lands inside a scanned region. The shape
        // check is what keeps that safe: a schema entry's name is nested under "function", not top level.
        Assert.Null(Match("""<tools>{"type":"function","function":{"name":"get_time"}}</tools>"""));
    }
}

