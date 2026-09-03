using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Covers constrained decoding end to end - request rewriting, envelope parsing, the buffered and streaming
/// response paths, and the arming decision that selects it.
/// <para>
/// The behavior these pin down is the one the delimiter dialects cannot offer: a model that emits an
/// unregistered framing, or no framing at all, still produces a usable call because the sampler was never
/// able to emit anything else. The live measurement behind that claim is recorded on
/// <see cref="ConstrainedToolCallRewriter"/>.
/// </para>
/// </summary>
public class ConstrainedToolCallTests
{
    private const string Provider = "lmstudio";
    private const string Model = "qwen2.5-coder-7b-instruct-ghidra-v2";

    private const string WeatherTool =
        """{"type":"function","function":{"name":"get_weather","description":"Returns the current weather for a city.","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}""";

    private const string TimeTool =
        """{"type":"function","function":{"name":"get_time","description":"Returns the current time.","parameters":{"type":"object","properties":{"timezone":{"type":"string"}},"required":["timezone"]}}}""";

    // ----- Request rewriting -----

    [Fact]
    public void Rewrite_ReplacesToolsWithAnEnvelopeSchema_AndStillDescribesThemInThePrompt()
    {
        var rewritten = Rewrite(RequestWithTools(WeatherTool));

        // Both halves are required. The schema makes a malformed call impossible; the prompt is the only
        // thing that tells the model the tool exists at all - measured, see the rewriter's own docs.
        Assert.Null(rewritten["tools"]);
        Assert.Null(rewritten["tool_choice"]);

        var schema = Assert.IsType<JsonObject>(rewritten["response_format"]);
        Assert.Equal(expected: "json_schema", actual: schema["type"]!.GetValue<string>());

        var system = SystemMessage(rewritten);
        Assert.Contains(expectedSubstring: "get_weather", actualString: system,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Returns the current weather", actualString: system,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_ConstrainsToolNames_SoAnInventedToolIsUnrepresentable()
    {
        // The property delimiter scanning can never have: a hallucinated name is not detected and rejected
        // after the fact, it is unreachable in the grammar the sampler walks.
        var rewritten = Rewrite(RequestWithTools(WeatherTool, TimeTool));

        Assert.Equal(expected: ["get_weather", "get_time"], actual: CallBranchNames(rewritten));
    }

    [Fact]
    public void Rewrite_BindsEachToolNameToItsOwnArgumentSchema()
    {
        // An earlier shape put a shared `name` enum and a `oneOf` of argument schemas side by side as
        // sibling properties. It reads as though it couples them; it does not. With no discriminator
        // between siblings, {"name":"get_weather","arguments":{"timezone":"UTC"}} satisfies every
        // constraint - the name matches the enum, the arguments match a branch of the union, and nothing
        // relates the two choices. Each call is now one self-contained oneOf branch instead.
        var branches = CallBranches(Rewrite(RequestWithTools(WeatherTool, TimeTool)));

        Assert.All(collection: branches, action: branch =>
        {
            var name = branch["properties"]!["name"]!["const"]!.GetValue<string>();
            var argumentProperties = branch["properties"]!["arguments"]!["properties"]!.AsObject()
                .Select(p => p.Key).ToList();

            // The pairing is structural: weather's branch can only carry `city`, time's only `timezone`.
            var expected = name switch
            {
                "get_weather" => "city",
                "get_time" => "timezone",
                _ => throw new InvalidOperationException($"unexpected tool '{name}'")
            };

            Assert.Equal(expected: [expected], actual: argumentProperties);
        });
    }

    [Fact]
    public void Rewrite_AToolWithNoParameters_StillGetsACallableBranch()
    {
        // Without an entry the tool would have no legal arguments shape at all, making it uncallable
        // despite being described to the model in the prompt.
        const string NoArgsTool = """{"type":"function","function":{"name":"ping","description":"Pings."}}""";

        var branch = Assert.Single(CallBranches(Rewrite(RequestWithTools(NoArgsTool))));

        Assert.Equal(expected: "ping", actual: branch["properties"]!["name"]!["const"]!.GetValue<string>());
        Assert.Equal(expected: "object", actual: branch["properties"]!["arguments"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_LeavesAClientsOwnResponseFormatAlone()
    {
        // A request can carry only one response_format, and silently replacing the caller's would corrupt a
        // response they are about to parse - a worse failure than the unreliable tool calling being fixed.
        var request = RequestWithTools(WeatherTool);
        request["response_format"] = new JsonObject { ["type"] = "json_object" };

        var rewritten = Rewrite(request);

        Assert.Equal(expected: "json_object", actual: rewritten["response_format"]!["type"]!.GetValue<string>());
        Assert.NotNull(rewritten["tools"]);
    }

    [Fact]
    public void Rewrite_WithHistoryButNoTools_StillFlattensHistory_ButConstrainsNothing()
    {
        // The follow-up turn after a tool ran. The model must be able to read its own prior turn, but there
        // is nothing to constrain it to - and a schema naming no tools would forbid every reply it could give.
        var request = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "weather?" },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "call_1",
                            ["function"] = new JsonObject
                                { ["name"] = "get_weather", ["arguments"] = """{"city":"Paris"}""" }
                        }
                    }
                },
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_1", ["content"] = "17C" }
            }
        };

        var rewritten = Rewrite(request);
        var messages = Assert.IsType<JsonArray>(rewritten["messages"]);

        Assert.Null(rewritten["response_format"]);

        // The tool result became a user message, because `tool` is exactly the role these templates lack.
        var last = Assert.IsType<JsonObject>(messages[^1]);
        Assert.Equal(expected: "user", actual: last["role"]!.GetValue<string>());
        Assert.Contains(expectedSubstring: "17C", actualString: last["content"]!.GetValue<string>(),
            comparisonType: StringComparison.Ordinal);

        // The assistant's prior call reads back as the same envelope it is asked to produce, so its own
        // transcript never teaches it a syntax that contradicts the grammar.
        var assistant = Assert.IsType<JsonObject>(messages[1]);
        Assert.Null(assistant["tool_calls"]);
        Assert.Contains(expectedSubstring: "\"tool_calls\"", actualString: assistant["content"]!.GetValue<string>(),
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "get_weather", actualString: assistant["content"]!.GetValue<string>(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_ANonChatBody_IsForwardedUntouched()
    {
        var body = Encoding.UTF8.GetBytes("""{"model":"m","prompt":"hi"}""");

        Assert.Equal(expected: body,
            actual: ConstrainedToolCallRewriter.Rewrite(openAiShapedBody: body,
                dialect: ToolCallDialectRegistry.Constrained, logger: NullLogger.Instance));
    }

    // ----- Buffered responses -----

    [Fact]
    public void ABufferedEnvelope_BecomesRealToolCalls()
    {
        var response = Translate(BufferedResponse(
            """{"content":"","tool_calls":[{"name":"get_weather","arguments":{"city":"Paris"}}]}"""));

        var message = response["choices"]![0]!["message"]!;
        var call = message["tool_calls"]![0]!;

        Assert.Equal(expected: "get_weather", actual: call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("""{"city":"Paris"}""", actual: call["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal(expected: "tool_calls", actual: response["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Null(message["content"]);
    }

    [Fact]
    public void AnEnvelopeDecliningToCall_IsUnwrappedToProse()
    {
        // The false-positive case the envelope shape exists to keep legal. Verified live: a schema forcing a
        // call would make declining unrepresentable, so the model would invent one.
        var response = Translate(BufferedResponse(
            """{"content":"Waves kiss the shore.","tool_calls":[]}"""));

        var message = response["choices"]![0]!["message"]!;

        Assert.Equal(expected: "Waves kiss the shore.", actual: message["content"]!.GetValue<string>());
        Assert.Equal(expected: "stop", actual: response["choices"]![0]!["finish_reason"]!.GetValue<string>());

        // Upstream's own empty tool_calls is left exactly as it arrived rather than stripped: it already
        // means "no calls" to every OpenAI client, so rewriting it would be a change with no reader.
        Assert.Empty(Assert.IsType<JsonArray>(message["tool_calls"]));
    }

    [Fact]
    public void ANativeToolCallsResponse_IsNeverOverwritten_AndIsRecordedAsEvidence()
    {
        // The one thing a constrained request cannot manufacture, so unlike the envelope it *is* evidence -
        // and it says this model never needed constraining.
        var store = new FakeToolCallCapabilityStore();
        var body = """
                   {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[
                     {"id":"x","type":"function","function":{"name":"get_weather","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                   """;

        var translated = TranslatorFor(store).TranslateResponse(Encoding.UTF8.GetBytes(body));

        Assert.Equal(expected: body, actual: Encoding.UTF8.GetString(translated));
        Assert.Equal(expected: "openai-native", actual: Assert.Single(store.Recorded).Dialect);
    }

    [Fact]
    public void AnEmptyNativeToolCallsArray_DoesNotCountAsANativeCall()
    {
        // LM Studio emits "tool_calls": [] on every buffered response. A plain null check read that as a
        // native call and permanently recorded openai-native at Observed confidence, silently disabling
        // normalization for the model. Found by probing a live server, not by reading.
        var store = new FakeToolCallCapabilityStore();
        var body = """
                   {"choices":[{"message":{"role":"assistant","tool_calls":[],
                     "content":"{\"content\":\"\",\"tool_calls\":[{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Paris\"}}]}"},"finish_reason":"stop"}]}
                   """;

        var translated = TranslatorFor(store).TranslateResponse(Encoding.UTF8.GetBytes(body));
        var response = JsonNode.Parse(translated)!;

        Assert.Equal(expected: "get_weather",
            actual: response["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public void AReplyThatIsNotTheEnvelope_IsForwardedAsPlainContent()
    {
        // Fail open. A degraded response the operator can see is recoverable; a dropped one is not.
        var body = BufferedResponse("I am not JSON at all.");

        Assert.Equal(expected: body,
            actual: Encoding.UTF8.GetString(TranslatorFor().TranslateResponse(Encoding.UTF8.GetBytes(body))));
    }

    // ----- Streaming -----

    [Fact]
    public void StreamedProse_ReachesTheClientIncrementally_NotAsRawJson()
    {
        // The whole reason for the incremental scanner: agent-mode clients attach tools to nearly every
        // request, so buffering the envelope would make most replies arrive in one lump - and forwarding it
        // untouched would stream `{"content":"...` into the chat window.
        var translator = TranslatorFor().CreateStreamTranslator();

        var emitted = new StringBuilder();
        foreach (var fragment in new[] { "{\"content\":\"", "Hel", "lo the", "re", "\",\"tool_calls\":[]}" })
            emitted.Append(Encoding.UTF8.GetString(translator.Push(Encoding.UTF8.GetBytes(Chunk(fragment)))));

        emitted.Append(Encoding.UTF8.GetString(translator.Flush()));

        var prose = ContentDeltas(emitted.ToString());

        Assert.Equal(expected: "Hello there", actual: prose);
        Assert.DoesNotContain(expectedSubstring: "tool_calls\\\":[]", actualString: emitted.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamedEnvelope_EmitsToolCallsOnTheFinishChunk()
    {
        var translator = TranslatorFor().CreateStreamTranslator();

        var emitted = new StringBuilder();
        emitted.Append(Encoding.UTF8.GetString(translator.Push(Encoding.UTF8.GetBytes(
            Chunk("""{"content":"","tool_calls":[{"name":"get_weather","arguments":{"city":"Paris"}}]}""")))));
        emitted.Append(Encoding.UTF8.GetString(translator.Push(Encoding.UTF8.GetBytes(FinishChunk()))));
        emitted.Append(Encoding.UTF8.GetString(translator.Flush()));

        var text = emitted.ToString();

        Assert.Contains(expectedSubstring: "\"name\":\"get_weather\"", actualString: text,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "\"finish_reason\":\"tool_calls\"", actualString: text,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamedEscapeSplitAcrossChunks_IsDecodedCorrectly()
    {
        // A \uXXXX escape needs six characters; a token boundary can land anywhere inside it.
        var translator = TranslatorFor().CreateStreamTranslator();

        var emitted = new StringBuilder();
        foreach (var fragment in new[] { "{\"content\":\"a\\u", "004", "2c\\nd", "\",\"tool_calls\":[]}" })
            emitted.Append(Encoding.UTF8.GetString(translator.Push(Encoding.UTF8.GetBytes(Chunk(fragment)))));

        emitted.Append(Encoding.UTF8.GetString(translator.Flush()));

        Assert.Equal(expected: "aBc\nd", actual: ContentDeltas(emitted.ToString()));
    }

    [Theory]
    // Split between the two halves of the pair - the case that actually broke.
    [InlineData("{\"content\":\"sky \\ud83c", "\\udf0a done\",\"tool_calls\":[]}")]
    // Split inside the second escape's hex digits, to prove the hold survives a second boundary.
    [InlineData("{\"content\":\"sky \\ud83c\\ud", "f0a done\",\"tool_calls\":[]}")]
    public void ASurrogatePairSplitAcrossChunks_SurvivesAsOneCharacter(string first, string second)
    {
        // JSON writes any non-BMP character as two consecutive \uXXXX escapes, each decoding to one UTF-16
        // char. Emitting the first alone yields a string ending in a lone surrogate, which cannot be encoded
        // - so the client renders U+FFFD. Caught live: a haiku arrived as "moonlit sky?".
        var translator = TranslatorFor().CreateStreamTranslator();

        var emitted = new StringBuilder();
        emitted.Append(Encoding.UTF8.GetString(translator.Push(Encoding.UTF8.GetBytes(Chunk(first)))));
        emitted.Append(Encoding.UTF8.GetString(translator.Push(Encoding.UTF8.GetBytes(Chunk(second)))));
        emitted.Append(Encoding.UTF8.GetString(translator.Flush()));

        var prose = ContentDeltas(emitted.ToString());

        Assert.Equal(expected: "sky \U0001F30A done", actual: prose);
        Assert.DoesNotContain('�', collection: prose);
    }

    [Fact]
    public void AContentKeyNestedInsideToolArguments_DoesNotHijackTheStream()
    {
        // Why this is a real JSON walk rather than a search for `"content":`. A tool argument may legally
        // contain that key, and streaming it as prose would leak arguments into the chat window.
        var translator = TranslatorFor().CreateStreamTranslator();

        var emitted = new StringBuilder();
        emitted.Append(Encoding.UTF8.GetString(translator.Push(Encoding.UTF8.GetBytes(Chunk(
            """{"content":"ok","tool_calls":[{"name":"get_weather","arguments":{"content":"LEAKED"}}]}""")))));
        emitted.Append(Encoding.UTF8.GetString(translator.Push(Encoding.UTF8.GetBytes(FinishChunk()))));
        emitted.Append(Encoding.UTF8.GetString(translator.Flush()));

        Assert.Equal(expected: "ok", actual: ContentDeltas(emitted.ToString()));
    }

    // ----- Arming -----

    [Fact]
    public void AnEndpointSupportingJsonSchema_PrefersConstrainedDecoding()
    {
        var store = new FakeToolCallCapabilityStore().SeedProvider(providerKey: Provider, true);

        var translator = new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true);

        var constrained = Assert.IsType<ConstrainedToolCallTranslator>(translator);
        Assert.True(condition: constrained.Plan.IsEmulating,
            userMessage: "a constrained reply is not evidence of a native dialect");
        Assert.True(condition: constrained.Plan.IsObserving,
            userMessage: "a native tool_calls reply is still worth recording");
    }

    [Fact]
    public void AnEndpointWithoutJsonSchemaSupport_FallsBackToTheScanningPath()
    {
        var store = new FakeToolCallCapabilityStore().SeedProvider(providerKey: Provider, false);

        var translator = new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true);

        Assert.IsType<ToolCallNormalizingTranslator>(translator);
    }

    [Fact]
    public void AClientsOwnResponseFormat_DisablesConstrainedDecodingButNothingElse()
    {
        var store = new FakeToolCallCapabilityStore().SeedProvider(providerKey: Provider, true);

        var translator = new ToolCallNormalizerFactory(store)
            .TryCreate(route: Route(), true, requestCarriesResponseFormat: true);

        Assert.IsType<ToolCallNormalizingTranslator>(translator);
    }

    [Fact]
    public void AModelKnownToEmitNativeToolCalls_IsNeverConstrained()
    {
        // Constraining it would replace a working native path with a rewritten one.
        var store = new FakeToolCallCapabilityStore()
            .SeedProvider(providerKey: Provider, true)
            .Seed(new ModelToolCapability(ProviderKey: Provider, ModelName: Model, Dialect: "openai-native",
                Confidence: DetectionConfidence.Observed));

        Assert.Null(new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true));
    }

    [Fact]
    public void AnOperatorPin_WinsOverTheEndpointCapability_InBothDirections()
    {
        var store = new FakeToolCallCapabilityStore().SeedProvider(providerKey: Provider, true);

        // Pinned away from constrained: honored, even though the endpoint supports it.
        store.Seed(new ModelToolCapability(ProviderKey: Provider, ModelName: Model, Dialect: "hermes",
            Confidence: DetectionConfidence.Operator));
        Assert.IsType<ToolCallNormalizingTranslator>(
            new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true));

        // Pinned onto constrained where detection said no: the documented escape hatch for the conservative
        // inference behind JsonSchemaResponseFormat.
        var unsupported = new FakeToolCallCapabilityStore()
            .SeedProvider(providerKey: Provider, false)
            .Seed(new ModelToolCapability(ProviderKey: Provider, ModelName: Model, Dialect: "constrained",
                Confidence: DetectionConfidence.Operator));

        Assert.IsType<ConstrainedToolCallTranslator>(
            new ToolCallNormalizerFactory(unsupported).TryCreate(route: Route(), true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void APinnedConstrainedModel_WhoseRequestCarriesAResponseFormat_ForwardsInsteadOfThrowing(
        bool carriesTools, bool carriesToolHistory)
    {
        // Regression: the client's response_format makes ShouldConstrain decline *before* it reaches the
        // operator-pin check, so this used to fall through to the emulation branch - which matched, because
        // `constrained` carries an instruction prompt - and then threw ArgumentException out of
        // ToolCallEmulatingTranslator's constructor, since that dialect declares no delimiters.
        // ProxyMiddleware calls TryCreate with no guard around it, so the throw reached a live request.
        //
        // Forwarding unrewritten is the right degradation: the client owns response_format, and without the
        // grammar there is nothing to read an envelope reply back with.
        var store = new FakeToolCallCapabilityStore()
            .SeedProvider(providerKey: Provider, true)
            .Seed(new ModelToolCapability(ProviderKey: Provider, ModelName: Model, Dialect: "constrained",
                Confidence: DetectionConfidence.Operator));

        IPayloadTranslator? translator = null;
        var exception = Record.Exception(() => translator = new ToolCallNormalizerFactory(store).TryCreate(
            route: Route(),
            requestCarriesTools: carriesTools,
            requestCarriesToolHistory: carriesToolHistory,
            true));

        Assert.Null(exception);
        Assert.Null(translator);
    }

    [Fact]
    public void NoDialectCarryingAPromptButNoDelimiters_CanEverReachTheEmulatingTranslator()
    {
        // The invariant behind the regression above, stated directly rather than through one scenario:
        // teaching a dialect is only half of emulation - the reply still has to be read back, and a
        // delimiter-less dialect gives the scanner nothing to find. Any future dialect shaped like
        // `constrained` must be excluded by this property, not by branch ordering.
        foreach (var dialect in ToolCallDialectRegistry.All.Where(d => d.EmulationPrompt is not null))
        {
            var store = new FakeToolCallCapabilityStore()
                .Seed(new ModelToolCapability(ProviderKey: Provider, ModelName: Model, Dialect: dialect.Name,
                    Confidence: DetectionConfidence.Operator));

            var translator = new ToolCallNormalizerFactory(store).TryCreate(
                route: Route(), true, true);

            if (translator is ToolCallEmulatingTranslator emulating)
                Assert.True(
                    condition: emulating.Dialect.IsScannable,
                    userMessage:
                    $"Dialect '{dialect.Name}' reached ToolCallEmulatingTranslator with no delimiters to read its reply back.");
        }
    }

    [Fact]
    public void ARequestOfferingNoToolsAndNoHistory_IsNotConstrained()
    {
        // Rule 1 still applies: the cheapest rewrite is the one never installed.
        var store = new FakeToolCallCapabilityStore().SeedProvider(providerKey: Provider, true);

        Assert.Null(new ToolCallNormalizerFactory(store).TryCreate(route: Route(), false));
    }

    [Fact]
    public void TheConstrainedTranslator_ReshapesTheRequestButKeepsTheClientsPath()
    {
        var store = new FakeToolCallCapabilityStore().SeedProvider(providerKey: Provider, true);

        var translator = new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true);

        Assert.IsAssignableFrom<IClientPathTranslator>(translator);
    }

    // ----- Helpers -----

    private static JsonObject Rewrite(JsonObject request)
    {
        var rewritten = ConstrainedToolCallRewriter.Rewrite(
            openAiShapedBody: Encoding.UTF8.GetBytes(request.ToJsonString()),
            dialect: ToolCallDialectRegistry.Constrained,
            logger: NullLogger.Instance);

        return Assert.IsType<JsonObject>(JsonNode.Parse(rewritten));
    }

    private static JsonObject RequestWithTools(params string[] tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools) array.Add(JsonNode.Parse(tool));

        return new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hi" } },
            ["tools"] = array
        };
    }

    private static string SystemMessage(JsonObject rewritten)
    {
        return Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rewritten["messages"])[0])["content"]!
            .GetValue<string>();
    }

    /// <summary>The per-tool <c>oneOf</c> branches the envelope constrains each call to.</summary>
    private static List<JsonObject> CallBranches(JsonObject rewritten)
    {
        var items = rewritten["response_format"]!["json_schema"]!["schema"]!["properties"]!["tool_calls"]!["items"]!;
        return [.. Assert.IsType<JsonArray>(items["oneOf"]).Select(b => Assert.IsType<JsonObject>(b))];
    }

    private static List<string> CallBranchNames(JsonObject rewritten)
    {
        return [.. CallBranches(rewritten).Select(b => b["properties"]!["name"]!["const"]!.GetValue<string>())];
    }

    private static ConstrainedToolCallTranslator TranslatorFor(FakeToolCallCapabilityStore? store = null)
    {
        return new ConstrainedToolCallTranslator(
            plan: new ToolCallNormalizationPlan(ProviderKey: Provider, ModelName: Model,
                Candidates: [ToolCallDialectRegistry.Constrained], true, true),
            dialect: ToolCallDialectRegistry.Constrained,
            capabilityStore: store,
            logger: NullLogger.Instance);
    }

    private static JsonObject Translate(string body)
    {
        return Assert.IsType<JsonObject>(
            JsonNode.Parse(TranslatorFor().TranslateResponse(Encoding.UTF8.GetBytes(body))));
    }

    private static string BufferedResponse(string content)
    {
        return new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject
                        { ["role"] = "assistant", ["content"] = content, ["tool_calls"] = new JsonArray() },
                    ["finish_reason"] = "stop"
                }
            }
        }.ToJsonString();
    }

    /// <summary>One SSE chunk carrying <paramref name="contentFragment"/> as <c>delta.content</c>.</summary>
    private static string Chunk(string contentFragment)
    {
        return $"data: {new JsonObject
        {
            ["id"] = "c1",
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["content"] = contentFragment },
                    ["finish_reason"] = null
                }
            }
        }.ToJsonString()}\n\n";
    }

    private static string FinishChunk()
    {
        return $"data: {new JsonObject
        {
            ["id"] = "c1",
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JsonArray
            {
                new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = "stop" }
            }
        }.ToJsonString()}\n\n";
    }

    /// <summary>Concatenates every <c>delta.content</c> the translator emitted, which is what a client would render.</summary>
    private static string ContentDeltas(string sse)
    {
        var prose = new StringBuilder();
        foreach (var line in sse.Split('\n'))
        {
            if (!line.StartsWith(value: "data: {", comparisonType: StringComparison.Ordinal)) continue;

            if (JsonNode.Parse(line["data: ".Length..]) is JsonObject chunk &&
                chunk["choices"] is JsonArray { Count: > 0 } choices &&
                choices[0]?["delta"]?["content"] is JsonValue value &&
                value.TryGetValue<string>(out var text))
                prose.Append(text);
        }

        return prose.ToString();
    }

    private static ResolvedModelRoute Route()
    {
        return new ResolvedModelRoute(
            ModelName: Model,
            Provider: Provider,
            ProviderModelId: Model,
            UpstreamBaseUrl: new Uri("http://127.0.0.1:1234/v1"),
            AuthHeaderName: "Authorization",
            ExtraHeaders: []);
    }
}