using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Coverage for PLAN.md TODO 4's "Unified API Translation" pillar, AWS Bedrock slice
/// (<c>docs/router/unified-api-translation.md</c> §4.2). Unlike every other translated provider,
/// Bedrock is invoked through the AWS SDK (<see cref="IAmazonBedrockRuntime"/>) rather than a forwarded
/// <c>HttpRequestMessage</c> - the SDK handles SigV4 signing and endpoint resolution itself. These
/// tests substitute a fake <see cref="IAmazonBedrockRuntime"/> (mock harness, per the plan - no live AWS
/// call or real credentials in this environment) and drive the real <see cref="ProxyMiddleware"/>
/// end-to-end through its Bedrock invocation path for all three in-scope model families (Anthropic
/// Claude, Amazon Titan, Meta Llama), plus direct unit coverage of each translator.
/// </summary>
public class BedrockProviderTests
{
    private const string AwsRegion = "us-east-1";

    private static IModelRouteResolver Resolver(string modelName, string providerModelId, string providerName, string awsRegion = AwsRegion) =>
        ModelRouteResolverTestFactory.Create(
            modelName: modelName,
            providerModelId: providerModelId,
            baseUrl: "https://bedrock-runtime.us-east-1.amazonaws.com",
            providerName: providerName,
            awsRegion: awsRegion);

    private static ProxyMiddleware BuildMiddleware(
        string modelName,
        string providerModelId,
        IBedrockPayloadTranslator translator,
        IAmazonBedrockRuntime client,
        ITelemetryPublisher? telemetry = null)
    {
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), Resolver(modelName, providerModelId, translator.Provider));
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            [translator.Provider] = translator,
        };

        return new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor,
            telemetryPublisher: telemetry,
            translators: translators,
            bedrockClientFactory: new FakeBedrockRuntimeClientFactory(client));
    }

    private static DefaultHttpContext BuildContext(string requestBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
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

    // --- Claude on Bedrock: non-streaming, through the real ProxyMiddleware + SDK dispatch ---

    [Fact]
    public async Task Claude_NonStreaming_TranslatesRequestAndResponse_ThroughSdkDispatch()
    {
        InvokeModelRequest? capturedRequest = null;

        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(() =>
            {
                const string claudeResponse = """
                    {
                      "id": "msg_bedrock_01",
                      "type": "message",
                      "role": "assistant",
                      "model": "anthropic.claude-3-5-sonnet-20241022-v2:0",
                      "content": [ { "type": "text", "text": "Hello from Bedrock Claude." } ],
                      "stop_reason": "end_turn",
                      "usage": { "input_tokens": 12, "output_tokens": 6 }
                    }
                    """;
                return new InvokeModelResponse { Body = new MemoryStream(Encoding.UTF8.GetBytes(claudeResponse)), ContentType = "application/json" };
            });

        var capturing = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware("claude-sonnet-bedrock", "anthropic.claude-3-5-sonnet-20241022-v2:0", new AnthropicOnBedrockPayloadTranslator(), mockClient.Object, capturing);

        var context = BuildContext("""
            {"model":"claude-sonnet-bedrock","messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"}]}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal("anthropic.claude-3-5-sonnet-20241022-v2:0", capturedRequest!.ModelId);
        using var forwardedBody = JsonDocument.Parse(Encoding.UTF8.GetString(capturedRequest.Body.ToArray()));
        Assert.Equal("bedrock-2023-05-31", forwardedBody.RootElement.GetProperty("anthropic_version").GetString());
        Assert.False(forwardedBody.RootElement.TryGetProperty("model", out _), "Bedrock body must not carry 'model' - it's the SDK's ModelId.");
        Assert.Equal("be brief", forwardedBody.RootElement.GetProperty("system").GetString());

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal("Hello from Bedrock Claude.", openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        var published = await capturing.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(12, published.PromptTokens);
        Assert.Equal(6, published.CompletionTokens);
    }

    [Fact]
    public async Task Claude_Streaming_TranslatesRealEventStreamFrames_ToOpenAiChunks()
    {
        // A genuine AWS application/vnd.amazon.eventstream binary encoding (prelude + headers + payload
        // + CRC32s, per the Smithy spec) - exercised through the real AWS SDK's own ResponseStream
        // decoder, not a fake shortcut, so this proves the SDK-decoded-chunk path end-to-end.
        var frames = new MemoryStream();
        AppendFrame(frames, "chunk", """{"type":"message_start","message":{"id":"msg_stream_01","model":"anthropic.claude-3-5-sonnet-20241022-v2:0","usage":{"input_tokens":5}}}""");
        AppendFrame(frames, "chunk", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        AppendFrame(frames, "chunk", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}""");
        AppendFrame(frames, "chunk", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}""");
        AppendFrame(frames, "chunk", """{"type":"content_block_stop","index":0}""");
        AppendFrame(frames, "chunk", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}""");
        AppendFrame(frames, "chunk", """{"type":"message_stop"}""");
        frames.Position = 0;

        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c => c.InvokeModelWithResponseStreamAsync(It.IsAny<InvokeModelWithResponseStreamRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvokeModelWithResponseStreamResponse { Body = new ResponseStream(frames) });

        var middleware = BuildMiddleware("claude-sonnet-bedrock", "anthropic.claude-3-5-sonnet-20241022-v2:0", new AnthropicOnBedrockPayloadTranslator(), mockClient.Object);

        var context = BuildContext("""
            {"model":"claude-sonnet-bedrock","messages":[{"role":"user","content":"hi"}],"stream":true}
            """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var body = ReadResponse(context);
        var dataLines = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => l["data: ".Length..])
            .ToList();

        Assert.Equal("[DONE]", dataLines[^1]);
        var chunks = dataLines.Where(l => l != "[DONE]").Select(l => JsonDocument.Parse(l)).ToList();
        var assembled = string.Concat(chunks.Select(c =>
            c.RootElement.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out var content)
                ? content.GetString()
                : string.Empty));
        Assert.Equal("Hello", assembled);
        Assert.Equal("stop", chunks[^1].RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task Claude_SdkThrows_Returns502WithErrorEnvelope()
    {
        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonBedrockRuntimeException("access denied"));

        var middleware = BuildMiddleware("claude-sonnet-bedrock", "anthropic.claude-3-5-sonnet-20241022-v2:0", new AnthropicOnBedrockPayloadTranslator(), mockClient.Object);
        var context = BuildContext("""{"model":"claude-sonnet-bedrock","messages":[{"role":"user","content":"hi"}]}""");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        using var error = JsonDocument.Parse(ReadResponse(context));
        var message = error.RootElement.GetProperty("error").GetProperty("message").GetString();
        // The 502 envelope must carry a generic message, not the raw SDK exception text, which can leak
        // internal endpoint/region/request-id detail (the exception is logged server-side instead).
        Assert.Equal("The upstream provider is unavailable.", message);
        Assert.DoesNotContain("access denied", message);
    }

    // --- Bedrock failover: a Bedrock candidate is no longer terminal (docs/router/agent-resilience-strategies.md) ---

    private static IModelRouteResolver TwoCandidateResolver(
        string primaryModel, string primaryProvider, string primaryProviderModelId,
        string backupModel, string backupProvider, string backupProviderModelId)
    {
        var options = new TotallyHot.ArcRouter.Models.ModelRoutingOptions
        {
            Providers = new Dictionary<string, TotallyHot.ArcRouter.Models.ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [primaryProvider] = new TotallyHot.ArcRouter.Models.ProviderOptions { BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com", AwsRegion = AwsRegion },
                [backupProvider] = new TotallyHot.ArcRouter.Models.ProviderOptions { BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com", AwsRegion = AwsRegion },
            },
            ModelList =
            [
                new TotallyHot.ArcRouter.Models.ModelRouteEntry { ModelName = primaryModel, Provider = primaryProvider, ProviderModelId = primaryProviderModelId },
                new TotallyHot.ArcRouter.Models.ModelRouteEntry { ModelName = backupModel, Provider = backupProvider, ProviderModelId = backupProviderModelId },
            ]
        };

        return new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
    }

    [Fact]
    public async Task Claude_GenericSdkFailure_FailsOverToNextCandidate_OnSameProvider()
    {
        // AmazonBedrockRuntimeException (not a credential problem) is a per-target failure, unconditionally
        // retriable against any next candidate - including one on the same Bedrock provider, unlike the
        // credential case below.
        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.SetupSequence(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonBedrockRuntimeException("throttled"))
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = new MemoryStream(Encoding.UTF8.GetBytes(
                    """{"id":"m","type":"message","role":"assistant","model":"x","content":[{"type":"text","text":"served by backup"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""))
            });

        var translator = new AnthropicOnBedrockPayloadTranslator();
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase) { [translator.Provider] = translator };
        var resolver = TwoCandidateResolver(
            "primary-claude", translator.Provider, "primary-model-id",
            "backup-claude", translator.Provider, "backup-model-id");

        var middleware = new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver),
            translators: translators,
            bedrockClientFactory: new FakeBedrockRuntimeClientFactory(mockClient.Object));

        var context = BuildContext("""{"model":"primary-claude","messages":[{"role":"user","content":"hi"}]}""");
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal("served by backup", openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        mockClient.Verify(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Claude_CredentialFailure_DifferentProviderBackup_FailsOver()
    {
        // Two genuinely distinct Bedrock provider keys, each with its own translator (bedrock-anthropic /
        // bedrock-titan) - IPayloadTranslator.Provider is a fixed constant per translator type, so a
        // "different provider" backup needs a different translator, not just a different config key.
        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) =>
            {
                if (req.ModelId == "anthropic.claude-3-5-sonnet-20241022-v2:0")
                {
                    throw new AmazonClientException("Failed to resolve bearer token in DefaultAWSTokenIdentityResolver");
                }
            })
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = new MemoryStream(Encoding.UTF8.GetBytes(
                    """{"inputTextTokenCount":1,"results":[{"tokenCount":1,"outputText":"served by backup","completionReason":"FINISHED"}]}"""))
            });

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["bedrock-anthropic"] = new AnthropicOnBedrockPayloadTranslator(),
            ["bedrock-titan"] = new TitanPayloadTranslator(),
        };
        var resolver = TwoCandidateResolver(
            "primary-claude", "bedrock-anthropic", "anthropic.claude-3-5-sonnet-20241022-v2:0",
            "backup-titan", "bedrock-titan", "amazon.titan-text-premier-v1:0");

        var middleware = new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver),
            translators: translators,
            bedrockClientFactory: new FakeBedrockRuntimeClientFactory(mockClient.Object));

        var context = BuildContext("""{"model":"primary-claude","messages":[{"role":"user","content":"hi"}]}""");
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal("served by backup", openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Claude_CredentialFailure_SameProviderBackup_DoesNotFailOver()
    {
        // Both models live on the same Bedrock provider key (the same AWS credentials), so a credential
        // failure must NOT fail over - the backup would hit the identical missing/invalid credential. The
        // client sees the 401-equivalent instead.
        var backupAttempted = false;
        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) =>
            {
                if (req.ModelId == "backup-model-id")
                {
                    backupAttempted = true;
                }
            })
            .ThrowsAsync(new AmazonClientException("Failed to resolve bearer token in DefaultAWSTokenIdentityResolver"));

        var translator = new AnthropicOnBedrockPayloadTranslator();
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase) { [translator.Provider] = translator };
        var resolver = TwoCandidateResolver(
            "primary-claude", translator.Provider, "primary-model-id",
            "backup-claude", translator.Provider, "backup-model-id");

        var middleware = new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver),
            translators: translators,
            bedrockClientFactory: new FakeBedrockRuntimeClientFactory(mockClient.Object));

        var context = BuildContext("""{"model":"primary-claude","messages":[{"role":"user","content":"hi"}]}""");
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(backupAttempted);
    }

    // --- Titan on Bedrock: non-streaming, through the real ProxyMiddleware + SDK dispatch ---

    [Fact]
    public async Task Titan_NonStreaming_TranslatesRequestAndResponse_ThroughSdkDispatch()
    {
        InvokeModelRequest? capturedRequest = null;

        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(() =>
            {
                const string titanResponse = """
                    {
                      "inputTextTokenCount": 7,
                      "results": [ { "tokenCount": 3, "outputText": "Hello from Titan.", "completionReason": "FINISHED" } ]
                    }
                    """;
                return new InvokeModelResponse { Body = new MemoryStream(Encoding.UTF8.GetBytes(titanResponse)) };
            });

        var middleware = BuildMiddleware("titan-text-premier-bedrock", "amazon.titan-text-premier-v1:0", new TitanPayloadTranslator(), mockClient.Object);
        var context = BuildContext("""{"model":"titan-text-premier-bedrock","messages":[{"role":"user","content":"hi"}]}""");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal("amazon.titan-text-premier-v1:0", capturedRequest!.ModelId);
        using var forwardedBody = JsonDocument.Parse(Encoding.UTF8.GetString(capturedRequest.Body.ToArray()));
        Assert.Contains("User: hi", forwardedBody.RootElement.GetProperty("inputText").GetString());

        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal("bedrock-titan", openAi.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello from Titan.", openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", openAi.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    // --- Llama on Bedrock: non-streaming, through the real ProxyMiddleware + SDK dispatch ---

    [Fact]
    public async Task Llama_NonStreaming_TranslatesRequestAndResponse_ThroughSdkDispatch()
    {
        InvokeModelRequest? capturedRequest = null;

        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(() =>
            {
                const string llamaResponse = """
                    { "generation": "Hello from Llama.", "prompt_token_count": 9, "generation_token_count": 4, "stop_reason": "stop" }
                    """;
                return new InvokeModelResponse { Body = new MemoryStream(Encoding.UTF8.GetBytes(llamaResponse)) };
            });

        var middleware = BuildMiddleware("llama3-70b-bedrock", "meta.llama3-70b-instruct-v1:0", new LlamaPayloadTranslator(), mockClient.Object);
        var context = BuildContext("""{"model":"llama3-70b-bedrock","messages":[{"role":"user","content":"hi"}]}""");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal("meta.llama3-70b-instruct-v1:0", capturedRequest!.ModelId);
        using var forwardedBody = JsonDocument.Parse(Encoding.UTF8.GetString(capturedRequest.Body.ToArray()));
        Assert.Contains("<|start_header_id|>user<|end_header_id|>", forwardedBody.RootElement.GetProperty("prompt").GetString());

        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal("Hello from Llama.", openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("bedrock-llama", openAi.RootElement.GetProperty("model").GetString());
    }

    // --- Direct translator unit coverage (no SDK involved) ---

    [Fact]
    public void TitanTranslator_BuildsUserBotTranscript_WithToolResultLabeled()
    {
        var translated = new TitanPayloadTranslator().TranslateRequest(Encoding.UTF8.GetBytes("""
            {"model":"x","messages":[
                {"role":"user","content":"weather?"},
                {"role":"assistant","content":"checking"},
                {"role":"tool","tool_call_id":"c1","content":"72F"},
                {"role":"user","content":"thanks"}
            ]}
            """));

        using var json = JsonDocument.Parse(translated);
        var inputText = json.RootElement.GetProperty("inputText").GetString()!;

        Assert.Contains("User: weather?", inputText);
        Assert.Contains("Bot: checking", inputText);
        Assert.Contains("Tool: 72F", inputText);
        Assert.Contains("User: thanks", inputText);
        Assert.EndsWith("Bot:", inputText);
    }

    [Fact]
    public void LlamaTranslator_BuildsChatTemplatePrompt_WithSystemAndTrailingAssistantHeader()
    {
        var translated = new LlamaPayloadTranslator().TranslateRequest(Encoding.UTF8.GetBytes("""
            {"model":"x","messages":[{"role":"system","content":"be terse"},{"role":"user","content":"hi"}],"max_tokens":100}
            """));

        using var json = JsonDocument.Parse(translated);
        var prompt = json.RootElement.GetProperty("prompt").GetString()!;

        Assert.StartsWith("<|begin_of_text|>", prompt);
        Assert.Contains("<|start_header_id|>system<|end_header_id|>\n\nbe terse<|eot_id|>", prompt);
        Assert.Contains("<|start_header_id|>user<|end_header_id|>\n\nhi<|eot_id|>", prompt);
        Assert.EndsWith("<|start_header_id|>assistant<|end_header_id|>\n\n", prompt);
        Assert.Equal(100, json.RootElement.GetProperty("max_gen_len").GetInt32());
    }

    [Fact]
    public void TitanStreamChunkTranslator_EmitsDeltaContent_AndFinishOnCompletionReason()
    {
        var translator = new TitanStreamChunkTranslator("bedrock-titan");

        var first = translator.TranslateChunk(Encoding.UTF8.GetBytes("""{"index":0,"outputText":"Hel"}"""));
        var second = translator.TranslateChunk(Encoding.UTF8.GetBytes("""{"index":1,"outputText":"lo","inputTextTokenCount":3,"totalOutputTextTokenCount":2,"completionReason":"FINISHED"}"""));
        var flush = translator.Flush();

        using var firstJson = JsonDocument.Parse(ExtractData(first));
        Assert.Equal("Hel", firstJson.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal("bedrock-titan", firstJson.RootElement.GetProperty("model").GetString());

        using var secondJson = JsonDocument.Parse(ExtractData(second));
        Assert.Equal("stop", secondJson.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(3, secondJson.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());

        Assert.Equal("data: [DONE]\n\n", Encoding.UTF8.GetString(flush));
    }

    [Fact]
    public void LlamaStreamChunkTranslator_EmitsDeltaContent_AndFinishOnStopReason()
    {
        var translator = new LlamaStreamChunkTranslator("bedrock-llama");

        var first = translator.TranslateChunk(Encoding.UTF8.GetBytes("""{"generation":"Hel"}"""));
        var second = translator.TranslateChunk(Encoding.UTF8.GetBytes("""{"generation":"lo","prompt_token_count":4,"generation_token_count":2,"stop_reason":"stop"}"""));

        using var firstJson = JsonDocument.Parse(ExtractData(first));
        Assert.Equal("Hel", firstJson.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal("bedrock-llama", firstJson.RootElement.GetProperty("model").GetString());

        using var secondJson = JsonDocument.Parse(ExtractData(second));
        Assert.Equal("stop", secondJson.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(4, secondJson.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
    }

    [Fact]
    public void BedrockRuntimeClientFactory_MissingRegion_Throws()
    {
        var route = new ResolvedModelRoute("m", "bedrock-anthropic", "anthropic.claude-3-5-sonnet-20241022-v2:0", new Uri("https://example.com"), "Authorization", []);
        var factory = new BedrockRuntimeClientFactory();

        Assert.Throws<InvalidOperationException>(() => factory.Create(route));
    }

    private static string ExtractData(byte[] sseLine)
    {
        var text = Encoding.UTF8.GetString(sseLine);
        const string prefix = "data: ";
        var start = text.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        return text[start..end];
    }

    // --- AWS application/vnd.amazon.eventstream binary frame encoder, test-only ---
    // Encodes one real event-stream message (prelude + headers + payload + CRC32s) per the Smithy spec,
    // so Claude_Streaming_... above exercises the actual AWS SDK's own ResponseStream decoder rather
    // than bypassing it - this codebase's production code never encodes this format (only ever consumes
    // what the SDK already decoded), so this encoder exists solely to build a realistic test fixture.

    /// <summary>
    /// Appends one event-stream frame whose payload is the Bedrock-specific <c>{"bytes": "&lt;base64&gt;"}</c>
    /// wrapper around <paramref name="nativeChunkJson"/> - confirmed against the installed AWSSDK.BedrockRuntime
    /// 4.0.100.5's actual unmarshalling behavior (not the raw native JSON directly): its
    /// <c>PayloadPartUnmarshaller</c> parses the frame payload as JSON and reads a base64 <c>bytes</c>
    /// field to populate <see cref="PayloadPart.Bytes"/>, matching how Bedrock's real wire protocol
    /// nests the native chunk one level deeper than some SDKs' code samples suggest.
    /// </summary>
    private static void AppendFrame(Stream destination, string eventType, string nativeChunkJson)
    {
        var wrapped = "{\"bytes\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(nativeChunkJson)) + "\"}";
        var frame = EncodeEventStreamMessage(eventType, wrapped);
        destination.Write(frame);
    }

    private static byte[] EncodeEventStreamMessage(string eventType, string payloadJson)
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        var headers = new List<byte>();

        void AddHeader(string name, string value)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            headers.Add((byte)nameBytes.Length);
            headers.AddRange(nameBytes);
            headers.Add(7); // header value type 7 == string
            var valueBytes = Encoding.UTF8.GetBytes(value);
            headers.Add((byte)(valueBytes.Length >> 8));
            headers.Add((byte)(valueBytes.Length & 0xFF));
            headers.AddRange(valueBytes);
        }

        AddHeader(":message-type", "event");
        AddHeader(":event-type", eventType);
        AddHeader(":content-type", "application/json");

        var headerBytes = headers.ToArray();
        var totalLength = 4 + 4 + 4 + headerBytes.Length + payload.Length + 4;

        var message = new byte[totalLength];
        var offset = 0;

        WriteUInt32BE(message, ref offset, (uint)totalLength);
        WriteUInt32BE(message, ref offset, (uint)headerBytes.Length);
        WriteUInt32BE(message, ref offset, Crc32(message.AsSpan(0, 8)));

        Array.Copy(headerBytes, 0, message, offset, headerBytes.Length);
        offset += headerBytes.Length;
        Array.Copy(payload, 0, message, offset, payload.Length);
        offset += payload.Length;

        WriteUInt32BE(message, ref offset, Crc32(message.AsSpan(0, offset)));

        return message;
    }

    private static void WriteUInt32BE(byte[] buffer, ref int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
        offset += 4;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private sealed class FakeBedrockRuntimeClientFactory : IBedrockRuntimeClientFactory
    {
        private readonly IAmazonBedrockRuntime _client;

        public FakeBedrockRuntimeClientFactory(IAmazonBedrockRuntime client) => _client = client;

        public IAmazonBedrockRuntime Create(ResolvedModelRoute route) => _client;
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

