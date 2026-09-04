using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Coverage for the Gemini slice of unified API translation
/// (<c>docs/router/unified-api-translation.md</c> §4.3): unlike Ollama (which is already OpenAI-shaped
/// and needs no translator), Gemini's native <c>generateContent</c> API differs in URL, auth, and
/// payload shape, so <see cref="GeminiPayloadTranslator"/> rewrites the request into Gemini's shape on
/// the way out and its response back into OpenAI's shape on the way in. These tests drive the real
/// <see cref="ProxyMiddleware"/> with the translator registered and a stubbed upstream returning
/// real-Gemini-shaped fixtures (mock harness, per the plan - no live Gemini call).
/// </summary>
public class GeminiProviderTests
{
    private const string GeminiBaseUrl = "https://generativelanguage.googleapis.com";
    private const string GeminiApiKey = "test-gemini-key";

    private static IModelRouteResolver GeminiResolver()
    {
        return ModelRouteResolverTestFactory.Create(
            modelName: "gemini-2.5-pro",
            providerModelId: "gemini-2.5-pro",
            baseUrl: GeminiBaseUrl,
            authHeaderName: "x-goog-api-key",
            authHeaderScheme: string.Empty,
            apiKey: GeminiApiKey,
            providerName: "gemini");
    }

    private static ProxyMiddleware BuildMiddleware(HttpMessageHandler handler, ITelemetryPublisher? telemetry = null)
    {
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: GeminiResolver());
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = new GeminiPayloadTranslator()
        };

        return new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetry,
                Translators = translators
            }
        );
    }

    private static DefaultHttpContext BuildContext(string openAiRequestBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var bytes = Encoding.UTF8.GetBytes(openAiRequestBody);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadResponse(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task NonStreaming_TranslatesOpenAiRequestToGemini_AndGeminiResponseBackToOpenAi()
    {
        JsonDocument? forwardedBody = null;
        Uri? forwardedUri = null;
        string? authHeader = null;

        var handler = new DelegatingHandlerStub(async request =>
        {
            forwardedUri = request.RequestUri;
            authHeader = request.Headers.TryGetValues(name: "x-goog-api-key", values: out var values)
                ? values.First()
                : null;
            forwardedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());

            // Gemini's own native non-streaming response shape (ai.google.dev generateContent).
            const string geminiResponse = """
                                          {
                                            "candidates": [
                                              {
                                                "content": { "role": "model", "parts": [ { "text": "Hello from Gemini." } ] },
                                                "finishReason": "STOP",
                                                "index": 0
                                              }
                                            ],
                                            "usageMetadata": { "promptTokenCount": 8, "candidatesTokenCount": 4, "totalTokenCount": 12 },
                                            "modelVersion": "gemini-2.5-pro",
                                            "responseId": "resp-123"
                                          }
                                          """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: geminiResponse, encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
        });

        var capturing = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware(handler: handler, telemetry: capturing);

        var context = BuildContext("""
                                   {"model":"gemini-2.5-pro","messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"}]}
                                   """);

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        // --- Request was translated to Gemini's URL + shape ---
        Assert.Equal(expected: "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent",
            actual: forwardedUri!.ToString());
        Assert.Equal(expected: GeminiApiKey, actual: authHeader);

        var root = forwardedBody!.RootElement;
        Assert.False(condition: root.TryGetProperty(propertyName: "messages", value: out _),
            userMessage: "OpenAI 'messages' must not leak into the Gemini body.");
        Assert.False(condition: root.TryGetProperty(propertyName: "stream", value: out _),
            userMessage: "'stream' must not be forwarded to Gemini.");
        Assert.Equal(expected: "be brief",
            actual: root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var contents = root.GetProperty("contents");
        Assert.Equal(expected: "user", actual: contents[0].GetProperty("role").GetString());
        Assert.Equal(expected: "hi", actual: contents[0].GetProperty("parts")[0].GetProperty("text").GetString());

        // --- Response was translated back to OpenAI's shape ---
        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        var choice = openAi.RootElement.GetProperty("choices")[0];
        Assert.Equal(expected: "Hello from Gemini.",
            actual: choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(expected: "stop", actual: choice.GetProperty("finish_reason").GetString());
        Assert.Equal(expected: "chat.completion", actual: openAi.RootElement.GetProperty("object").GetString());

        // --- Usage was parsed from Gemini's usageMetadata (via the OpenAI-shaped translated buffer) ---
        var published = await capturing.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, actual: published.PromptTokens);
        Assert.Equal(4, actual: published.CompletionTokens);
    }

    [Fact]
    public async Task
        NonStreaming_DoesNotForwardClientAcceptEncoding_AndStripsStaleContentEncodingFromTranslatedResponse()
    {
        // _httpClient never configures AutomaticDecompression, so relaying the client's own
        // "Accept-Encoding" upstream risks a genuinely-compressed response being handed straight to the
        // translator as if it were plain-text JSON/SSE. And even setting that aside, a translated
        // response is always freshly-serialized, uncompressed UTF-8 text - so a stale "Content-Encoding"
        // copied from upstream would claim an encoding the actual bytes don't have, which is exactly the
        // "Z_DATA_ERROR: incorrect header check" failure a real client hit in production.
        var forwardedAcceptEncoding = "not-set";

        var handler = new DelegatingHandlerStub(request =>
        {
            forwardedAcceptEncoding = request.Headers.TryGetValues(name: "Accept-Encoding", values: out var values)
                ? values.First()
                : null;

            const string geminiResponse = """
                                          {"candidates":[{"content":{"role":"model","parts":[{"text":"hi"}]},"finishReason":"STOP","index":0}]}
                                          """;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: geminiResponse, encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("""{"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}]}""");
        context.Request.Headers["Accept-Encoding"] = "gzip, deflate, br";

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Null(forwardedAcceptEncoding);
        Assert.False(
            condition: context.Response.Headers.ContainsKey("Content-Encoding"),
            userMessage:
            "A translated response must not claim an encoding the freshly-serialized body doesn't actually have.");
    }

    [Fact]
    public async Task NonStreaming_ToolCall_TranslatesFunctionDeclarations_AndFunctionCallResponse()
    {
        JsonDocument? forwardedBody = null;

        var handler = new DelegatingHandlerStub(async request =>
        {
            forwardedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());

            const string geminiResponse = """
                                          {
                                            "candidates": [
                                              {
                                                "content": { "role": "model", "parts": [ { "functionCall": { "name": "get_weather", "args": { "city": "SF" } } } ] },
                                                "finishReason": "STOP",
                                                "index": 0
                                              }
                                            ],
                                            "usageMetadata": { "promptTokenCount": 20, "candidatesTokenCount": 6, "totalTokenCount": 26 }
                                          }
                                          """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: geminiResponse, encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
        });

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("""
                                   {"model":"gemini-2.5-pro","messages":[{"role":"user","content":"weather in SF?"}],"tools":[{"type":"function","function":{"name":"get_weather","description":"Get weather","parameters":{"type":"object","additionalProperties":false,"properties":{"city":{"type":"string"}}}}}]}
                                   """);

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        // OpenAI tools -> Gemini functionDeclarations, with JSON-Schema-only keywords stripped.
        var tool = forwardedBody!.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
        Assert.Equal(expected: "get_weather", actual: tool.GetProperty("name").GetString());
        var parameters = tool.GetProperty("parameters");
        Assert.False(condition: parameters.TryGetProperty(propertyName: "additionalProperties", value: out _),
            userMessage: "additionalProperties must be stripped for Gemini's schema.");
        Assert.Equal(expected: "string",
            actual: parameters.GetProperty("properties").GetProperty("city").GetProperty("type").GetString());

        // Gemini functionCall -> OpenAI tool_calls, finish_reason tool_calls.
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        var message = openAi.RootElement.GetProperty("choices")[0].GetProperty("message");
        var toolCall = message.GetProperty("tool_calls")[0];
        Assert.Equal(expected: "get_weather", actual: toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Contains(expectedSubstring: "SF",
            actualString: toolCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal(expected: "tool_calls",
            actual: openAi.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task NonStreaming_ToolCall_SanitizesUnsupportedSchemaKeywords_IncludingNestedInsideAnyOf()
    {
        // Real-world tool schemas (e.g. VS Code's Copilot/LM tool definitions) carry JSON Schema
        // keywords Gemini's OpenAPI-subset schema rejects outright with a 400 - not just at the top
        // level, but nested inside anyOf/oneOf/allOf combinators too (seen in production: "$comment" and
        // "const" nested under tools[0].function_declarations[n].parameters...any_of[i]).
        JsonDocument? forwardedBody = null;

        var handler = new DelegatingHandlerStub(async request =>
        {
            forwardedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP","index":0}]}""",
                    encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
        });

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("""
                                   {"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}],"tools":[{"type":"function","function":{"name":"pick","description":"Pick","parameters":{"type":"object","$schema":"http://json-schema.org/draft-07/schema#","$comment":"top-level comment","properties":{"mode":{"enumDescriptions":{"a":"desc"},"anyOf":[{"const":"a"},{"const":"b"}]}}}}}]}
                                   """);

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        var parameters = forwardedBody!.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0]
            .GetProperty("parameters");
        Assert.False(condition: parameters.TryGetProperty(propertyName: "$schema", value: out _),
            userMessage: "$schema must be stripped for Gemini's schema.");
        Assert.False(condition: parameters.TryGetProperty(propertyName: "$comment", value: out _),
            userMessage: "$comment must be stripped for Gemini's schema.");

        var mode = parameters.GetProperty("properties").GetProperty("mode");
        Assert.False(condition: mode.TryGetProperty(propertyName: "enumDescriptions", value: out _),
            userMessage: "enumDescriptions must be stripped for Gemini's schema.");

        var anyOf = mode.GetProperty("anyOf");
        Assert.False(condition: anyOf[0].TryGetProperty(propertyName: "const", value: out _),
            userMessage: "const nested inside anyOf must be stripped for Gemini's schema.");
        Assert.Equal(expected: "a", actual: anyOf[0].GetProperty("enum")[0].GetString());
        Assert.Equal(expected: "b", actual: anyOf[1].GetProperty("enum")[0].GetString());
    }

    [Fact]
    public async Task Streaming_TranslatesGeminiSseChunks_ToOpenAiChunks_AndEmitsDone()
    {
        Uri? forwardedUri = null;

        // Gemini streamGenerateContent?alt=sse framing: each event is `data: {json}` per chunk.
        const string geminiSse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Hel\"}]}}],\"responseId\":\"r1\",\"modelVersion\":\"gemini-2.5-pro\"}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"lo\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":3,\"candidatesTokenCount\":1,\"totalTokenCount\":4}}\n\n";

        var handler = new DelegatingHandlerStub(request =>
        {
            forwardedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: geminiSse, encoding: Encoding.UTF8, mediaType: "text/event-stream")
            });
        });

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("""
                                   {"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}],"stream":true}
                                   """);

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(
            expected:
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse",
            actual: forwardedUri!.ToString());

        var body = ReadResponse(context);
        var dataLines = body.Split(separator: "\n\n", options: StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith(value: "data: ", comparisonType: StringComparison.Ordinal))
            .Select(l => l["data: ".Length..])
            .ToList();

        Assert.Equal(expected: "[DONE]", actual: dataLines[^1]);

        var chunks = dataLines.Where(l => l != "[DONE]").Select(l => JsonDocument.Parse(l)).ToList();
        Assert.All(collection: chunks,
            action: c => Assert.Equal(expected: "chat.completion.chunk",
                actual: c.RootElement.GetProperty("object").GetString()));

        // First chunk carries role + first text delta; the assembled content spans the chunks.
        Assert.Equal(expected: "assistant",
            actual: chunks[0].RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("role")
                .GetString());
        var assembled = string.Concat(chunks.Select(c =>
            c.RootElement.GetProperty("choices")[0].GetProperty("delta")
                .TryGetProperty(propertyName: "content", value: out var content)
                ? content.GetString()
                : string.Empty));
        Assert.Equal(expected: "Hello", actual: assembled);
        Assert.Equal(expected: "stop",
            actual: chunks[^1].RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task Streaming_TranslatedResponseLargerThanCaptureCap_RecoversUsageFromTail()
    {
        // Exercises TranslateAndCaptureStreamAsync's lazily-allocated IncrementalUsageScanner (the
        // translated-stream sibling of ProxyMiddlewareCaptureRecoveryTests' raw-HTTP-path coverage): a
        // giant leading content-delta chunk pushes the trailing usageMetadata chunk well past the 4 MiB
        // head cap, so a successful token count here can only have come from the tail-window fallback.
        const int fillerBytes = 5 * 1024 * 1024;
        var filler = new string('a', count: fillerBytes);
        var geminiSse =
            $"data: {{\"candidates\":[{{\"content\":{{\"role\":\"model\",\"parts\":[{{\"text\":\"{filler}\"}}]}}}}]}}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":5,\"totalTokenCount\":15}}\n\n";

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: geminiSse, encoding: Encoding.UTF8, mediaType: "text/event-stream")
        }));

        var capturing = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware(handler: handler, telemetry: capturing);
        var context = BuildContext("""
                                   {"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}],"stream":true}
                                   """);

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        var published = await capturing.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10, actual: published.PromptTokens);
        Assert.Equal(5, actual: published.CompletionTokens);
    }

    [Fact]
    public async Task Streaming_EmbeddedProviderError_TerminatesStream_WithoutDone()
    {
        // Gemini can deliver an error (e.g. 429 RESOURCE_EXHAUSTED) as an HTTP 200 SSE body with an
        // "error" field; LiteLLM raises rather than swallowing it. Our translator throws, and the proxy
        // truncates the already-committed 200 stream (no [DONE]) rather than pretending success.
        const string geminiSse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"partial\"}]}}]}\n\n" +
            "data: {\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"quota\"}}\n\n";

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: geminiSse, encoding: Encoding.UTF8, mediaType: "text/event-stream")
        }));

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("""
                                   {"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}],"stream":true}
                                   """);

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        var body = ReadResponse(context);
        Assert.Contains(expectedSubstring: "partial", actualString: body);
        Assert.DoesNotContain(expectedSubstring: "[DONE]", actualString: body);
    }

    [Fact]
    public void ExtractDataPayload_MultipleDataLines_JoinedWithNewline_NotConcatenatedDirectly()
    {
        // Per the SSE spec, multiple data: lines within one event must be joined with "\n", not
        // concatenated directly - otherwise a multi-line or pretty-printed JSON payload can come apart.
        // ExtractDataPayload is private, so this reaches it directly via reflection rather than
        // round-tripping through JSON parsing, which is too whitespace-tolerant to reliably distinguish
        // "joined with \n" from "concatenated" for most realistic payload splits.
        var eventBytes = "data: line1\ndata: line2"u8.ToArray();
        var method = typeof(GeminiStreamTranslator).GetMethod(name: "ExtractDataPayload",
            bindingAttr: BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method); // fails clearly if ExtractDataPayload is ever renamed/removed, rather than NRE-ing below

        var data = (string?)method.Invoke(null, parameters: [eventBytes]);

        Assert.Equal(expected: "line1\nline2", actual: data);
    }

    [Theory]
    // string -> single-element stopSequences
    [InlineData("""{"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}],"stop":"END"}""", true, "END")]
    // array of strings -> stopSequences preserved
    [InlineData("""{"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}],"stop":["A","B"]}""", true,
        "A")]
    // explicit null -> no stopSequences (must not become [null], which Gemini rejects)
    [InlineData("""{"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}],"stop":null}""", false, null)]
    // non-string (number) -> ignored, not wrapped into stopSequences
    [InlineData("""{"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}],"stop":5}""", false, null)]
    public void TranslateRequest_EmitsStopSequences_OnlyForStringOrStringArray(string openAiBody,
        bool expectStopSequences, string? expectedFirst)
    {
        var translated = new GeminiPayloadTranslator().TranslateRequest(Encoding.UTF8.GetBytes(openAiBody));
        using var json = JsonDocument.Parse(translated);

        var hasGenerationConfig =
            json.RootElement.TryGetProperty(propertyName: "generationConfig", value: out var generationConfig);
        var hasStopSequences = hasGenerationConfig &&
                               generationConfig.TryGetProperty(propertyName: "stopSequences", value: out _);

        Assert.Equal(expected: expectStopSequences, actual: hasStopSequences);
        if (expectStopSequences)
            Assert.Equal(expected: expectedFirst, actual: generationConfig.GetProperty("stopSequences")[0].GetString());
    }

    [Fact]
    public async Task NonStreaming_UpstreamReadAbortedByNonClientIoFailure_Returns502_NotAnEmpty200()
    {
        // TranslateAndCaptureBufferedAsync buffers the whole upstream body before ever writing to the
        // client, so an I/O failure here that is NOT the client disconnecting (context.RequestAborted
        // still live) must surface as a real upstream error rather than silently committing a 200 with an
        // empty body - regression test for that ProxyMiddleware.InvokeAsync fail-open-only-on-genuine-abort
        // fix.
        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingReadStream())
        }));

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("""{"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}]}""");

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status502BadGateway, actual: context.Response.StatusCode);
        using var body = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal(expected: "upstream_error",
            actual: body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task NonStreaming_UpstreamReadAbortedByGenuineClientDisconnect_ReturnsGracefully_WithoutThrowing()
    {
        // The counterpart to the test above: when the SAME kind of I/O failure happens because the client
        // itself went away, there is no one left to answer, so InvokeAsync must complete normally (no
        // unhandled exception) rather than attempt to write a 502 to a connection that is already gone.
        // The token starts live (not pre-cancelled) so the earlier request-body read still succeeds
        // normally; ThrowingReadStream cancels it itself, right before throwing, to model the client
        // disconnecting at the exact moment the upstream read fails - matching how
        // TranslateAndCaptureBufferedAsync's fix distinguishes this case (cancellationToken.IsCancellationRequested)
        // from the non-abort I/O failure the test above covers.
        using var cts = new CancellationTokenSource();

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingReadStream(cts))
        }));

        var middleware = BuildMiddleware(handler);
        var context = BuildContext("""{"model":"gemini-2.5-pro","messages":[{"role":"user","content":"hi"}]}""");
        context.RequestAborted = cts.Token;

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);
    }

    /// <summary>
    /// A content stream whose read always throws the exact IOException(SocketException) shape a mid-stream connection
    /// abort produces, used to exercise the buffered-response fail-open/fail-hard split.
    /// </summary>
    private sealed class ThrowingReadStream(CancellationTokenSource? cancelBeforeThrow = null) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancelBeforeThrow?.Cancel();
            throw new IOException(
                message:
                "Unable to read data from the transport connection: The I/O operation has been aborted because of either a thread exit or an application request.",
                innerException: new SocketException((int)SocketError.OperationAborted));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
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

    private sealed class CapturingTelemetryPublisher : ITelemetryPublisher
    {
        private readonly TaskCompletionSource<RoutingTelemetryEvent> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            _tcs.TrySetResult(telemetryEvent);
            return Task.CompletedTask;
        }

        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async Task<RoutingTelemetryEvent> WaitForEventAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task1: _tcs.Task, task2: Task.Delay(timeout));
            Assert.True(condition: ReferenceEquals(objA: completed, objB: _tcs.Task),
                userMessage: "Timed out waiting for a routing telemetry event.");
            return await _tcs.Task;
        }
    }
}