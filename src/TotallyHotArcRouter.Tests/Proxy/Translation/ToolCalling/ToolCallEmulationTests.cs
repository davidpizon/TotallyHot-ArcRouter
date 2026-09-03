using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Coverage for tool-call emulation (<c>docs/router/tool-call-normalization.md</c> Phase 5): teaching a
/// model with no native tool calling a syntax on the way out, and reading it back on the way in.
/// <para>
/// The response half is deliberately thin here - it is
/// <c>ToolCallNormalizingTranslator</c> unchanged, already covered by
/// <see cref="ToolCallNormalizingTranslatorTests"/>. What this suite owns is the request rewrite, the
/// multi-turn re-rendering that makes emulation survive past its first turn, and the two decisions that
/// keep the classification from eating itself: a taught reply is never recorded as an observation, and a
/// template with tools in an unknown dialect is never condemned to emulation.
/// </para>
/// </summary>
public class ToolCallEmulationTests
{
    // ----- Request rewriting: tools become instructions -----

    [Fact]
    public void TheToolsArray_IsReplacedByInstructionsInTheSystemPrompt()
    {
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[{"role":"user","content":"time?"}],
             "tools":[{"type":"function","function":{"name":"get_time","description":"Gets the time","parameters":{"type":"object","properties":{}}}}]}
            """);

        // An emulated model's template cannot render a tools array, and some servers reject one outright
        // for a model they know has no tool support.
        Assert.False(rewritten.RootElement.TryGetProperty(propertyName: "tools", value: out _));

        var system = Messages(rewritten)[0];
        Assert.Equal(expected: "system", actual: system.GetProperty("role").GetString());

        var content = system.GetProperty("content").GetString()!;
        Assert.Contains(expectedSubstring: "<tool_call>", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "get_time", actualString: content, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Gets the time", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TheInjectedSchemas_KeepTheirTypeFunctionWrapper()
    {
        // Measured, not stylistic. An earlier version stripped the wrapper as OpenAI protocol overhead
        // "carrying nothing a model needs"; against a live qwen2.5.1-coder-7b-instruct that dropped the
        // success rate and pushed the model into inventing its own reply tags. The wrapper is not
        // information for the model, it is a shape the model recognizes.
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[{"role":"user","content":"hi"}],
             "tools":[{"type":"function","function":{"name":"get_time"}}]}
            """);

        var content = Messages(rewritten)[0].GetProperty("content").GetString()!;
        Assert.Contains("""{"type":"function","function":{"name":"get_time"}}""", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmulatedDialect_AlsoAcceptsTheSchemaTag_BecauseSmallModelsBlendTheTwo()
    {
        // Told to wrap schemas in <tools> and reply in <tool_call>, a live qwen2.5.1-coder-7b-instruct
        // replied in <tools> - the tag it had just seen framing JSON - on every single run. That is the
        // same blending the Hermes entry documents from the original incident. Registering only
        // <tool_call> here takes emulation from every probe case passing to almost none.
        var store = Emulated();
        var translator = (ToolCallEmulatingTranslator)Factory(store).TryCreate(route: Route(), true)!;

        var response = translator.TranslateResponse(Encoding.UTF8.GetBytes(
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"<tools>{\"name\":\"get_time\",\"arguments\":{\"timezone\":\"Asia/Tokyo\"}}</tools>"},"finish_reason":"stop"}]}"""));

        using var parsed = JsonDocument.Parse(response);
        var call = parsed.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("tool_calls")[0].GetProperty("function");

        Assert.Equal(expected: "get_time", actual: call.GetProperty("name").GetString());
        Assert.Contains(expectedSubstring: "Asia/Tokyo", actualString: call.GetProperty("arguments").GetString()!,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AnEchoedSchemaBlock_IsNotMistakenForACall()
    {
        // The cost of accepting <tools> as a reply delimiter: the schema block TotallyHot.ArcRouter itself injects
        // is wrapped in those tags, so a model echoing it back lands in a scanned region. The shape check
        // is what makes that safe - a schema entry has no top-level "name", only a nested one.
        var store = Emulated();
        var translator = (ToolCallEmulatingTranslator)Factory(store).TryCreate(route: Route(), true)!;

        var body =
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"<tools>{\"type\":\"function\",\"function\":{\"name\":\"get_time\"}}</tools>"},"finish_reason":"stop"}]}""";
        var response = translator.TranslateResponse(Encoding.UTF8.GetBytes(body));

        using var parsed = JsonDocument.Parse(response);
        var message = parsed.RootElement.GetProperty("choices")[0].GetProperty("message");
        Assert.False(message.TryGetProperty(propertyName: "tool_calls", value: out _));
    }

    [Fact]
    public void ToolChoiceAndParallelToolCalls_AreStrippedToo()
    {
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[{"role":"user","content":"hi"}],"tool_choice":"auto","parallel_tool_calls":true,
             "tools":[{"type":"function","function":{"name":"get_time"}}]}
            """);

        Assert.False(rewritten.RootElement.TryGetProperty(propertyName: "tool_choice", value: out _));
        Assert.False(rewritten.RootElement.TryGetProperty(propertyName: "parallel_tool_calls", value: out _));
    }

    [Fact]
    public void TheInstructions_AreAppendedToAnExistingSystemMessage_NotAddedAsASecondOne()
    {
        // Several local chat templates render only the first system message. Inserting a second one would
        // be silently dropped on exactly the models this path exists to serve.
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[{"role":"system","content":"You are terse."},{"role":"user","content":"time?"}],
             "tools":[{"type":"function","function":{"name":"get_time"}}]}
            """);

        var messages = Messages(rewritten);
        Assert.Single(collection: messages, predicate: m => m.GetProperty("role").GetString() == "system");

        var content = messages[0].GetProperty("content").GetString()!;
        Assert.StartsWith(expectedStartString: "You are terse.", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "get_time", actualString: content, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestWithNoTools_GetsNoInstructions()
    {
        var rewritten = Rewrite("""{"model":"tiny","messages":[{"role":"user","content":"hi"}]}""");

        var messages = Messages(rewritten);
        Assert.Single(messages);
        Assert.Equal(expected: "user", actual: messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public void ABodyThatIsNotJson_IsForwardedUnchanged()
    {
        // Fail-open: RequestInterceptor already parsed this body, so reaching here means something upstream
        // changed. The request may still work; throwing would turn a heuristic into an outage.
        var original = Encoding.UTF8.GetBytes("not json at all");
        var result = ToolCallEmulationRewriter.Rewrite(openAiShapedBody: original,
            dialect: ToolCallDialectRegistry.Emulated, logger: Mock.Of<ILogger>());

        Assert.Equal(expected: original, actual: result);
    }

    [Fact]
    public void ABodyWithNoMessagesArray_KeepsItsTools_RatherThanLosingThemForNothing()
    {
        // Stripping without injecting is the one outcome worse than not rewriting at all: the request
        // loses tool calling and gains nothing in its place - the silent drop this workstream exists to
        // prevent. Everything the rewriter can do lives in `messages`, so without it the safe rewrite is
        // none.
        var original =
            """{"model":"tiny","prompt":"time?","tools":[{"type":"function","function":{"name":"get_time"}}]}""";
        var rewritten = Rewrite(original);

        Assert.True(rewritten.RootElement.TryGetProperty(propertyName: "tools", value: out var tools));
        Assert.Equal(expected: "get_time", actual: tools[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void AMessagesFieldOfTheWrongShape_IsAlsoLeftAlone()
    {
        var rewritten = Rewrite("""{"model":"tiny","messages":"not an array","tool_choice":"auto"}""");

        Assert.True(rewritten.RootElement.TryGetProperty(propertyName: "tool_choice", value: out _));
    }

    // ----- Multi-turn: the part that makes emulation survive past one turn -----

    [Fact]
    public void AnAssistantToolCall_IsReRenderedAsTheTaughtSyntax()
    {
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"user","content":"time?"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_0","type":"function","function":{"name":"get_time","arguments":"{\"tz\":\"UTC\"}"}}]}
            ]}
            """);

        var assistant = Messages(rewritten)[1];

        // The model must be able to read its own previous turn in the syntax it was told to write.
        Assert.False(assistant.TryGetProperty(propertyName: "tool_calls", value: out _));

        var content = assistant.GetProperty("content").GetString()!;
        Assert.Contains(expectedSubstring: "<tool_call>", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "</tool_call>", actualString: content,
            comparisonType: StringComparison.Ordinal);

        // Arguments arrive as a serialized string and are re-parsed into a real object, so the model sees
        // the shape it was taught rather than a string of escaped quotes.
        Assert.Contains(expectedSubstring: "\"arguments\":{\"tz\":\"UTC\"}", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AnAssistantToolCall_KeepsAnyProseTheMessageAlreadyCarried()
    {
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","content":"Let me check.","tool_calls":[{"id":"c1","function":{"name":"get_time","arguments":"{}"}}]}
            ]}
            """);

        var content = Messages(rewritten)[0].GetProperty("content").GetString()!;
        Assert.StartsWith(expectedStartString: "Let me check.", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "<tool_call>", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MultiPartAssistantContent_IsKeptAlongsideTheRenderedCall()
    {
        // The assistant path used to read content only as a string, then overwrite it with the rendered
        // blocks - so a client sending OpenAI's typed-parts shape had that turn's prose silently deleted
        // from the history. The tool-result path already wrote non-string content through, so the two
        // renderers disagreed; they now share one helper.
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","content":[{"type":"text","text":"Let me check the clock."}],
               "tool_calls":[{"id":"c1","function":{"name":"get_time","arguments":"{}"}}]}
            ]}
            """);

        var content = Messages(rewritten)[0].GetProperty("content").GetString()!;
        Assert.StartsWith(expectedStartString: "Let me check the clock.", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "<tool_call>", actualString: content,
            comparisonType: StringComparison.Ordinal);

        // Rendered as prose, not as its raw JSON - protocol noise where the model expects text.
        Assert.DoesNotContain(expectedSubstring: "\"type\"", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MultiPartToolResultContent_IsRenderedAsItsText()
    {
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","tool_calls":[{"id":"c1","function":{"name":"get_time","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"c1","content":[{"type":"text","text":"12:00 UTC"}]}
            ]}
            """);

        Assert.Equal(expected: "Tool result for get_time:\n12:00 UTC",
            actual: Messages(rewritten)[1].GetProperty("content").GetString());
    }

    [Fact]
    public void ContentWithNoTextParts_IsPreservedAsJson_RatherThanDropped()
    {
        // An image-only turn cannot be rendered as text for a model that could not read it anyway, but it
        // still must not vanish: a model reading something odd is recoverable, a model reading nothing is
        // the silent drop this workstream exists to prevent.
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","content":[{"type":"image_url","image_url":{"url":"http://example.invalid/a.png"}}],
               "tool_calls":[{"id":"c1","function":{"name":"get_time","arguments":"{}"}}]}
            ]}
            """);

        var content = Messages(rewritten)[0].GetProperty("content").GetString()!;
        Assert.Contains(expectedSubstring: "image_url", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "<tool_call>", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedArguments_AreWrittenThroughAsAString_RatherThanThrowing()
    {
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","tool_calls":[{"id":"c1","function":{"name":"get_time","arguments":"{not json"}}]}
            ]}
            """);

        var content = Messages(rewritten)[0].GetProperty("content").GetString()!;
        Assert.Contains(expectedSubstring: "get_time", actualString: content, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "not json", actualString: content, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AToolResult_BecomesAUserMessage_LabeledWithTheToolItAnswers()
    {
        // `tool` is exactly the role an emulated model's template has never seen; `user` is the one role
        // every template supports. The name is knowable only from the assistant turn that requested it.
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","tool_calls":[{"id":"call_7","function":{"name":"get_time","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"call_7","content":"12:00 UTC"}
            ]}
            """);

        var result = Messages(rewritten)[1];
        Assert.Equal(expected: "user", actual: result.GetProperty("role").GetString());

        var content = result.GetProperty("content").GetString()!;
        Assert.Contains(expectedSubstring: "Tool result for get_time:", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "12:00 UTC", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AToolResultWithAnUnresolvableId_IsLabeledWithoutAName_RatherThanDropped()
    {
        var rewritten = Rewrite(
            """{"model":"tiny","messages":[{"role":"tool","tool_call_id":"orphan","content":"42"}]}""");

        var content = Messages(rewritten)[0].GetProperty("content").GetString()!;
        Assert.Equal(expected: "Tool result:\n42", actual: content);
    }

    [Fact]
    public void ConsecutiveToolResults_AreMergedIntoOneUserMessage()
    {
        // Parallel calls return several results in a row, and many local templates require user and
        // assistant turns to alternate - three consecutive user messages would break rendering.
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","tool_calls":[
                {"id":"a","function":{"name":"get_time","arguments":"{}"}},
                {"id":"b","function":{"name":"get_date","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"a","content":"12:00"},
              {"role":"tool","tool_call_id":"b","content":"Tuesday"}
            ]}
            """);

        var messages = Messages(rewritten);
        Assert.Equal(2, actual: messages.Count);

        var content = messages[1].GetProperty("content").GetString()!;
        Assert.Contains(expectedSubstring: "Tool result for get_time:\n12:00", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Tool result for get_date:\nTuesday", actualString: content,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryThatIsNotAMessageObject_IsForwardedRatherThanDropped()
    {
        // The rewriter's contract is to forward what it cannot handle. Deleting the entry would also
        // change what upstream sees: a malformed body that a non-emulated route forwards - and the server
        // rejects - would be quietly repaired for an emulated one.
        var rewritten = Rewrite(
            """{"model":"tiny","messages":[{"role":"user","content":"hi"},"not-a-message",{"role":"user","content":"bye"}]}""");

        var messages = Messages(rewritten);
        Assert.Equal(3, actual: messages.Count);
        Assert.Equal(expected: "not-a-message", actual: messages[1].GetString());
    }

    [Fact]
    public void ANonMessageEntry_SeparatesToolResultsInsteadOfLettingThemMerge()
    {
        // The subtle half. Buffered tool results are waiting to become one user message; carrying them
        // past the preserved node would emit them *after* something that came before them, reordering the
        // conversation rather than merely tolerating an oddity in it.
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","tool_calls":[
                {"id":"a","function":{"name":"get_time","arguments":"{}"}},
                {"id":"b","function":{"name":"get_date","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"a","content":"12:00"},
              42,
              {"role":"tool","tool_call_id":"b","content":"Tuesday"}
            ]}
            """);

        var messages = Messages(rewritten);
        Assert.Equal(4, actual: messages.Count);

        Assert.Equal(expected: "Tool result for get_time:\n12:00",
            actual: messages[1].GetProperty("content").GetString());
        Assert.Equal(42, actual: messages[2].GetInt32());
        Assert.Equal(expected: "Tool result for get_date:\nTuesday",
            actual: messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public void HistoryIsReRendered_EvenWhenTheFollowUpTurnOffersNoTools()
    {
        // A client that resends history without re-offering the tools would otherwise forward role:"tool"
        // straight to a model whose template has no such role - "emulation works for exactly one turn".
        var rewritten = Rewrite(
            """
            {"model":"tiny","messages":[
              {"role":"assistant","tool_calls":[{"id":"a","function":{"name":"get_time","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"a","content":"12:00"}
            ]}
            """);

        Assert.DoesNotContain(collection: Messages(rewritten),
            filter: m => m.GetProperty("role").GetString() == "tool");
    }

    // ----- Bounded overhead -----

    [Fact]
    public void AnOversizedToolset_DropsWholeToolsAndWarns_RatherThanTruncatingASchema()
    {
        // A truncated schema is worse than an absent one: the model would confidently call a tool with a
        // signature it half-read.
        var (messages, logger) = CapturingLogger();
        var padding = new string('x', 4096);
        var tools = JsonSerializer.Serialize(Enumerable.Range(0, 8).Select(i => new
        {
            type = "function",
            function = new { name = $"tool_{i}", description = padding }
        }));

        var rewritten = Rewrite(
            requestBody: $$"""{"model":"tiny","messages":[{"role":"user","content":"hi"}],"tools":{{tools}}}""",
            logger: logger);

        var content = Messages(rewritten)[0].GetProperty("content").GetString()!;
        Assert.True(content.Length < ToolCallInstructionInjector.MaxToolSchemaChars * 2);
        Assert.Contains(expectedSubstring: "tool_0", actualString: content, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "tool_7", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(collection: messages,
            filter: m => m.Contains(value: "injection budget", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void AnEightThousandTokenWindow_ProducesTheSameBudgetAsTheFixedFallback()
    {
        // The scaling fraction was chosen so an 8k-context model - the case the original fixed 16 KiB
        // constant was reasoned about - gets the identical budget it always did; the change is only in
        // what happens above and below that point.
        var (messages, logger) = CapturingLogger();

        var rewritten = ToolCallEmulationRewriter.Rewrite(
            openAiShapedBody: Encoding.UTF8.GetBytes(OversizedToolsetRequestBody()),
            dialect: ToolCallDialectRegistry.Emulated,
            logger: logger,
            contextWindow: new ModelContextWindow(ProviderKey: "lmstudio", ModelName: "tiny", 8192));

        var content = Messages(JsonDocument.Parse(rewritten))[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain(expectedSubstring: "tool_39", actualString: content,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(collection: messages, filter: m => m.Contains(
            value: $"{ToolCallInstructionInjector.MaxToolSchemaChars}-character injection budget",
            comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void ALargeProbedContextWindow_RaisesTheInjectionBudget_AboveTheFixedFallback()
    {
        // A model whose probed window is far larger than 8k was previously having tools dropped at the same
        // fixed 16 KiB every small local model was capped at. Scaling to the window fixes that.
        var (messages, logger) = CapturingLogger();

        var rewritten = ToolCallEmulationRewriter.Rewrite(
            openAiShapedBody: Encoding.UTF8.GetBytes(OversizedToolsetRequestBody()),
            dialect: ToolCallDialectRegistry.Emulated,
            logger: logger,
            contextWindow: new ModelContextWindow(ProviderKey: "lmstudio", ModelName: "tiny", 1_000_000));

        var content = Messages(JsonDocument.Parse(rewritten))[0].GetProperty("content").GetString()!;
        Assert.Contains(expectedSubstring: "tool_0", actualString: content, comparisonType: StringComparison.Ordinal);

        // Clamped at the sanity ceiling rather than scaling without bound from a million-token window.
        Assert.Contains(collection: messages,
            filter: m =>
                m.Contains(value: "131072-character injection budget", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void ATinyProbedContextWindow_LowersTheInjectionBudget_BelowTheFixedFallback()
    {
        // The other direction matters just as much: 16 KiB can be most or all of a genuinely small window,
        // which would crowd out the conversation the tools exist to serve.
        var (messages, logger) = CapturingLogger();

        var rewritten = ToolCallEmulationRewriter.Rewrite(
            openAiShapedBody: Encoding.UTF8.GetBytes(OversizedToolsetRequestBody()),
            dialect: ToolCallDialectRegistry.Emulated,
            logger: logger,
            contextWindow: new ModelContextWindow(ProviderKey: "lmstudio", ModelName: "tiny", 512));

        var content = Messages(JsonDocument.Parse(rewritten))[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain(expectedSubstring: "tool_1", actualString: content,
            comparisonType: StringComparison.Ordinal);

        // Clamped at the floor rather than shrinking to a budget too small to describe even one tool.
        Assert.Contains(collection: messages,
            filter: m =>
                m.Contains(value: "4096-character injection budget", comparisonType: StringComparison.Ordinal));
    }

    /// <summary>
    /// 40 tools with 4 KiB descriptions each - about 165 KB serialized, comfortably over every budget these
    /// tests probe (the 4 KiB floor, the 16 KiB fallback, and the 128 KiB ceiling alike), so each test's
    /// warning and included/excluded tools reflect the budget actually applied rather than everything fitting.
    /// </summary>
    private static string OversizedToolsetRequestBody()
    {
        var padding = new string('x', 4096);
        var tools = JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(i => new
        {
            type = "function",
            function = new { name = $"tool_{i}", description = padding }
        }));

        return $$"""{"model":"tiny","messages":[{"role":"user","content":"hi"}],"tools":{{tools}}}""";
    }

    [Fact]
    public void TheFactory_WiresTheProbedContextWindow_IntoTheEmulatingTranslatorsBudget()
    {
        // End-to-end through the same construction path production DI uses: TryCreate must pass the store
        // down so the translator actually looks the window up, not just that Rewrite honors one when handed
        // it directly (covered above).
        var store = Emulated().SeedContextWindow(providerKey: "lmstudio", modelName: "tiny", 1_000_000);
        var factory = new ToolCallNormalizerFactory(capabilityStore: store, contextWindowStore: store);
        var translator = (ToolCallEmulatingTranslator)factory.TryCreate(route: Route(), true)!;

        // tool_20 fits inside the ~128 KiB ceiling a million-token window scales to, but not inside the
        // fixed 16 KiB fallback (~3 tools' worth of these 4 KiB descriptions) - so its presence proves the
        // factory actually threaded the store through, not just that Rewrite honors a window when handed one
        // directly (covered above).
        var withWindow = Messages(JsonDocument.Parse(
                translator.TranslateRequest(Encoding.UTF8.GetBytes(OversizedToolsetRequestBody()))))[0]
            .GetProperty("content").GetString()!;
        Assert.Contains(expectedSubstring: "tool_20", actualString: withWindow,
            comparisonType: StringComparison.Ordinal);

        var withoutWindow = Messages(Rewrite(OversizedToolsetRequestBody()))[0]
            .GetProperty("content").GetString()!;
        Assert.DoesNotContain(expectedSubstring: "tool_20", actualString: withoutWindow,
            comparisonType: StringComparison.Ordinal);
    }

    // ----- Selection, and the classification that must not eat itself -----

    [Fact]
    public void AnEmulatedCapabilityRow_SelectsTheEmulatingTranslator()
    {
        var translator = Factory(Emulated()).TryCreate(route: Route(), true);

        var emulator = Assert.IsType<ToolCallEmulatingTranslator>(translator);
        Assert.Equal(expected: "emulated", actual: emulator.Dialect.Name);
        Assert.True(emulator.Plan.IsEmulating);
    }

    [Fact]
    public void AnEmulatedModel_IsNotArmed_WhenTheRequestHasNeitherToolsNorHistory()
    {
        Assert.Null(Factory(Emulated()).TryCreate(route: Route(), false));
    }

    [Fact]
    public void AnEmulatedModel_IsArmed_ForAFollowUpTurnCarryingOnlyHistory()
    {
        var translator = Factory(Emulated())
            .TryCreate(route: Route(), false, true);

        Assert.IsType<ToolCallEmulatingTranslator>(translator);
    }

    [Fact]
    public void ATaughtReply_IsNotRecordedAsAnObservation()
    {
        // The trap this exists to avoid: DetectionConfidence.Observed outranks the `emulated` row that
        // produced the instructions, so recording the taught syntax would stop emulation on the next
        // request, remove the instructions, and leave the model emitting nothing - a classification that
        // erases the reason it was made.
        var store = Emulated();
        var translator = (ToolCallEmulatingTranslator)Factory(store).TryCreate(route: Route(), true)!;

        var response = translator.TranslateResponse(Encoding.UTF8.GetBytes(
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"<tool_call>{\"name\":\"get_time\",\"arguments\":{}}</tool_call>"},"finish_reason":"stop"}]}"""));

        // The call is still normalized - emulation works.
        using var parsed = JsonDocument.Parse(response);
        var message = parsed.RootElement.GetProperty("choices")[0].GetProperty("message");
        Assert.Equal(expected: "get_time",
            actual: message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());

        // ...but nothing was learned from it.
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public void ANativeToolCallsReply_IsStillRecorded_EvenWhileEmulating()
    {
        // The one piece of evidence an emulated request cannot manufacture, and the signal that this model
        // should never have been emulated - so it is exactly what breaks out of the classification.
        var store = Emulated();
        var translator = (ToolCallEmulatingTranslator)Factory(store).TryCreate(route: Route(), true)!;

        translator.TranslateResponse(Encoding.UTF8.GetBytes(
            """{"choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"get_time","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""));

        var recorded = Assert.Single(store.Recorded);
        Assert.Equal(expected: "openai-native", actual: recorded.Dialect);
        Assert.Equal(expected: DetectionConfidence.Observed, actual: recorded.Confidence);
    }

    [Fact]
    public void ADialectThatCannotBeTaught_IsRejectedAtConstruction()
    {
        // A dialect that can be recognized but not taught would produce a request stripped of its tools
        // with nothing put back in their place - silently removing tool calling instead of emulating it.
        var plan = new ToolCallNormalizationPlan(ProviderKey: "lmstudio", ModelName: "tiny",
            Candidates: [ToolCallDialectRegistry.Hermes], true, true);

        Assert.Throws<ArgumentException>(() =>
            new ToolCallEmulatingTranslator(plan: plan, dialect: ToolCallDialectRegistry.Hermes, null,
                logger: Mock.Of<ILogger>()));
    }

    // ----- End to end, through the real middleware -----

    [Fact]
    public async Task ProxyMiddleware_ForwardsAnEmulatedRequest_WithNoToolsAndWithInstructions_OnTheClientsPath()
    {
        var (forwarded, forwardedPath, _) = await RunThroughMiddlewareAsync(
            """
            {"model":"tiny","messages":[{"role":"user","content":"time?"}],
             "tools":[{"type":"function","function":{"name":"get_time","parameters":{"type":"object","properties":{}}}}]}
            """);

        using var body = JsonDocument.Parse(forwarded);
        Assert.False(body.RootElement.TryGetProperty(propertyName: "tools", value: out _));
        Assert.Contains(expectedSubstring: "get_time",
            actualString: Messages(body)[0].GetProperty("content").GetString()!,
            comparisonType: StringComparison.Ordinal);

        // IClientPathTranslator: the body is rewritten, but the upstream URL is still the client's own path
        // against the provider's base URL - BuildRequestUri never sees that path, so routing this through
        // the request-reshaping branch would have silently dropped "/v1".
        Assert.Equal(expected: "http://127.0.0.1:1234/v1/chat/completions", actual: forwardedPath);
    }

    [Fact]
    public async Task ProxyMiddleware_CompletesATwoTurnToolExchange()
    {
        // The full round trip Phase 5 exists for: teach, parse the taught reply back into a real call, then
        // accept the client's tool result and re-render the whole history on the next outbound request.
        var (_, _, clientResponse) = await RunThroughMiddlewareAsync(
            """
            {"model":"tiny","messages":[{"role":"user","content":"time?"}],
             "tools":[{"type":"function","function":{"name":"get_time"}}]}
            """);

        // Turn 1: the taught text came back as a real tool_calls delta, which is the only shape VS Code has.
        Assert.Contains(expectedSubstring: "\"tool_calls\"", actualString: clientResponse,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "get_time", actualString: clientResponse,
            comparisonType: StringComparison.Ordinal);

        // Turn 2: the client replies with the result, in the shapes an emulated model cannot read.
        var (forwarded, _, _) = await RunThroughMiddlewareAsync(
            """
            {"model":"tiny","messages":[
              {"role":"user","content":"time?"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_0","type":"function","function":{"name":"get_time","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"call_0","content":"12:00 UTC"}
            ],
             "tools":[{"type":"function","function":{"name":"get_time"}}]}
            """);

        using var body = JsonDocument.Parse(forwarded);
        var roles = Messages(body).Select(m => m.GetProperty("role").GetString()).ToList();

        Assert.DoesNotContain(expected: "tool", collection: roles);
        Assert.DoesNotContain(collection: Messages(body),
            filter: m => m.TryGetProperty(propertyName: "tool_calls", value: out _));

        var assistant = Messages(body).Single(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Contains(expectedSubstring: "<tool_call>", actualString: assistant.GetProperty("content").GetString()!,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "12:00 UTC", actualString: forwarded,
            comparisonType: StringComparison.Ordinal);
    }

    // ----- Test helpers -----

    private static JsonDocument Rewrite(string requestBody, ILogger? logger = null)
    {
        return JsonDocument.Parse(ToolCallEmulationRewriter.Rewrite(
            openAiShapedBody: Encoding.UTF8.GetBytes(requestBody),
            dialect: ToolCallDialectRegistry.Emulated,
            logger: logger ?? Mock.Of<ILogger>()));
    }

    private static List<JsonElement> Messages(JsonDocument body)
    {
        return [.. body.RootElement.GetProperty("messages").EnumerateArray()];
    }

    private static FakeToolCallCapabilityStore Emulated()
    {
        return new FakeToolCallCapabilityStore().Seed(
            new ModelToolCapability(ProviderKey: "lmstudio", ModelName: "tiny", Dialect: "emulated",
                Confidence: DetectionConfidence.Template));
    }

    private static ToolCallNormalizerFactory Factory(IToolCallCapabilityStore store)
    {
        return new ToolCallNormalizerFactory(store);
    }

    private static ResolvedModelRoute Route()
    {
        return new ResolvedModelRoute(
            ModelName: "tiny",
            Provider: "lmstudio",
            ProviderModelId: "tiny",
            UpstreamBaseUrl: new Uri("http://127.0.0.1:1234/v1"),
            AuthHeaderName: "Authorization",
            ExtraHeaders: []);
    }

    /// <summary>
    /// Drives one request through the real middleware against a stub upstream that replies with the taught
    /// syntax, returning what was actually forwarded upstream and what the client received.
    /// </summary>
    private static async Task<(string ForwardedBody, string ForwardedUrl, string ClientResponse)>
        RunThroughMiddlewareAsync(
            string requestBody)
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "tiny",
            providerModelId: "tiny",
            baseUrl: "http://127.0.0.1:1234",
            providerName: "lmstudio");

        var store = new FakeToolCallCapabilityStore().Seed(
            new ModelToolCapability(ProviderKey: "lmstudio", ModelName: "tiny", Dialect: "emulated",
                Confidence: DetectionConfidence.Template));

        const string taughtReply =
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>"},"finish_reason":"stop"}]}""";

        var forwardedBody = string.Empty;
        var forwardedUrl = string.Empty;

        var handler = new DelegatingHandlerStub(async request =>
        {
            forwardedUrl = request.RequestUri!.ToString();
            forwardedBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: taughtReply, encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
        });

        using var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
                modelRouteResolver: resolver),
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                ToolCallNormalizerFactory = new ToolCallNormalizerFactory(store)
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var requestBytes = Encoding.UTF8.GetBytes(requestBody);
        context.Request.Body = new MemoryStream(requestBytes);
        context.Request.ContentLength = requestBytes.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        context.Response.Body.Position = 0;
        var clientResponse = await new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8)
            .ReadToEndAsync(TestContext.Current.CancellationToken);

        return (forwardedBody, forwardedUrl, clientResponse);
    }

    /// <summary>A logger that records each formatted warning, for the bounded-overhead assertion.</summary>
    private static (List<string> Messages, ILogger Logger) CapturingLogger()
    {
        var messages = new List<string>();
        var loggerMock = new Mock<ILogger>();

        loggerMock
            .Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
                messages.Add(invocation.Arguments[2]?.ToString() ?? string.Empty)));

        return (messages, loggerMock.Object);
    }

    private sealed class DelegatingHandlerStub : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}