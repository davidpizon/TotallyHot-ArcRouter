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
/// Coverage for tool-call normalization (<c>docs/router/tool-call-normalization.md</c> Phase 4): rewriting
/// a model's dialect-framed tool call into a real OpenAI <c>tool_calls</c> shape, and recording what the
/// response revealed about the model.
/// <para>
/// The first half of this suite is <b>the regression contract for the original incident</b>
/// (<c>unified-api-translation.md</c> §4.5), ported verbatim from <c>ToolCallEchoGuardTranslatorTests</c>
/// - including the token-by-token fragment sequence captured live from LM Studio's
/// <c>qwen2.5.1-coder-7b-instruct</c> before any of this existed. If those stop passing, generalizing the
/// guard to every dialect broke the narrow fix it grew out of.
/// </para>
/// </summary>
public class ToolCallNormalizingTranslatorTests
{
    // ----- Ported regression contract: the Hermes dialect the shipping echo guard handled -----

    [Fact]
    public void StreamTranslator_NoTagsPresent_ContentPassesThroughUnchanged()
    {
        var translator = StreamTranslator();

        var output = Decode(translator.Push(ToBytes(Chunk(content: "Hello there"))));
        output += Decode(translator.Flush());

        var chunks = ParseChunks(output);
        Assert.Single(chunks);
        Assert.Equal(expected: "Hello there", actual: Delta(chunks[0]).GetProperty("content").GetString());
        Assert.False(Delta(chunks[0]).TryGetProperty(propertyName: "tool_calls", value: out _));
    }

    [Fact]
    public void StreamTranslator_ToolCallTag_FullyInOnePush_EmitsToolCallsDelta_AndRewritesFinishReason()
    {
        var translator = StreamTranslator();

        var input =
            Chunk(content: "<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>") +
            Chunk(finishReason: "stop");

        var output = Decode(translator.Push(ToBytes(input)));
        output += Decode(translator.Flush());

        var chunks = ParseChunks(output);
        var toolCall = Delta(chunks[0]).GetProperty("tool_calls")[0];
        Assert.Equal(expected: "get_time", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(expected: "function", actual: toolCall.GetProperty("type").GetString());
        Assert.Equal(0, actual: toolCall.GetProperty("index").GetInt32());

        Assert.Equal(expected: "tool_calls",
            actual: chunks[^1].RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.EndsWith(expectedEndString: "[DONE]", actualString: output.TrimEnd());
    }

    [Fact]
    public void StreamTranslator_ToolsTagVariant_AlsoRecognized()
    {
        var translator = StreamTranslator();

        var output =
            Decode(translator.Push(
                ToBytes(Chunk(content: "<tools>{\"name\": \"get_time\", \"arguments\": {}}</tools>"))));
        output += Decode(translator.Flush());

        var toolCall = Delta(ParseChunks(output)[0]).GetProperty("tool_calls")[0];
        Assert.Equal(expected: "get_time", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void StreamTranslator_LiteralLmStudioRepro_TagSplitAcrossManyPushCalls_StillDetected()
    {
        // The exact token-by-token content fragments captured from LM Studio's real (mis)behavior, each its
        // own separate SSE "data:" event/Push call - reconstructs to
        // "<tools>\n{\"name\": \"get_time\", \"arguments\": {}}\n</tools>".
        string[] fragments =
        [
            "<tools", ">\n", "{\"", "name", "\":", " \"", "get", "_time", "\",", " \"",
            "arguments", "\":", " {}", "}\n", "</", "tools", ">"
        ];

        var translator = StreamTranslator();
        var output = new StringBuilder();

        foreach (var fragment in fragments) output.Append(Decode(translator.Push(ToBytes(Chunk(content: fragment)))));

        output.Append(Decode(translator.Push(ToBytes(Chunk(finishReason: "stop")))));
        output.Append(Decode(translator.Flush()));

        var chunks = ParseChunks(output.ToString());
        var toolCallChunk =
            chunks.FirstOrDefault(c => Delta(c).TryGetProperty(propertyName: "tool_calls", value: out _));
        Assert.NotNull(toolCallChunk);

        var toolCall = Delta(toolCallChunk).GetProperty("tool_calls")[0];
        Assert.Equal(expected: "get_time", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(expected: "tool_calls",
            actual: chunks[^1].RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void StreamTranslator_ContentBeforeAndAfterTag_PreservedAroundToolCallsDelta()
    {
        var translator = StreamTranslator();

        var input = Chunk(content: "Sure, checking the time. ") +
                    Chunk(content: "<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>") +
                    Chunk(content: " One moment.");

        var output = Decode(translator.Push(ToBytes(input)));
        output += Decode(translator.Flush());

        var chunks = ParseChunks(output);
        Assert.Equal(expected: "Sure, checking the time.  One moment.", actual: AllContent(chunks));
        Assert.Contains(collection: chunks,
            filter: c => Delta(c).TryGetProperty(propertyName: "tool_calls", value: out _));
    }

    [Fact]
    public void StreamTranslator_TwoSequentialToolCallBlocks_ProduceTwoDistinctIndices()
    {
        var translator = StreamTranslator();

        var input =
            Chunk(content: "<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>") +
            Chunk(content: "<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"SF\"}}</tool_call>");

        var output = Decode(translator.Push(ToBytes(input)));
        output += Decode(translator.Flush());

        var toolCalls = AllToolCalls(ParseChunks(output));

        Assert.Equal(2, actual: toolCalls.Count);
        Assert.Equal(expected: "get_time",
            actual: toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(0, actual: toolCalls[0].GetProperty("index").GetInt32());
        Assert.Equal(expected: "get_weather",
            actual: toolCalls[1].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(1, actual: toolCalls[1].GetProperty("index").GetInt32());
    }

    [Fact]
    public void StreamTranslator_MalformedTagBody_FailsOpen_ForwardsRawTextUnchanged_AndLogsWarning()
    {
        var loggerMock = new Mock<ILogger>();
        var translator = StreamTranslator(loggerMock.Object);

        // Not valid JSON at all inside the tag.
        var output = Decode(translator.Push(ToBytes(Chunk(content: "<tool_call>not json</tool_call>"))));
        output += Decode(translator.Flush());

        var chunks = ParseChunks(output);
        Assert.Equal(expected: "<tool_call>not json</tool_call>", actual: AllContent(chunks));
        Assert.DoesNotContain(collection: chunks,
            filter: c => Delta(c).TryGetProperty(propertyName: "tool_calls", value: out _));
        VerifyLogsWarning(loggerMock);
    }

    [Fact]
    public void StreamTranslator_SchemaEchoedInsteadOfCall_FailsOpen_NotMistakenForACall()
    {
        // The tag's *documented* schema shape (what the system prompt itself wraps in <tools>) has no
        // top-level name/arguments - must never be mistaken for a real call.
        var loggerMock = new Mock<ILogger>();
        var translator = StreamTranslator(loggerMock.Object);

        const string schemaEcho =
            "<tools>{\"type\": \"function\", \"function\": {\"name\": \"get_time\", \"parameters\": {}}}</tools>";
        var output = Decode(translator.Push(ToBytes(Chunk(content: schemaEcho))));
        output += Decode(translator.Flush());

        Assert.DoesNotContain(collection: ParseChunks(output),
            filter: c => Delta(c).TryGetProperty(propertyName: "tool_calls", value: out _));
        VerifyLogsWarning(loggerMock);
    }

    [Fact]
    public void StreamTranslator_UnterminatedTagAtStreamEnd_FlushFailsOpen_AndLogsWarning()
    {
        var loggerMock = new Mock<ILogger>();
        var translator = StreamTranslator(loggerMock.Object);

        translator.Push(ToBytes(Chunk(content: "<tool_call>{\"name\": \"get_time\", \"arguments\": {}}")));
        var output = Decode(translator.Flush());

        Assert.Equal(expected: "<tool_call>{\"name\": \"get_time\", \"arguments\": {}}",
            actual: AllContent(ParseChunks(output)));
        VerifyLogsWarning(loggerMock);
    }

    [Fact]
    public void StreamTranslator_MultiChoiceChunk_FailsOpen_ForwardsAllChoicesUnchanged_AndLogsWarning()
    {
        // A client-supplied "n" > 1 is outside this translator's scope (its region state machine only
        // tracks a single choices[0] stream) - rewriting choices[0] and dropping every other choice would
        // silently lose client-visible data, so the whole chunk passes through with every choice intact.
        var loggerMock = new Mock<ILogger>();
        var translator = StreamTranslator(loggerMock.Object);

        const string multiChoiceEvent =
            "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"model\":\"m\"," +
            "\"choices\":[" +
            "{\"index\":0,\"delta\":{\"content\":\"<tool_call>{\\\"name\\\": \\\"get_time\\\", \\\"arguments\\\": {}}</tool_call>\"},\"finish_reason\":null}," +
            "{\"index\":1,\"delta\":{\"content\":\"second choice text\"},\"finish_reason\":null}" +
            "]}\n\n";

        var output = Decode(translator.Push(ToBytes(multiChoiceEvent)));
        output += Decode(translator.Flush());

        var choices = ParseChunks(output)[0].RootElement.GetProperty("choices");
        Assert.Equal(2, actual: choices.GetArrayLength());

        Assert.Equal(
            expected: "<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>",
            actual: choices[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.False(choices[0].GetProperty("delta").TryGetProperty(propertyName: "tool_calls", value: out _));
        Assert.Equal(expected: "second choice text",
            actual: choices[1].GetProperty("delta").GetProperty("content").GetString());

        VerifyLogsWarning(loggerMock);
    }

    [Fact]
    public void StreamTranslator_MultiChoiceEvent_PreservesNonDataLines_NotJustTheDataPayload()
    {
        // Regression test for the fail-open passthrough itself: it must forward the *original event bytes*
        // (event:/id: lines and all), not re-serialize just the parsed "data:" JSON - which would silently
        // drop any non-"data:" line.
        var translator = StreamTranslator();

        const string multiChoiceEventWithExtraLines =
            "event: message\n" +
            "id: evt-1\n" +
            "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"model\":\"m\"," +
            "\"choices\":[" +
            "{\"index\":0,\"delta\":{\"content\":\"a\"},\"finish_reason\":null}," +
            "{\"index\":1,\"delta\":{\"content\":\"b\"},\"finish_reason\":null}" +
            "]}\n\n";

        var output = Decode(translator.Push(ToBytes(multiChoiceEventWithExtraLines)));

        Assert.Contains(expectedSubstring: "event: message", actualString: output,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "id: evt-1", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(
            expectedSubstring:
            "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"},\"finish_reason\":null},{\"index\":1,\"delta\":{\"content\":\"b\"},\"finish_reason\":null}]",
            actualString: output, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TranslateResponse_NoTagsPresent_ReturnsBodyUnchanged()
    {
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"hi there"},"finish_reason":"stop"}]}""");

        Assert.Equal(expected: Decode(body), actual: Decode(BufferedTranslator().TranslateResponse(body)));
    }

    [Fact]
    public void TranslateResponse_ToolCallTagInContent_RewritesToToolCallsAndFinishReason()
    {
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>"},"finish_reason":"stop"}]}""");

        using var json = JsonDocument.Parse(BufferedTranslator().TranslateResponse(body));

        var toolCall = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal(expected: "get_time", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(expected: "tool_calls",
            actual: json.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void TranslateResponse_StrayUnbalancedBraceBeforeValidCall_StillExtractsTheCall()
    {
        // A stray "{" earlier in the region body must not stop the brace scan from finding the later,
        // well-formed call - regression test for its resume-after-unterminated-object behavior.
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"<tool_call>garbage { still garbage {\"name\": \"get_time\", \"arguments\": {}}</tool_call>"},"finish_reason":"stop"}]}""");

        using var json = JsonDocument.Parse(BufferedTranslator().TranslateResponse(body));

        var toolCall = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal(expected: "get_time", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(expected: "tool_calls",
            actual: json.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void TranslateResponse_MultipleChoices_FailsOpen_ReturnsBodyUnchanged_AndLogsWarning()
    {
        var loggerMock = new Mock<ILogger>();
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>"},"finish_reason":"stop"},{"message":{"role":"assistant","content":"second choice"},"finish_reason":"stop"}]}""");

        Assert.Equal(expected: Decode(body),
            actual: Decode(BufferedTranslator(loggerMock.Object).TranslateResponse(body)));
        VerifyLogsWarning(loggerMock);
    }

    [Fact]
    public void TranslateResponse_MalformedTagBody_FailsOpen_ReturnsContentUnchanged_AndLogsWarning()
    {
        var loggerMock = new Mock<ILogger>();
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"<tool_call>not json</tool_call>"},"finish_reason":"stop"}]}""");

        Assert.Equal(expected: Decode(body),
            actual: Decode(BufferedTranslator(loggerMock.Object).TranslateResponse(body)));
        VerifyLogsWarning(loggerMock);
    }

    // ----- The fail-open warning describes what actually happened -----

    [Fact]
    public void TranslateResponse_AProseMentionOfACloseLessOpener_WarnsNothing()
    {
        // A close-less dialect owns the rest of the message by definition, so its opener alone proves
        // nothing. Warning here would fire on every response where a coding assistant names the token
        // while explaining tool calling - routine work, and armed precisely when tools are offered.
        var (messages, logger) = CapturingLogger();
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"Mistral models open with the [TOOL_CALLS] token."},"finish_reason":"stop"}]}""");

        Assert.Equal(expected: Decode(body), actual: Decode(BufferedTranslator(logger).TranslateResponse(body)));
        Assert.Empty(messages);
    }

    [Fact]
    public void TranslateResponse_AnAttemptedCloseLessPayload_IsReportedAsSuch()
    {
        var (messages, logger) = CapturingLogger();
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"[TOOL_CALLS][{\"nome\": \"get_time\"}]"},"finish_reason":"stop"}]}""");

        BufferedTranslator(logger).TranslateResponse(body);

        Assert.Contains(expectedSubstring: "the text after [TOOL_CALLS] did not contain a valid tool call",
            actualString: Assert.Single(messages), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TranslateResponse_AnOpenerWithNoCloser_IsReportedAsUnterminated_NotAsFramed()
    {
        // "Framed by X" is false when there is no closing token, and it is the message the streaming path
        // reserves for a complete open/close pair - the two paths must describe the same response the
        // same way.
        var (messages, logger) = CapturingLogger();
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"cut off mid-call <tool_call>{\"name\": \"get_time\""},"finish_reason":"length"}]}""");

        BufferedTranslator(logger).TranslateResponse(body);

        var message = Assert.Single(messages);
        Assert.Contains(expectedSubstring: "unterminated <tool_call> block", actualString: message,
            comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "framed", actualString: message,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranslateResponse_ACompleteRegionThatYieldedNothing_NamesBothDelimiters()
    {
        var (messages, logger) = CapturingLogger();
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"<tool_call>not json</tool_call>"},"finish_reason":"stop"}]}""");

        BufferedTranslator(logger).TranslateResponse(body);

        Assert.Contains(expectedSubstring: "<tool_call>...</tool_call> did not contain a valid tool call",
            actualString: Assert.Single(messages), comparisonType: StringComparison.Ordinal);
    }

    // ----- Beyond the ported contract: the dialects the echo guard could not see -----

    [Fact]
    public void StreamTranslator_MistralCloseLessPayload_IsResolvedOnTheFinishChunk()
    {
        // [TOOL_CALLS] has no closing token by design, so the region can only be resolved when the message
        // ends. Doing that on the finish chunk - rather than in Flush - is what lets the client see
        // finish_reason "tool_calls" instead of "stop" followed by an orphaned call.
        var translator = StreamTranslator();

        var output = Decode(translator.Push(ToBytes(
            Chunk(content: "[TOOL_CALLS][{\"name\": \"get_time\", \"arguments\": {\"tz\": \"UTC\"}}]") +
            Chunk(finishReason: "stop"))));
        output += Decode(translator.Flush());

        var chunks = ParseChunks(output);
        var toolCall = AllToolCalls(chunks).Single();
        Assert.Equal(expected: "get_time", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(expected: "{\"tz\":\"UTC\"}",
            actual: toolCall.GetProperty("function").GetProperty("arguments").GetString());

        // The load-bearing part, and what distinguishes resolving here from resolving in Flush: the call
        // rides on the chunk that *carries* the finish reason, and no chunk ever says "stop". Deferring to
        // Flush would emit finish_reason "stop" first and the tool call after it, which a client reading
        // the finish reason as end-of-turn has every right to act on.
        var finishChunk = Assert.Single(
            collection: chunks,
            predicate: c =>
                c.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind != JsonValueKind.Null);
        Assert.Equal(expected: "tool_calls",
            actual: finishChunk.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.True(Delta(finishChunk).TryGetProperty(propertyName: "tool_calls", value: out _));
    }

    [Fact]
    public void StreamTranslator_Llama3ParametersKey_IsReadAsArguments()
    {
        // The Llama 3.x JSON form keys its arguments as "parameters" - the reason a hardcoded "arguments"
        // lookup silently dropped a Llama-family call even when the surrounding text was found.
        var translator = StreamTranslator();

        var output = Decode(translator.Push(ToBytes(
            Chunk(content: "<|python_tag|>{\"name\": \"get_time\", \"parameters\": {\"tz\": \"UTC\"}}") +
            Chunk(finishReason: "stop"))));
        output += Decode(translator.Flush());

        var toolCall = AllToolCalls(ParseChunks(output)).Single();
        Assert.Equal(expected: "get_time", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(expected: "{\"tz\":\"UTC\"}",
            actual: toolCall.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public void TranslateResponse_MistralPayload_IsRewritten()
    {
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"[TOOL_CALLS][{\"name\": \"get_time\", \"arguments\": {}}]"},"finish_reason":"stop"}]}""");

        using var json = JsonDocument.Parse(BufferedTranslator().TranslateResponse(body));

        var toolCall = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal(expected: "get_time", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void StreamTranslator_AnOversizedUnclosedRegion_IsAbandoned_RatherThanBufferingTheWholeResponse()
    {
        // Performance rule 4. Without the cap, one stray delimiter holds an entire response in memory and
        // delays every byte of it - so past the cap the text is flushed as ordinary content instead.
        var loggerMock = new Mock<ILogger>();
        var translator = StreamTranslator(loggerMock.Object);
        var runaway = new string('x', 70_000);

        var output = Decode(translator.Push(ToBytes(Chunk(content: "<tool_call>" + runaway))));

        Assert.Equal(expected: "<tool_call>" + runaway, actual: AllContent(ParseChunks(output)));
        VerifyLogsWarning(loggerMock);
    }

    // ----- Tier 4: what the response taught us about the model -----

    [Fact]
    public void StreamTranslator_NativeToolCallsDelta_IsRecordedAsOpenAiNative_AndLeftUntouched()
    {
        var store = new FakeToolCallCapabilityStore();
        var translator = StreamTranslator(store: store);

        const string nativeEvent =
            "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"model\":\"m\",\"choices\":[{\"index\":0," +
            "\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_real\",\"type\":\"function\",\"function\":{\"name\":\"get_time\",\"arguments\":\"{}\"}}]}," +
            "\"finish_reason\":null}]}\n\n";

        var output = Decode(translator.Push(ToBytes(nativeEvent)));
        output += Decode(translator.Flush());

        Assert.Contains(expectedSubstring: "call_real", actualString: output, comparisonType: StringComparison.Ordinal);

        var recorded = Assert.Single(store.Recorded);
        Assert.Equal(expected: "openai-native", actual: recorded.Dialect);
        Assert.Equal(expected: DetectionConfidence.Observed, actual: recorded.Confidence);
    }

    [Fact]
    public void StreamTranslator_OnceNative_LaterTagShapedProse_IsNotRewritten()
    {
        // The false positive per-model detection exists to prevent: a capable model that already emitted a
        // real call and then *writes about* the syntax must not have its prose turned into a second call.
        var translator = StreamTranslator(store: new FakeToolCallCapabilityStore());

        const string nativeEvent =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_real\",\"type\":\"function\",\"function\":{\"name\":\"get_time\",\"arguments\":\"{}\"}}]},\"finish_reason\":null}]}\n\n";

        var output = Decode(translator.Push(ToBytes(nativeEvent)));
        output += Decode(translator.Push(
            ToBytes(Chunk(content: "<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>"))));
        output += Decode(translator.Flush());

        // Asserted on the decoded JSON content, never on the raw SSE bytes. System.Text.Json's default
        // encoder emits '<' as a Unicode escape sequence rather than as the literal character, so a
        // forwarded tag is present in the payload but is not spelled "<tool_call>" anywhere on the wire.
        // A raw-text assertion here would therefore pass or fail for reasons that have nothing to do with
        // whether the tag was rewritten.
        var chunks = ParseChunks(output);
        Assert.Contains(expectedSubstring: "<tool_call>", actualString: AllContent(chunks),
            comparisonType: StringComparison.Ordinal);
        Assert.Single(AllToolCalls(chunks));
    }

    [Fact]
    public void StreamTranslator_AMatchedDialect_IsRecordedOnce_NotOncePerCall()
    {
        var store = new FakeToolCallCapabilityStore();
        var translator = StreamTranslator(store: store);

        var input =
            Chunk(content: "<tool_call>{\"name\": \"a\", \"arguments\": {}}</tool_call>") +
            Chunk(content: "<tool_call>{\"name\": \"b\", \"arguments\": {}}</tool_call>");

        translator.Push(ToBytes(input));
        translator.Flush();

        var recorded = Assert.Single(store.Recorded);
        Assert.Equal(expected: "hermes", actual: recorded.Dialect);
        Assert.Equal(expected: DetectionConfidence.Observed, actual: recorded.Confidence);
        Assert.Equal(1, actual: recorded.ObservationCount);
    }

    [Fact]
    public void AProseAnswer_RecordsNothing_EvenThoughToolsWereOffered()
    {
        // The overwhelmingly common response to a tools-carrying request is prose: the model simply had no
        // reason to call a tool. That is not evidence of anything, and counting it against the model is how
        // a well-behaved model would get condemned to emulation for answering a question.
        var store = new FakeToolCallCapabilityStore();

        BufferedTranslator(store: store).TranslateResponse(
            ToBytes(
                """{"choices":[{"message":{"role":"assistant","content":"The time is 4pm."},"finish_reason":"stop"}]}"""));

        Assert.Empty(store.Recorded);
    }

    [Fact]
    public void ASettledClassification_IsNotRewritten_OnEveryResponse()
    {
        // A plan built for an already-observed model does not observe: re-recording the identical row would
        // be a database write and a full cache reload per response, on the request path.
        var store = new FakeToolCallCapabilityStore();
        var translator = new ToolCallNormalizingTranslator(
            plan: new ToolCallNormalizationPlan(ProviderKey: "lmstudio", ModelName: "m",
                Candidates: [ToolCallDialectRegistry.Hermes], false),
            capabilityStore: store,
            logger: Mock.Of<ILogger>());

        translator.TranslateResponse(ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"<tool_call>{\"name\": \"a\", \"arguments\": {}}</tool_call>"},"finish_reason":"stop"}]}"""));

        Assert.Empty(store.Recorded);
    }

    [Fact]
    public void TranslateResponse_MessageAlreadyHasToolCalls_IsRecordedAsNative_AndNeverOverwritten()
    {
        var store = new FakeToolCallCapabilityStore();
        var body = ToBytes(
            """{"choices":[{"message":{"role":"assistant","content":"<tool_call>{\"name\": \"get_time\", \"arguments\": {}}</tool_call>","tool_calls":[{"id":"call_real","type":"function","function":{"name":"real_call","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""");

        Assert.Equal(expected: Decode(body), actual: Decode(BufferedTranslator(store: store).TranslateResponse(body)));
        Assert.Equal(expected: "openai-native", actual: Assert.Single(store.Recorded).Dialect);
    }

    // ----- End-to-end wiring through the real ProxyMiddleware -----

    [Fact]
    public async Task ProxyMiddleware_RequestCarryingTools_RewritesEchoedToolCall_InStreamedResponse()
    {
        // The original repro, now armed by the request's own "tools" field rather than by a provider flag.
        var body = await RunThroughMiddlewareAsync(
            requestBody:
            """{"model":"qwen2.5.1-coder-7b-instruct","messages":[{"role":"user","content":"time?"}],"stream":true,"tools":[{"type":"function","function":{"name":"get_time","parameters":{"type":"object","properties":{}}}}]}""");

        // Both spellings the tag could appear in: the literal one, as the stub upstream wrote it and as a
        // byte-for-byte passthrough would preserve it, and the Unicode-escaped one System.Text.Json emits
        // when a chunk is re-serialized. Checking only the literal form would let this pass merely because
        // the response went through JSON, without the tag ever having been rewritten into a tool call.
        Assert.DoesNotContain(expectedSubstring: "<tool_call>", actualString: body,
            comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "\\u003Ctool_call", actualString: body,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "\"tool_calls\"", actualString: body,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "get_time", actualString: body, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyMiddleware_RequestWithNoTools_LeavesTagShapedTextAlone()
    {
        // Performance rule 1, and the false-positive guard: no tool was offered, so a <tool_call> block in
        // the reply is the model *explaining* the syntax - routine work for a coding assistant.
        var body = await RunThroughMiddlewareAsync(
            requestBody:
            """{"model":"qwen2.5.1-coder-7b-instruct","messages":[{"role":"user","content":"explain qwen tool syntax"}],"stream":true}""");

        Assert.Contains(expectedSubstring: "<tool_call>", actualString: body, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "\"tool_calls\"", actualString: body,
            comparisonType: StringComparison.Ordinal);
    }

    // ----- An empty tool_calls array is not a native call -----

    [Fact]
    public void BufferedTranslator_AnEmptyToolCallsArray_IsNotTreatedAsANativeCall()
    {
        // Captured from a live LM Studio: it emits "tool_calls": [] on *every* non-streaming response,
        // prose included. Reading that as a native call disabled normalization for the response in hand -
        // and recorded openai-native at Observed confidence, the highest automatic tier, which no later
        // template or model-id scan may overwrite. One non-streaming request permanently disabled tool-call
        // normalization for that model, including for the streaming clients that were working.
        var store = new FakeToolCallCapabilityStore();
        var translator = BufferedTranslator(store: store);

        var result = translator.TranslateResponse(ToBytes(
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"<tool_call>{\"name\":\"get_time\",\"arguments\":{}}</tool_call>","tool_calls":[]},"finish_reason":"stop"}]}"""));

        using var parsed = JsonDocument.Parse(result);
        var message = parsed.RootElement.GetProperty("choices")[0].GetProperty("message");

        Assert.Equal(expected: "get_time",
            actual: message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.DoesNotContain(collection: store.Recorded, filter: r => r.Dialect == "openai-native");
    }

    [Fact]
    public void BufferedTranslator_AProseReplyCarryingAnEmptyToolCallsArray_ClassifiesNothing()
    {
        // The same server shape on an ordinary answer. Recording openai-native here would be worse than
        // the missed rewrite above: it is permanent, and it is wrong about a model nobody has yet observed
        // calling a tool at all.
        var store = new FakeToolCallCapabilityStore();

        BufferedTranslator(store: store).TranslateResponse(ToBytes(
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"Tokyo is nine hours ahead of UTC.","tool_calls":[]},"finish_reason":"stop"}]}"""));

        Assert.Empty(store.Recorded);
    }

    [Fact]
    public void StreamTranslator_AnEmptyToolCallsDelta_DoesNotDisarmTheScanner()
    {
        // LM Studio's streaming deltas omit the field today, so this path was not broken - but the two
        // paths must agree about what a native call is.
        var store = new FakeToolCallCapabilityStore();
        var translator = StreamTranslator(store: store);

        var sse = Decode(translator.Push(ToBytes(
                      "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[],\"content\":\"<tool_call>{\\\"name\\\": \\\"get_time\\\", \\\"arguments\\\": {}}</tool_call>\"},\"finish_reason\":null}]}\n\n")))
                  + Decode(translator.Flush());

        var chunks = ParseChunks(sse);
        Assert.Equal(expected: "get_time",
            actual: AllToolCalls(chunks)[0].GetProperty("function").GetProperty("name").GetString());
        Assert.DoesNotContain(collection: store.Recorded, filter: r => r.Dialect == "openai-native");
    }

    // ----- Test helpers -----

    private static ToolCallNormalizationPlan UnionPlan()
    {
        return new ToolCallNormalizationPlan(ProviderKey: "lmstudio", ModelName: "qwen2.5.1-coder-7b-instruct",
            Candidates: ToolCallDialectRegistry.ScannableDialects, true);
    }

    private static ToolCallNormalizingStreamTranslator StreamTranslator(
        ILogger? logger = null, IToolCallCapabilityStore? store = null)
    {
        return new ToolCallNormalizingStreamTranslator(plan: UnionPlan(), capabilityStore: store,
            logger: logger ?? Mock.Of<ILogger>());
    }

    private static ToolCallNormalizingTranslator BufferedTranslator(
        ILogger? logger = null, IToolCallCapabilityStore? store = null)
    {
        return new ToolCallNormalizingTranslator(plan: UnionPlan(), capabilityStore: store,
            logger: logger ?? Mock.Of<ILogger>());
    }

    /// <summary>
    /// Drives one streamed request through the real middleware against a stub upstream that echoes a Hermes-framed
    /// tool call.
    /// </summary>
    private static async Task<string> RunThroughMiddlewareAsync(string requestBody)
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "qwen2.5.1-coder-7b-instruct",
            providerModelId: "qwen2.5.1-coder-7b-instruct",
            baseUrl: "http://127.0.0.1:1234/v1",
            providerName: "lmstudio");

        const string echoedSse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"<tool_call>{\\\"name\\\": \\\"get_time\\\", \\\"arguments\\\": {}}</tool_call>\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: echoedSse, encoding: Encoding.UTF8, mediaType: "text/event-stream")
        }));

        using var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
                modelRouteResolver: resolver),
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var requestBytes = ToBytes(requestBody);
        context.Request.Body = new MemoryStream(requestBytes);
        context.Request.ContentLength = requestBytes.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        context.Response.Body.Position = 0;
        return await new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8).ReadToEndAsync(TestContext
            .Current.CancellationToken);
    }

    private static string Chunk(string? content = null, string? finishReason = null)
    {
        var delta = content is not null ? $"{{\"content\":{JsonSerializer.Serialize(content)}}}" : "{}";
        var finish = finishReason is not null ? $"\"{finishReason}\"" : "null";
        return
            $"data: {{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"model\":\"m\",\"choices\":[{{\"index\":0,\"delta\":{delta},\"finish_reason\":{finish}}}]}}\n\n";
    }

    private static byte[] ToBytes(string s)
    {
        return Encoding.UTF8.GetBytes(s);
    }

    private static string Decode(byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }

    private static JsonElement Delta(JsonDocument chunk)
    {
        return chunk.RootElement.GetProperty("choices")[0].GetProperty("delta");
    }

    private static string AllContent(IEnumerable<JsonDocument> chunks)
    {
        return string.Concat(chunks
            .Select(Delta)
            .Where(d => d.TryGetProperty(propertyName: "content", value: out _))
            .Select(d => d.GetProperty("content").GetString()));
    }

    private static List<JsonElement> AllToolCalls(IEnumerable<JsonDocument> chunks)
    {
        return
        [
            .. chunks
                .Select(Delta)
                .Where(d => d.TryGetProperty(propertyName: "tool_calls", value: out _))
                .SelectMany(d => d.GetProperty("tool_calls").EnumerateArray())
        ];
    }

    private static List<JsonDocument> ParseChunks(string sse)
    {
        return
        [
            .. sse.Split(separator: "\n\n", options: StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith(value: "data: ", comparisonType: StringComparison.Ordinal))
                .Select(l => l["data: ".Length..])
                .Where(l => l != "[DONE]")
                .Select(l => JsonDocument.Parse(l))
        ];
    }

    /// <summary>
    /// A logger that records each formatted warning, for the tests that assert on <em>which</em> warning
    /// was chosen rather than merely that one was logged.
    /// </summary>
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

    private static void VerifyLogsWarning(Mock<ILogger> loggerMock)
    {
        loggerMock.Verify(
            expression: logger => logger.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times: Times.AtLeastOnce);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}