using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Coverage for the Anthropic retrofit of unified API translation
/// (<c>docs/router/unified-api-translation.md</c> §4.4): unlike every other translated provider,
/// "anthropic" is dual-mode - real Claude Code production traffic already speaks Anthropic's native
/// Messages API directly and must keep passing through unchanged, while a client sending an
/// OpenAI-shaped request gets translated to and from that same native shape.
/// <see cref="AnthropicPayloadTranslator.ShouldTranslate"/> is the request-path-based seam that tells
/// the two apart. These tests drive the real <see cref="ProxyMiddleware"/> with the translator
/// registered and a stubbed upstream returning real-Anthropic-shaped fixtures (mock harness, per the
/// plan - no live Anthropic call).
/// </summary>
public class AnthropicProviderTests
{
    private const string AnthropicBaseUrl = "https://api.anthropic.com";
    private const string AnthropicApiKey = "test-anthropic-key";

    private static IModelRouteResolver AnthropicResolver() => ModelRouteResolverTestFactory.Create(
        modelName: "claude-sonnet-5",
        providerModelId: "claude-sonnet-5",
        baseUrl: AnthropicBaseUrl,
        authHeaderName: "x-api-key",
        authHeaderScheme: string.Empty,
        apiKey: AnthropicApiKey,
        providerName: "anthropic");

    private static ProxyMiddleware BuildMiddleware(HttpMessageHandler handler, ITelemetryPublisher? telemetry = null)
    {
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), AnthropicResolver());
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new AnthropicPayloadTranslator(),
        };

        return new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor,
            new HttpClient(handler),
            telemetryPublisher: telemetry,
            translators: translators);
    }

    private static DefaultHttpContext BuildContext(string requestPath, string requestBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = requestPath;
        var bytes = Encoding.UTF8.GetBytes(requestBody);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadResponse(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task NativeMessagesPath_PassesThroughUnchanged_NoTranslation()
    {
        // The one path real Claude Code production traffic depends on: an already-Anthropic-shaped
        // request to /v1/messages must never be touched by the translator, even though "anthropic" now
        // has one registered. This is the regression test for §4.4's highest-risk requirement.
        JsonDocument? forwardedBody = null;
        Uri? forwardedUri = null;

        const string nativeAnthropicResponse = """
            {
              "id": "msg_01",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-5",
              "content": [ { "type": "text", "text": "native response" } ],
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 10, "output_tokens": 5 }
            }
            """;

        var handler = new DelegatingHandlerStub(async request =>
        {
            forwardedUri = request.RequestUri;
            forwardedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nativeAnthropicResponse, Encoding.UTF8, "application/json"),
            };
        });

        var middleware = BuildMiddleware(handler);
        const string nativeRequestBody = """
            {"model":"claude-sonnet-5","system":"be brief","messages":[{"role":"user","content":"hi"}],"max_tokens":1024}
            """;
        var context = BuildContext("/v1/messages", nativeRequestBody);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal("https://api.anthropic.com/v1/messages", forwardedUri!.ToString());

        // The forwarded body is byte-for-byte the same JSON shape the client sent - not re-encoded by a
        // translator (RequestInterceptor only rewrites "model"; nothing else is touched).
        Assert.Equal("be brief", forwardedBody!.RootElement.GetProperty("system").GetString());
        Assert.False(forwardedBody.RootElement.TryGetProperty("stream", out _));

        // The response reaching the client is Anthropic's own native shape, not translated to OpenAI's.
        var responseBody = ReadResponse(context);
        using var response = JsonDocument.Parse(responseBody);
        Assert.Equal("message", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("native response", response.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(response.RootElement.TryGetProperty("choices", out _), "A native passthrough response must not be OpenAI-shaped.");
    }

    [Fact]
    public async Task NonStreaming_TranslatesOpenAiRequestToAnthropic_AndAnthropicResponseBackToOpenAi()
    {
        JsonDocument? forwardedBody = null;
        Uri? forwardedUri = null;
        string? authHeader = null;

        var handler = new DelegatingHandlerStub(async request =>
        {
            forwardedUri = request.RequestUri;
            authHeader = request.Headers.TryGetValues("x-api-key", out var values) ? values.First() : null;
            forwardedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());

            const string anthropicResponse = """
                {
                  "id": "msg_01ABC",
                  "type": "message",
                  "role": "assistant",
                  "model": "claude-sonnet-5",
                  "content": [ { "type": "text", "text": "Hello from Claude." } ],
                  "stop_reason": "end_turn",
                  "usage": { "input_tokens": 8, "output_tokens": 4 }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(anthropicResponse, Encoding.UTF8, "application/json"),
            };
        });

        var capturing = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware(handler, capturing);

        var context = BuildContext("/v1/chat/completions", """
            {"model":"claude-sonnet-5","messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"}]}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // --- Request was translated to Anthropic's URL + shape ---
        Assert.Equal("https://api.anthropic.com/v1/messages", forwardedUri!.ToString());
        Assert.Equal(AnthropicApiKey, authHeader);

        var root = forwardedBody!.RootElement;
        Assert.False(root.TryGetProperty("messages", out var messagesCheck) && messagesCheck.EnumerateArray().Any(m => m.TryGetProperty("role", out var r) && r.GetString() == "system"),
            "A 'system' role message must not leak into Anthropic's messages array.");
        Assert.Equal("be brief", root.GetProperty("system").GetString());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        var messages = root.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hi", messages[0].GetProperty("content")[0].GetProperty("text").GetString());

        // --- Response was translated back to OpenAI's shape ---
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        var choice = openAi.RootElement.GetProperty("choices")[0];
        Assert.Equal("Hello from Claude.", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("chat.completion", openAi.RootElement.GetProperty("object").GetString());

        // --- Usage was parsed via the OpenAI-shaped translated buffer (telemetryShapeProvider="openai") ---
        var published = await capturing.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, published.PromptTokens);
        Assert.Equal(4, published.CompletionTokens);
    }

    [Fact]
    public async Task NonStreaming_ToolCall_TranslatesToolsAndToolUse_AndToolUseResponse()
    {
        JsonDocument? forwardedBody = null;

        var handler = new DelegatingHandlerStub(async request =>
        {
            forwardedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());

            const string anthropicResponse = """
                {
                  "id": "msg_02",
                  "type": "message",
                  "role": "assistant",
                  "model": "claude-sonnet-5",
                  "content": [ { "type": "tool_use", "id": "toolu_01", "name": "get_weather", "input": { "city": "SF" } } ],
                  "stop_reason": "tool_use",
                  "usage": { "input_tokens": 20, "output_tokens": 6 }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(anthropicResponse, Encoding.UTF8, "application/json"),
            };
        });

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("/v1/chat/completions", """
            {"model":"claude-sonnet-5","messages":[{"role":"user","content":"weather in SF?"}],"tools":[{"type":"function","function":{"name":"get_weather","description":"Get weather","parameters":{"type":"object","properties":{"city":{"type":"string"}}}}}]}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // OpenAI tools -> Anthropic {name, description, input_schema}.
        var tool = forwardedBody!.RootElement.GetProperty("tools")[0];
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.Equal("string", tool.GetProperty("input_schema").GetProperty("properties").GetProperty("city").GetProperty("type").GetString());

        // Anthropic tool_use -> OpenAI tool_calls, finish_reason tool_calls, id preserved for round trip.
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        var message = openAi.RootElement.GetProperty("choices")[0].GetProperty("message");
        var toolCall = message.GetProperty("tool_calls")[0];
        Assert.Equal("toolu_01", toolCall.GetProperty("id").GetString());
        Assert.Equal("get_weather", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Contains("SF", toolCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool_calls", openAi.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void TranslateRequest_ToolResultMessage_BecomesUserTurnToolResultBlock()
    {
        var translator = new AnthropicPayloadTranslator();
        var translated = translator.TranslateRequest(Encoding.UTF8.GetBytes("""
            {"model":"claude-sonnet-5","messages":[
                {"role":"user","content":"weather in SF?"},
                {"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"SF\"}"}}]},
                {"role":"tool","tool_call_id":"call_1","content":"72F and sunny"}
            ]}
            """));

        using var json = JsonDocument.Parse(translated);
        var messages = json.RootElement.GetProperty("messages");

        // assistant turn: tool_use block with the OpenAI tool_call's id preserved.
        var assistantTurn = messages[1];
        Assert.Equal("assistant", assistantTurn.GetProperty("role").GetString());
        var toolUse = assistantTurn.GetProperty("content")[0];
        Assert.Equal("tool_use", toolUse.GetProperty("type").GetString());
        Assert.Equal("call_1", toolUse.GetProperty("id").GetString());
        Assert.Equal("SF", toolUse.GetProperty("input").GetProperty("city").GetString());

        // tool result -> a user-role tool_result block referencing the same id.
        var toolResultTurn = messages[2];
        Assert.Equal("user", toolResultTurn.GetProperty("role").GetString());
        var toolResultBlock = toolResultTurn.GetProperty("content")[0];
        Assert.Equal("tool_result", toolResultBlock.GetProperty("type").GetString());
        Assert.Equal("call_1", toolResultBlock.GetProperty("tool_use_id").GetString());
        Assert.Equal("72F and sunny", toolResultBlock.GetProperty("content").GetString());
    }

    [Fact]
    public void TranslateRequest_ToolResultMissingCallId_FallsBackToLabeledTextBlock_NotEmptyToolUseId()
    {
        // A legacy role:"function" message (or a malformed/partial client payload) can omit
        // tool_call_id. Anthropic requires tool_use_id on every tool_result block, so emitting one with
        // an empty id would produce an invalid upstream request Anthropic 400s on - this must fall back
        // to a plain text block instead.
        var translator = new AnthropicPayloadTranslator();
        var translated = translator.TranslateRequest(Encoding.UTF8.GetBytes("""
            {"model":"claude-sonnet-5","messages":[
                {"role":"user","content":"weather in SF?"},
                {"role":"function","name":"get_weather","content":"72F and sunny"}
            ]}
            """));

        using var json = JsonDocument.Parse(translated);
        var messages = json.RootElement.GetProperty("messages");

        // Both messages map to Anthropic's "user" role and are consecutive, so they merge into one turn
        // (Anthropic's alternation requirement) - the fallback block is the second content block on it.
        Assert.Equal(1, messages.GetArrayLength());
        var turn = messages[0];
        Assert.Equal("user", turn.GetProperty("role").GetString());
        var block = turn.GetProperty("content")[1];
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.False(block.TryGetProperty("tool_use_id", out _), "A fallback block must not claim a tool-result linkage that doesn't exist.");
        Assert.Contains("72F and sunny", block.GetProperty("text").GetString());
    }

    [Fact]
    public void TranslateResponse_ThinkingBlock_MapsToReasoningContentAndThinkingBlocks()
    {
        var translator = new AnthropicPayloadTranslator();
        const string anthropicResponse = """
            {
              "id": "msg_03",
              "model": "claude-sonnet-5",
              "content": [
                { "type": "thinking", "thinking": "Let me work through this.", "signature": "sig-abc" },
                { "type": "text", "text": "The answer is 4." }
              ],
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 5, "output_tokens": 9 }
            }
            """;

        var translated = translator.TranslateResponse(Encoding.UTF8.GetBytes(anthropicResponse));
        using var json = JsonDocument.Parse(translated);
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");

        Assert.Equal("Let me work through this.", message.GetProperty("reasoning_content").GetString());
        Assert.Equal("The answer is 4.", message.GetProperty("content").GetString());
        var thinkingBlock = message.GetProperty("thinking_blocks")[0];
        Assert.Equal("thinking", thinkingBlock.GetProperty("type").GetString());
        Assert.Equal("sig-abc", thinkingBlock.GetProperty("signature").GetString());
    }

    [Fact]
    public void TranslateResponse_UsageWithCacheTokens_EnrichesToInclusivePromptTokensWithDetails()
    {
        // docs/router/openai-format-usage-accuracy-plan.md §5.1: prompt_tokens becomes the inclusive
        // total (input + cache_creation + cache_read), with prompt_tokens_details.cached_tokens broken
        // out and the raw Anthropic components riding along as extension fields.
        var translator = new AnthropicPayloadTranslator();
        const string anthropicResponse = """
            {
              "id": "msg_07",
              "model": "claude-sonnet-5",
              "content": [ { "type": "text", "text": "hi" } ],
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 50, "output_tokens": 10, "cache_creation_input_tokens": 30, "cache_read_input_tokens": 200 }
            }
            """;

        var translated = translator.TranslateResponse(Encoding.UTF8.GetBytes(anthropicResponse));
        using var json = JsonDocument.Parse(translated);
        var usage = json.RootElement.GetProperty("usage");

        Assert.Equal(280, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(10, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(290, usage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(200, usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(30, usage.GetProperty("cache_creation_input_tokens").GetInt32());
        Assert.Equal(200, usage.GetProperty("cache_read_input_tokens").GetInt32());
    }

    [Fact]
    public void TranslateResponse_UsageWithoutCacheTokens_EmitsLegacyTwoFieldShape()
    {
        var translator = new AnthropicPayloadTranslator();
        const string anthropicResponse = """
            {
              "id": "msg_08",
              "model": "claude-sonnet-5",
              "content": [ { "type": "text", "text": "hi" } ],
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 8, "output_tokens": 4 }
            }
            """;

        var translated = translator.TranslateResponse(Encoding.UTF8.GetBytes(anthropicResponse));
        using var json = JsonDocument.Parse(translated);
        var usage = json.RootElement.GetProperty("usage");

        Assert.Equal(8, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(4, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("total_tokens").GetInt32());
        Assert.False(usage.TryGetProperty("prompt_tokens_details", out _));
        Assert.False(usage.TryGetProperty("cache_creation_input_tokens", out _));
        Assert.False(usage.TryGetProperty("cache_read_input_tokens", out _));
    }

    [Fact]
    public void TryExtractEmbeddedError_AnthropicErrorShape_ExtractsTypeAndMessage()
    {
        const string anthropicError = """
            {"type":"error","error":{"type":"invalid_request_error","message":"messages: at least one message is required"}}
            """;

        var extracted = AnthropicPayloadTranslator.TryExtractEmbeddedError(
            Encoding.UTF8.GetBytes(anthropicError), out var errorType, out var message);

        Assert.True(extracted);
        Assert.Equal("invalid_request_error", errorType);
        Assert.Equal("messages: at least one message is required", message);
    }

    [Theory]
    [InlineData("""{"reason":"unrecognized_shape"}""")]
    [InlineData("""{"type":"error","error":{"type":"invalid_request_error","message":""}}""")]
    [InlineData("not json")]
    public void TryExtractEmbeddedError_NotAnAnthropicErrorShape_ReturnsFalse(string body)
    {
        var extracted = AnthropicPayloadTranslator.TryExtractEmbeddedError(
            Encoding.UTF8.GetBytes(body), out _, out _);

        Assert.False(extracted);
    }

    [Fact]
    public async Task Streaming_UsageWithCacheTokens_EnrichesTerminalChunkUsage()
    {
        const string anthropicSse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_09\",\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tokens\":50,\"cache_creation_input_tokens\":30,\"cache_read_input_tokens\":200}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":10}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(anthropicSse, Encoding.UTF8, "text/event-stream"),
        }));

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("/v1/chat/completions", """
            {"model":"claude-sonnet-5","messages":[{"role":"user","content":"hi"}],"stream":true}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var body = ReadResponse(context);
        var lastData = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal) && l != "data: [DONE]")
            .Select(l => l["data: ".Length..])
            .Last();
        using var lastChunk = JsonDocument.Parse(lastData);
        var usage = lastChunk.RootElement.GetProperty("usage");

        Assert.Equal(280, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(10, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(200, usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(30, usage.GetProperty("cache_creation_input_tokens").GetInt32());
    }

    [Fact]
    public async Task NonStreaming_TranslatedAnthropicResponseWithCacheTokens_LedgerRecordsCacheTokensViaNativeTap()
    {
        // docs/router/openai-format-usage-accuracy-plan.md §4: telemetry for a translated Anthropic
        // response reads cache tokens from the native tap - asserted here via the values that reach
        // IBudgetEnforcer.RecordUsageAsync, the ledger write telemetry feeds.
        var handler = new DelegatingHandlerStub(_ =>
        {
            const string anthropicResponse = """
                {
                  "id": "msg_10",
                  "model": "claude-sonnet-5",
                  "content": [ { "type": "text", "text": "hi" } ],
                  "stop_reason": "end_turn",
                  "usage": { "input_tokens": 50, "output_tokens": 10, "cache_creation_input_tokens": 30, "cache_read_input_tokens": 200 }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(anthropicResponse, Encoding.UTF8, "application/json"),
            });
        });

        var budgetStore = new Mock<TotallyHot.ArcRouter.PriceCatalog.IBudgetEnforcer>();
        int? capturedCacheCreation = null;
        int? capturedCacheRead = null;
        budgetStore
            .Setup(b => b.RecordUsageAsync(
                It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<string, decimal?, int?, int?, int?, int?, DateTimeOffset, CancellationToken>(
                (_, _, _, _, cacheCreation, cacheRead, _, _) =>
                {
                    capturedCacheCreation = cacheCreation;
                    capturedCacheRead = cacheRead;
                })
            .Returns(Task.CompletedTask);

        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), AnthropicResolver());
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new AnthropicPayloadTranslator(),
        };
        var middleware = new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor,
            new HttpClient(handler),
            translators: translators,
            budgetStore: budgetStore.Object);

        var context = BuildContext("/v1/chat/completions", """
            {"model":"claude-sonnet-5","messages":[{"role":"user","content":"hi"}]}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(30, capturedCacheCreation);
        Assert.Equal(200, capturedCacheRead);
    }

    [Fact]
    public async Task NonStreaming_AnthropicResponseWithCacheTokens_PublishesTelemetryEventCarryingBothCacheCounts()
    {
        var handler = new DelegatingHandlerStub(_ =>
        {
            const string anthropicResponse = """
                {
                  "id": "msg_11",
                  "model": "claude-sonnet-5",
                  "content": [ { "type": "text", "text": "hi" } ],
                  "stop_reason": "end_turn",
                  "usage": { "input_tokens": 50, "output_tokens": 10, "cache_creation_input_tokens": 30, "cache_read_input_tokens": 200 }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(anthropicResponse, Encoding.UTF8, "application/json"),
            });
        });

        var capturing = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware(handler, capturing);

        var context = BuildContext("/v1/chat/completions", """
            {"model":"claude-sonnet-5","messages":[{"role":"user","content":"hi"}]}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var published = await capturing.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(30, published.CacheCreationTokens);
        Assert.Equal(200, published.CacheReadTokens);
    }

    [Fact]
    public void TranslateRequest_ThinkingBlocksOnAssistantMessage_RoundTripsFirstInContent()
    {
        // A client resending a prior assistant turn (LiteLLM's reasoning_content/thinking_blocks
        // convention) must have the raw thinking_blocks - carrying the signature Anthropic requires -
        // placed first in that turn's content, ahead of any text/tool_use blocks.
        var translator = new AnthropicPayloadTranslator();
        var translated = translator.TranslateRequest(Encoding.UTF8.GetBytes("""
            {"model":"claude-sonnet-5","messages":[
                {"role":"user","content":"what is 2+2?"},
                {"role":"assistant","content":"4","thinking_blocks":[{"type":"thinking","thinking":"2+2=4","signature":"sig-xyz"}]}
            ]}
            """));

        using var json = JsonDocument.Parse(translated);
        var assistantTurn = json.RootElement.GetProperty("messages")[1];
        var blocks = assistantTurn.GetProperty("content");

        Assert.Equal("thinking", blocks[0].GetProperty("type").GetString());
        Assert.Equal("sig-xyz", blocks[0].GetProperty("signature").GetString());
        Assert.Equal("text", blocks[1].GetProperty("type").GetString());
        Assert.Equal("4", blocks[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Streaming_TranslatesAnthropicSseEvents_ToOpenAiChunks_AndEmitsDone()
    {
        Uri? forwardedUri = null;

        // Anthropic's real event framing: "event: <type>\ndata: {json}\n\n" per event.
        const string anthropicSse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_04\",\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tokens\":3}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"lo\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var handler = new DelegatingHandlerStub(request =>
        {
            forwardedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(anthropicSse, Encoding.UTF8, "text/event-stream"),
            });
        });

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("/v1/chat/completions", """
            {"model":"claude-sonnet-5","messages":[{"role":"user","content":"hi"}],"stream":true}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal("https://api.anthropic.com/v1/messages", forwardedUri!.ToString());

        var body = ReadResponse(context);
        var dataLines = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => l["data: ".Length..])
            .ToList();

        Assert.Equal("[DONE]", dataLines[^1]);

        var chunks = dataLines.Where(l => l != "[DONE]").Select(l => JsonDocument.Parse(l)).ToList();
        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c.RootElement.GetProperty("object").GetString()));

        Assert.Equal("assistant", chunks[0].RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        var assembled = string.Concat(chunks.Select(c =>
            c.RootElement.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out var content)
                ? content.GetString()
                : string.Empty));
        Assert.Equal("Hello", assembled);

        var lastChoice = chunks[^1].RootElement.GetProperty("choices")[0];
        Assert.Equal("stop", lastChoice.GetProperty("finish_reason").GetString());
        var usage = chunks[^1].RootElement.GetProperty("usage");
        Assert.Equal(3, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2, usage.GetProperty("completion_tokens").GetInt32());
    }

    [Fact]
    public async Task Streaming_ToolUseDeltas_AccumulateIntoOpenAiToolCallArguments()
    {
        const string anthropicSse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_05\",\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tokens\":10}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_9\",\"name\":\"get_weather\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"city\\\":\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"SF\\\"}\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":8}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(anthropicSse, Encoding.UTF8, "text/event-stream"),
        }));

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("/v1/chat/completions", """
            {"model":"claude-sonnet-5","messages":[{"role":"user","content":"weather in SF?"}],"stream":true}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var body = ReadResponse(context);
        var chunks = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal) && l != "data: [DONE]")
            .Select(l => JsonDocument.Parse(l["data: ".Length..]))
            .ToList();

        var argumentFragments = chunks
            .Select(c => c.RootElement.GetProperty("choices")[0].GetProperty("delta"))
            .Where(d => d.TryGetProperty("tool_calls", out _))
            .Select(d => d.GetProperty("tool_calls")[0])
            .ToList();

        Assert.Equal("toolu_9", argumentFragments[0].GetProperty("id").GetString());
        Assert.Equal("get_weather", argumentFragments[0].GetProperty("function").GetProperty("name").GetString());

        var assembledArguments = string.Concat(argumentFragments
            .Where(f => f.GetProperty("function").TryGetProperty("arguments", out _))
            .Select(f => f.GetProperty("function").GetProperty("arguments").GetString()));
        Assert.Equal("{\"city\":\"SF\"}", assembledArguments);

        Assert.Equal("tool_calls", chunks[^1].RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task Streaming_ErrorEvent_TerminatesStream_WithoutDone()
    {
        // Anthropic delivers a mid-stream failure as its own "error" event; LiteLLM raises rather than
        // swallowing it. Our translator throws, and the proxy truncates the already-committed 200
        // stream (no [DONE]) rather than pretending success.
        const string anthropicSse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_06\",\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tokens\":3}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n" +
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(anthropicSse, Encoding.UTF8, "text/event-stream"),
        }));

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("/v1/chat/completions", """
            {"model":"claude-sonnet-5","messages":[{"role":"user","content":"hi"}],"stream":true}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var body = ReadResponse(context);
        Assert.Contains("partial", body);
        Assert.DoesNotContain("[DONE]", body);
    }

    [Fact]
    public void ParseSseEvent_MultipleDataLines_JoinedWithNewline_NotConcatenatedDirectly()
    {
        // Per the SSE spec, multiple data: lines within one event must be joined with "\n", not
        // concatenated directly - otherwise a multi-line or pretty-printed JSON payload can come apart.
        // ParseSseEvent is private, so this reaches it directly via reflection rather than round-tripping
        // through JSON parsing, which is too whitespace-tolerant to reliably distinguish "joined with \n"
        // from "concatenated" for most realistic payload splits.
        var eventBytes = Encoding.UTF8.GetBytes("data: line1\ndata: line2");
        var method = typeof(AnthropicStreamTranslator).GetMethod("ParseSseEvent", BindingFlags.NonPublic | BindingFlags.Static)!;

        var (_, data) = ((string? EventType, string? Data))method.Invoke(null, [eventBytes])!;

        Assert.Equal("line1\nline2", data);
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }

    private sealed class CapturingTelemetryPublisher : ITelemetryPublisher
    {
        private readonly TaskCompletionSource<RoutingTelemetryEvent> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            _tcs.TrySetResult(telemetryEvent);
            return Task.CompletedTask;
        }

        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<RoutingTelemetryEvent> WaitForEventAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_tcs.Task, Task.Delay(timeout));
            Assert.True(ReferenceEquals(completed, _tcs.Task), "Timed out waiting for a routing telemetry event.");
            return await _tcs.Task;
        }
    }
}

