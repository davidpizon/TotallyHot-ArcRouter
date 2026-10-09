using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Tests.Sessions;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Pins the #165 hot-path capture (ADR-0019): with the Transcription Capture toggle on, the proxy stores the
/// request bytes exactly as the client sent them and the response bytes exactly as relayed (however large),
/// plus a translated turn's provider-side pair; with it off nothing is created; and a planted secret never
/// reaches a session file. Drives the real <see cref="ProxyMiddleware"/> against a stubbed upstream.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class ProxyMiddlewareSessionCaptureTests : IDisposable
{
    private const string ClientSession = "client-session-1";

    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot, "proxy-capture-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly TranscriptDatabase _database;
    private readonly SessionIndex _index;
    private readonly SessionStore _store;
    private readonly SessionCaptureWriter _writer;
    private readonly StaticOptionsMonitor<TranscriptOptions> _transcriptOptions = new(new TranscriptOptions { Enabled = true });

    /// <summary>Builds a store, a started writer and the capture factory over a scratch directory.</summary>
    public ProxyMiddlewareSessionCaptureTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Path.Combine(_root, "sessions");
        _database = new TranscriptDatabase(Options.Create(new StorageOptions
        {
            TranscriptDatabasePath = Path.Combine(_root, "transcripts.db")
        }));
        _index = new SessionIndex(_database);
        _index.EnsureCreated();
        _store = new SessionStore(_index, new InMemoryMasterKeyStore(), _folder);
        _writer = new SessionCaptureWriter(
            new Lazy<SessionStore>(() => _store),
            Options.Create(new SessionCaptureOptions()),
            NullLogger<SessionCaptureWriter>.Instance);
        _writer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _writer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _writer.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch cleanup is also done by TestTempDirectorySweeper.
        }
    }

    /// <summary>The stored request is the client's bytes, whitespace and non-ASCII included, and the response is the relayed bytes.</summary>
    [Fact]
    public async Task CaptureOn_StoresClientRequestAndResponseByteForByte()
    {
        const string response = "{\"id\":\"c1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"héllo\"}}]," +
                                "\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}";
        // Pretty-printed with <, > and a non-ASCII letter: the forwarded copy is re-serialized, the stored one must not be.
        var request = "{\n  \"model\" : \"primary\",\n  \"messages\":[{\"role\":\"user\",\"content\":\"a<b> & café\"}]\n}";

        await RunAsync(request, _ => Json(response), "claude-code/2.1.0 (cli)");
        var frames = await ReadFramesAsync();

        Assert.Equal(request, Text(frames, SessionBodyKind.ClientRequest));
        Assert.Equal(response, Text(frames, SessionBodyKind.ClientResponse));
        Assert.DoesNotContain(frames, f => f.Kind is SessionBodyKind.ProviderRequest or SessionBodyKind.ProviderResponse);
    }

    /// <summary>The metadata snapshot carries the routing facts and a normalized harness token, never the raw User-Agent.</summary>
    [Fact]
    public async Task CaptureOn_StoresMetadataAndExtracts()
    {
        const string response = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"the answer\"}}]," +
                                "\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":4}}";
        await RunAsync(ChatRequest("what is it"), _ => Json(response), "claude-code/2.1.0 (host=my-laptop)");
        var frames = await ReadFramesAsync();

        using var metadata = JsonDocument.Parse(Text(frames, SessionBodyKind.TurnMetadata));
        Assert.Equal("claude-code/2.1.0", metadata.RootElement.GetProperty("harness").GetString());
        Assert.Equal("primary", metadata.RootElement.GetProperty("requested_model").GetString());
        Assert.Equal(7, metadata.RootElement.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal("json", metadata.RootElement.GetProperty("content_encoding").GetString());
        Assert.DoesNotContain("my-laptop", Text(frames, SessionBodyKind.TurnMetadata));

        using var extracts = JsonDocument.Parse(Text(frames, SessionBodyKind.Extracts));
        Assert.Equal("what is it", extracts.RootElement.GetProperty("newest_user_message").GetString());
        Assert.Equal("the answer", extracts.RootElement.GetProperty("response_text").GetString());
    }

    /// <summary>With the toggle off, no session folder, spool or turn is created.</summary>
    [Fact]
    public async Task CaptureOff_CreatesNothing()
    {
        _transcriptOptions.Set(new TranscriptOptions { Enabled = false });

        await RunAsync(ChatRequest("hi"), _ => Json("{\"choices\":[]}"));
        await _writer.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(Directory.EnumerateFiles(_folder));
        Assert.Empty(_store.ListTurns(_store.ResolveArchiveSessionId(ClientSession)));
    }

    /// <summary>A streamed response over the 4 MiB telemetry cap is stored whole, not as a prefix.</summary>
    [Fact]
    public async Task CaptureOn_ResponseOverTelemetryCap_IsStoredWhole()
    {
        // Words, not one long run of letters: a base64-looking run over 4096 characters is redacted whole by design.
        var filler = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 5 * 1024 * 1024 / 17 + 1));
        var sse = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{filler}\"}}}}]}}\n\n" +
                  "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n" +
                  "data: [DONE]\n\n";

        await RunAsync(ChatRequest("go"), _ => Sse(sse));
        var frames = await ReadFramesAsync();

        Assert.Equal(sse, Text(frames, SessionBodyKind.ClientResponse));
        using var extracts = JsonDocument.Parse(Text(frames, SessionBodyKind.Extracts));
        Assert.True(extracts.RootElement.GetProperty("response_text_truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, extracts.RootElement.GetProperty("response_text").ValueKind);
    }

    /// <summary>A planted key in the request and in the reply never reaches the session file.</summary>
    [Fact]
    public async Task CaptureOn_PlantedSecrets_AreObscuredInEveryBody()
    {
        const string secret = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGH";
        var response = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"your key is " + secret +
                       "\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}";

        await RunAsync(ChatRequest("my key is " + secret), _ => Json(response));
        var frames = await ReadFramesAsync();

        foreach (var frame in frames.Where(f => f.Plaintext is not null))
        {
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(frame.Plaintext!));
        }

        Assert.DoesNotContain(secret, string.Concat(Directory.EnumerateFiles(_folder).Select(File.ReadAllText)));
    }

    /// <summary>A translated turn also stores the provider-side request and the untranslated provider response.</summary>
    [Fact]
    public async Task CaptureOn_TranslatedTurn_StoresProviderSideBodies()
    {
        const string geminiResponse =
            "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hi there\"}]},\"finishReason\":\"STOP\",\"index\":0}]," +
            "\"usageMetadata\":{\"promptTokenCount\":8,\"candidatesTokenCount\":4,\"totalTokenCount\":12}}";
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gemini-2.5-pro", providerModelId: "gemini-2.5-pro", baseUrl: "https://generativelanguage.googleapis.com",
            authHeaderName: "x-goog-api-key", authHeaderScheme: string.Empty, apiKey: "k", providerName: "gemini");
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = new GeminiPayloadTranslator()
        };
        var request = "{\"model\":\"gemini-2.5-pro\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";

        await RunAsync(request, _ => Json(geminiResponse), resolver: resolver, translators: translators);
        var frames = await ReadFramesAsync();

        Assert.Equal(request, Text(frames, SessionBodyKind.ClientRequest));
        Assert.Equal(geminiResponse, Text(frames, SessionBodyKind.ProviderResponse));
        Assert.Contains("hello", Text(frames, SessionBodyKind.ProviderRequest));
        Assert.NotEqual(request, Text(frames, SessionBodyKind.ProviderRequest));
        Assert.Contains("hi there", Text(frames, SessionBodyKind.ClientResponse));
        Assert.NotEqual(geminiResponse, Text(frames, SessionBodyKind.ClientResponse));
        using var metadata = JsonDocument.Parse(Text(frames, SessionBodyKind.TurnMetadata));
        Assert.True(metadata.RootElement.GetProperty("translated").GetBoolean());
    }

    /// <summary>A request the router rejects stores no turn and leaves no spool behind.</summary>
    [Fact]
    public async Task CaptureOn_RejectedRequest_StoresNothing_AndLeavesNoSpool()
    {
        await RunAsync("{\"model\":", _ => Json("{}"));
        await _writer.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));
        Assert.Empty(_store.ListTurns(_store.ResolveArchiveSessionId(ClientSession)));
    }

    /// <summary>A failover from a translated candidate to a pass-through one leaves no stale provider-side bodies.</summary>
    [Fact]
    public async Task CaptureOn_FailoverFromTranslatedToPassThrough_StoresNoProviderBodies()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("gemini-2.5-pro", "gemini", "gemini-2.5-pro", "https://gemini.test"),
            ("local", "openai", "local", "https://local.test"));
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = new GeminiPayloadTranslator()
        };
        const string served = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"from local\"}}]," +
                              "\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":2}}";
        var request = "{\"model\":\"gemini-2.5-pro\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";

        await RunAsync(
            request,
            message => message.RequestUri!.Host == "gemini.test" ? throw new HttpRequestException("down") : Json(served),
            resolver: resolver,
            translators: translators);
        var frames = await ReadFramesAsync();

        Assert.Equal(served, Text(frames, SessionBodyKind.ClientResponse));
        Assert.DoesNotContain(frames, f => f.Kind is SessionBodyKind.ProviderRequest or SessionBodyKind.ProviderResponse);
        using var metadata = JsonDocument.Parse(Text(frames, SessionBodyKind.TurnMetadata));
        Assert.False(metadata.RootElement.GetProperty("translated").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("fallback").GetBoolean());
        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));
    }

    /// <summary>A non-streaming Bedrock turn stores the body sent to Bedrock and the payload it returned.</summary>
    [Fact]
    public async Task CaptureOn_BedrockNonStreaming_StoresProviderSideBodies()
    {
        const string claudeResponse = "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\"," +
                                      "\"content\":[{\"type\":\"text\",\"text\":\"Hello from Bedrock.\"}],\"stop_reason\":\"end_turn\"," +
                                      "\"usage\":{\"input_tokens\":12,\"output_tokens\":6}}";
        var client = new Mock<IAmazonBedrockRuntime>();
        client.Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new InvokeModelResponse
            {
                Body = new MemoryStream(Encoding.UTF8.GetBytes(claudeResponse)),
                ContentType = "application/json"
            });
        var request = "{\"model\":\"claude-bedrock\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

        await RunAsync(request, _ => Json("{}"), resolver: BedrockResolver(), translators: BedrockTranslators(), bedrock: client.Object);
        var frames = await ReadFramesAsync();

        Assert.Equal(request, Text(frames, SessionBodyKind.ClientRequest));
        Assert.Equal(claudeResponse, Text(frames, SessionBodyKind.ProviderResponse));
        Assert.Contains("bedrock-2023-05-31", Text(frames, SessionBodyKind.ProviderRequest));
        Assert.Contains("Hello from Bedrock.", Text(frames, SessionBodyKind.ClientResponse));
        Assert.DoesNotContain("bedrock-2023-05-31", Text(frames, SessionBodyKind.ClientResponse));
        using var metadata = JsonDocument.Parse(Text(frames, SessionBodyKind.TurnMetadata));
        Assert.True(metadata.RootElement.GetProperty("translated").GetBoolean());
        Assert.Equal(12, metadata.RootElement.GetProperty("prompt_tokens").GetInt32());
    }

    /// <summary>A streamed Bedrock turn stores the relayed SSE and the event payloads, one per line.</summary>
    [Fact]
    public async Task CaptureOn_BedrockStreaming_StoresRelayedSseAndPayloadLines()
    {
        var eventStream = new MemoryStream();
        string[] payloads =
        [
            "{\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":5}}}",
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}",
            "{\"type\":\"content_block_stop\",\"index\":0}",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}",
            "{\"type\":\"message_stop\"}",
        ];
        foreach (var payload in payloads) BedrockProviderTests.AppendFrame(eventStream, "chunk", payload);
        eventStream.Position = 0;
        var client = new Mock<IAmazonBedrockRuntime>();
        client.Setup(c => c.InvokeModelWithResponseStreamAsync(
                It.IsAny<InvokeModelWithResponseStreamRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvokeModelWithResponseStreamResponse { Body = new ResponseStream(eventStream) });
        var request = "{\"model\":\"claude-bedrock\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}";

        await RunAsync(request, _ => Json("{}"), resolver: BedrockResolver(), translators: BedrockTranslators(), bedrock: client.Object);
        var stored = await ReadFramesAsync();

        Assert.Equal(string.Concat(payloads.Select(p => p + "\n")), Text(stored, SessionBodyKind.ProviderResponse));
        var sse = Text(stored, SessionBodyKind.ClientResponse);
        Assert.Contains("data: [DONE]", sse);
        Assert.Contains("\"Hi\"", sse);
        using var metadata = JsonDocument.Parse(Text(stored, SessionBodyKind.TurnMetadata));
        Assert.Equal("sse", metadata.RootElement.GetProperty("content_encoding").GetString());
    }

    private static IModelRouteResolver BedrockResolver() => ModelRouteResolverTestFactory.Create(
        modelName: "claude-bedrock",
        providerModelId: "anthropic.claude-3-5-sonnet-20241022-v2:0",
        baseUrl: "https://bedrock-runtime.us-east-1.amazonaws.com",
        providerName: new AnthropicOnBedrockPayloadTranslator().Provider,
        awsRegion: "us-east-1");

    private static Dictionary<string, IPayloadTranslator> BedrockTranslators()
    {
        var translator = new AnthropicOnBedrockPayloadTranslator();
        return new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            [translator.Provider] = translator
        };
    }

    private static string ChatRequest(string userText) =>
        "{\"model\":\"primary\",\"messages\":[{\"role\":\"user\",\"content\":\"" + userText + "\"}]}";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private static string Text(IReadOnlyList<SessionBodyFrame> frames, SessionBodyKind kind) =>
        Encoding.UTF8.GetString(frames.Single(f => f.Kind == kind).Plaintext
                                ?? throw new InvalidOperationException($"{kind} was recorded as missing."));

    /// <summary>Waits for the writer to store the turn, then reads every body of the one stored session.</summary>
    private async Task<IReadOnlyList<SessionBodyFrame>> ReadFramesAsync()
    {
        Assert.True(await _writer.WaitForIdleAsync(TimeSpan.FromSeconds(30)));
        return _store.ReadBodies(_store.ResolveArchiveSessionId(ClientSession));
    }

    private async Task RunAsync(
        string requestBody,
        Func<HttpRequestMessage, HttpResponseMessage> upstream,
        string? userAgent = null,
        IModelRouteResolver? resolver = null,
        IReadOnlyDictionary<string, IPayloadTranslator>? translators = null,
        IAmazonBedrockRuntime? bedrock = null)
    {
        resolver ??= ModelRouteResolverTestFactory.CreateWithModels(("primary", "openai", "primary-upstream", "https://primary.test"));
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: RequestInterceptorBuilder.For(resolver),
            httpClient: new HttpClient(new StubHandler(upstream)),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators,
                BedrockClientFactory = bedrock is null ? null : new FakeBedrockClientFactory(bedrock),
                TurnCapture = new TurnCaptureFactory(
                    _writer,
                    _transcriptOptions,
                    Options.Create(new SessionCaptureOptions()),
                    _database,
                    NullLogger<TurnCaptureFactory>.Instance)
            });

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        context.Request.Headers["x-claude-code-session-id"] = ClientSession;
        if (userAgent is not null) context.Request.Headers.UserAgent = userAgent;
        var bytes = Encoding.UTF8.GetBytes(requestBody);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        context.RequestAborted = TestContext.Current.CancellationToken;

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);
    }

    private sealed class FakeBedrockClientFactory(IAmazonBedrockRuntime client) : IBedrockRuntimeClientFactory
    {
        public IAmazonBedrockRuntime Create(ResolvedModelRoute route) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
