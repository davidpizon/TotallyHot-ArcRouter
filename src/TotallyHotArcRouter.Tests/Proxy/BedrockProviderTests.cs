using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Coverage for the AWS Bedrock slice of unified API translation
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

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static IModelRouteResolver Resolver(string modelName, string providerModelId, string providerName,
        string awsRegion = AwsRegion)
    {
        return ModelRouteResolverTestFactory.Create(
            modelName: modelName,
            providerModelId: providerModelId,
            baseUrl: "https://bedrock-runtime.us-east-1.amazonaws.com",
            providerName: providerName,
            awsRegion: awsRegion);
    }

    private static ProxyMiddleware BuildMiddleware(
        string modelName,
        string providerModelId,
        IBedrockPayloadTranslator translator,
        IAmazonBedrockRuntime client,
        ITelemetryPublisher? telemetry = null)
    {
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: Resolver(modelName: modelName, providerModelId: providerModelId,
                providerName: translator.Provider));
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            [translator.Provider] = translator
        };

        return new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetry,
                Translators = translators,
                BedrockClientFactory = new FakeBedrockRuntimeClientFactory(client)
            }
        );
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
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
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
                return new InvokeModelResponse
                {
                    Body = new MemoryStream(Encoding.UTF8.GetBytes(claudeResponse)),
                    ContentType = "application/json"
                };
            });

        var capturing = new CapturingTelemetryPublisher();
        var middleware = BuildMiddleware(modelName: "claude-sonnet-bedrock",
            providerModelId: "anthropic.claude-3-5-sonnet-20241022-v2:0",
            translator: new AnthropicOnBedrockPayloadTranslator(), client: mockClient.Object, telemetry: capturing);

        var context = BuildContext("""
                                   {"model":"claude-sonnet-bedrock","messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"}]}
                                   """);

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: "anthropic.claude-3-5-sonnet-20241022-v2:0", actual: capturedRequest!.ModelId);
        using var forwardedBody = JsonDocument.Parse(Encoding.UTF8.GetString(capturedRequest.Body.ToArray()));
        Assert.Equal(expected: "bedrock-2023-05-31",
            actual: forwardedBody.RootElement.GetProperty("anthropic_version").GetString());
        Assert.False(condition: forwardedBody.RootElement.TryGetProperty(propertyName: "model", value: out _),
            userMessage: "Bedrock body must not carry 'model' - it's the SDK's ModelId.");
        Assert.Equal(expected: "be brief", actual: forwardedBody.RootElement.GetProperty("system").GetString());

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal(expected: "Hello from Bedrock Claude.",
            actual: openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString());

        var published = await capturing.WaitForEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(12, actual: published.PromptTokens);
        Assert.Equal(6, actual: published.CompletionTokens);
    }

    [Fact]
    public async Task Claude_Streaming_TranslatesRealEventStreamFrames_ToOpenAiChunks()
    {
        // A genuine AWS application/vnd.amazon.eventstream binary encoding (prelude + headers + payload
        // + CRC32s, per the Smithy spec) - exercised through the real AWS SDK's own ResponseStream
        // decoder, not a fake shortcut, so this proves the SDK-decoded-chunk path end-to-end.
        var frames = new MemoryStream();
        AppendFrame(destination: frames, eventType: "chunk",
            """{"type":"message_start","message":{"id":"msg_stream_01","model":"anthropic.claude-3-5-sonnet-20241022-v2:0","usage":{"input_tokens":5}}}""");
        AppendFrame(destination: frames, eventType: "chunk",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        AppendFrame(destination: frames, eventType: "chunk",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}""");
        AppendFrame(destination: frames, eventType: "chunk",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}""");
        AppendFrame(destination: frames, eventType: "chunk", """{"type":"content_block_stop","index":0}""");
        AppendFrame(destination: frames, eventType: "chunk",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}""");
        AppendFrame(destination: frames, eventType: "chunk", """{"type":"message_stop"}""");
        frames.Position = 0;

        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c =>
                c.InvokeModelWithResponseStreamAsync(It.IsAny<InvokeModelWithResponseStreamRequest>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvokeModelWithResponseStreamResponse { Body = new ResponseStream(frames) });

        var middleware = BuildMiddleware(modelName: "claude-sonnet-bedrock",
            providerModelId: "anthropic.claude-3-5-sonnet-20241022-v2:0",
            translator: new AnthropicOnBedrockPayloadTranslator(), client: mockClient.Object);

        var context = BuildContext("""
                                   {"model":"claude-sonnet-bedrock","messages":[{"role":"user","content":"hi"}],"stream":true}
                                   """);

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        var body = ReadResponse(context);
        var dataLines = body.Split(separator: "\n\n", options: StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith(value: "data: ", comparisonType: StringComparison.Ordinal))
            .Select(l => l["data: ".Length..])
            .ToList();

        Assert.Equal(expected: "[DONE]", actual: dataLines[^1]);
        var chunks = dataLines.Where(l => l != "[DONE]").Select(l => JsonDocument.Parse(l)).ToList();
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
    public async Task Claude_SdkThrows_Returns502WithErrorEnvelope()
    {
        var mockClient = new Mock<IAmazonBedrockRuntime>();
        mockClient.Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonBedrockRuntimeException("access denied"));

        var middleware = BuildMiddleware(modelName: "claude-sonnet-bedrock",
            providerModelId: "anthropic.claude-3-5-sonnet-20241022-v2:0",
            translator: new AnthropicOnBedrockPayloadTranslator(), client: mockClient.Object);
        var context = BuildContext("""{"model":"claude-sonnet-bedrock","messages":[{"role":"user","content":"hi"}]}""");

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status502BadGateway, actual: context.Response.StatusCode);
        using var error = JsonDocument.Parse(ReadResponse(context));
        var message = error.RootElement.GetProperty("error").GetProperty("message").GetString();
        // The 502 envelope must carry a generic message, not the raw SDK exception text, which can leak
        // internal endpoint/region/request-id detail (the exception is logged server-side instead).
        Assert.Equal(expected: "The upstream provider is unavailable.", actual: message);
        Assert.DoesNotContain(expectedSubstring: "access denied", actualString: message);
    }

    // --- Bedrock failover: a Bedrock candidate is no longer terminal (docs/router/agent-resilience-strategies.md) ---

    private static IModelRouteResolver TwoCandidateResolver(
        string primaryModel, string primaryProvider, string primaryProviderModelId,
        string backupModel, string backupProvider, string backupProviderModelId)
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [primaryProvider] = new()
                { BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com", AwsRegion = AwsRegion },
                [backupProvider] = new()
                { BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com", AwsRegion = AwsRegion }
            },
            ModelList =
            [
                new ModelRouteEntry
                    { ModelName = primaryModel, Provider = primaryProvider, ProviderModelId = primaryProviderModelId },
                new ModelRouteEntry
                    { ModelName = backupModel, Provider = backupProvider, ProviderModelId = backupProviderModelId }
            ]
        };

        return new ModelRouteResolver(store: new InMemoryProviderConfigStore(options),
            environment: Mock.Of<IEnvironmentVariableProvider>());
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
                Body = new MemoryStream("""{"id":"m","type":"message","role":"assistant","model":"x","content":[{"type":"text","text":"served by backup"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""u8.ToArray())
            });

        var translator = new AnthropicOnBedrockPayloadTranslator();
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        { [translator.Provider] = translator };
        var resolver = TwoCandidateResolver(
            primaryModel: "primary-claude", primaryProvider: translator.Provider,
            primaryProviderModelId: "primary-model-id",
            backupModel: "backup-claude", backupProvider: translator.Provider,
            backupProviderModelId: "backup-model-id");

        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
                modelRouteResolver: resolver),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators,
                BedrockClientFactory = new FakeBedrockRuntimeClientFactory(mockClient.Object)
            }
        );

        var context = BuildContext("""{"model":"primary-claude","messages":[{"role":"user","content":"hi"}]}""");
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal(expected: "served by backup",
            actual: openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString());
        mockClient.Verify(
            expression: c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()),
            times: Times.Exactly(2));
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
                    throw new AmazonClientException(
                        "Failed to resolve bearer token in DefaultAWSTokenIdentityResolver");
            })
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = new MemoryStream("""{"inputTextTokenCount":1,"results":[{"tokenCount":1,"outputText":"served by backup","completionReason":"FINISHED"}]}"""u8.ToArray())
            });

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["bedrock-anthropic"] = new AnthropicOnBedrockPayloadTranslator(),
            ["bedrock-titan"] = new TitanPayloadTranslator()
        };
        var resolver = TwoCandidateResolver(
            primaryModel: "primary-claude", primaryProvider: "bedrock-anthropic",
            primaryProviderModelId: "anthropic.claude-3-5-sonnet-20241022-v2:0",
            backupModel: "backup-titan", backupProvider: "bedrock-titan",
            backupProviderModelId: "amazon.titan-text-premier-v1:0");

        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
                modelRouteResolver: resolver),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators,
                BedrockClientFactory = new FakeBedrockRuntimeClientFactory(mockClient.Object)
            }
        );

        var context = BuildContext("""{"model":"primary-claude","messages":[{"role":"user","content":"hi"}]}""");
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal(expected: "served by backup",
            actual: openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString());
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
                if (req.ModelId == "backup-model-id") backupAttempted = true;
            })
            .ThrowsAsync(
                new AmazonClientException("Failed to resolve bearer token in DefaultAWSTokenIdentityResolver"));

        var translator = new AnthropicOnBedrockPayloadTranslator();
        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        { [translator.Provider] = translator };
        var resolver = TwoCandidateResolver(
            primaryModel: "primary-claude", primaryProvider: translator.Provider,
            primaryProviderModelId: "primary-model-id",
            backupModel: "backup-claude", backupProvider: translator.Provider,
            backupProviderModelId: "backup-model-id");

        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
                modelRouteResolver: resolver),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators,
                BedrockClientFactory = new FakeBedrockRuntimeClientFactory(mockClient.Object)
            }
        );

        var context = BuildContext("""{"model":"primary-claude","messages":[{"role":"user","content":"hi"}]}""");
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status401Unauthorized, actual: context.Response.StatusCode);
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

        var middleware = BuildMiddleware(modelName: "titan-text-premier-bedrock",
            providerModelId: "amazon.titan-text-premier-v1:0", translator: new TitanPayloadTranslator(),
            client: mockClient.Object);
        var context =
            BuildContext("""{"model":"titan-text-premier-bedrock","messages":[{"role":"user","content":"hi"}]}""");

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: "amazon.titan-text-premier-v1:0", actual: capturedRequest!.ModelId);
        using var forwardedBody = JsonDocument.Parse(Encoding.UTF8.GetString(capturedRequest.Body.ToArray()));
        Assert.Contains(expectedSubstring: "User: hi",
            actualString: forwardedBody.RootElement.GetProperty("inputText").GetString());

        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal(expected: "bedrock-titan", actual: openAi.RootElement.GetProperty("model").GetString());
        Assert.Equal(expected: "Hello from Titan.",
            actual: openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString());
        Assert.Equal(expected: "stop",
            actual: openAi.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
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

        var middleware = BuildMiddleware(modelName: "llama3-70b-bedrock",
            providerModelId: "meta.llama3-70b-instruct-v1:0", translator: new LlamaPayloadTranslator(),
            client: mockClient.Object);
        var context = BuildContext("""{"model":"llama3-70b-bedrock","messages":[{"role":"user","content":"hi"}]}""");

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: "meta.llama3-70b-instruct-v1:0", actual: capturedRequest!.ModelId);
        using var forwardedBody = JsonDocument.Parse(Encoding.UTF8.GetString(capturedRequest.Body.ToArray()));
        Assert.Contains(expectedSubstring: "<|start_header_id|>user<|end_header_id|>",
            actualString: forwardedBody.RootElement.GetProperty("prompt").GetString());

        using var openAi = JsonDocument.Parse(ReadResponse(context));
        Assert.Equal(expected: "Hello from Llama.",
            actual: openAi.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString());
        Assert.Equal(expected: "bedrock-llama", actual: openAi.RootElement.GetProperty("model").GetString());
    }

    // --- Direct translator unit coverage (no SDK involved) ---

    [Fact]
    public void TitanTranslator_BuildsUserBotTranscript_WithToolResultLabeled()
    {
        var translated = new TitanPayloadTranslator().TranslateRequest("""
            {"model":"x","messages":[
                {"role":"user","content":"weather?"},
                {"role":"assistant","content":"checking"},
                {"role":"tool","tool_call_id":"c1","content":"72F"},
                {"role":"user","content":"thanks"}
            ]}
            """u8.ToArray());

        using var json = JsonDocument.Parse(translated);
        var inputText = json.RootElement.GetProperty("inputText").GetString()!;

        Assert.Contains(expectedSubstring: "User: weather?", actualString: inputText);
        Assert.Contains(expectedSubstring: "Bot: checking", actualString: inputText);
        Assert.Contains(expectedSubstring: "Tool: 72F", actualString: inputText);
        Assert.Contains(expectedSubstring: "User: thanks", actualString: inputText);
        Assert.EndsWith(expectedEndString: "Bot:", actualString: inputText);
    }

    [Fact]
    public void LlamaTranslator_BuildsChatTemplatePrompt_WithSystemAndTrailingAssistantHeader()
    {
        var translated = new LlamaPayloadTranslator().TranslateRequest("""
            {"model":"x","messages":[{"role":"system","content":"be terse"},{"role":"user","content":"hi"}],"max_tokens":100}
            """u8.ToArray());

        using var json = JsonDocument.Parse(translated);
        var prompt = json.RootElement.GetProperty("prompt").GetString()!;

        Assert.StartsWith(expectedStartString: "<|begin_of_text|>", actualString: prompt);
        Assert.Contains(expectedSubstring: "<|start_header_id|>system<|end_header_id|>\n\nbe terse<|eot_id|>",
            actualString: prompt);
        Assert.Contains(expectedSubstring: "<|start_header_id|>user<|end_header_id|>\n\nhi<|eot_id|>",
            actualString: prompt);
        Assert.EndsWith(expectedEndString: "<|start_header_id|>assistant<|end_header_id|>\n\n", actualString: prompt);
        Assert.Equal(100, actual: json.RootElement.GetProperty("max_gen_len").GetInt32());
    }

    [Fact]
    public void TitanStreamChunkTranslator_EmitsDeltaContent_AndFinishOnCompletionReason()
    {
        var translator = new TitanStreamChunkTranslator("bedrock-titan");

        var first = translator.TranslateChunk("""{"index":0,"outputText":"Hel"}"""u8.ToArray());
        var second = translator.TranslateChunk("""{"index":1,"outputText":"lo","inputTextTokenCount":3,"totalOutputTextTokenCount":2,"completionReason":"FINISHED"}"""u8.ToArray());
        var flush = translator.Flush();

        using var firstJson = JsonDocument.Parse(ExtractData(first));
        Assert.Equal(expected: "Hel",
            actual: firstJson.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content")
                .GetString());
        Assert.Equal(expected: "bedrock-titan", actual: firstJson.RootElement.GetProperty("model").GetString());

        using var secondJson = JsonDocument.Parse(ExtractData(second));
        Assert.Equal(expected: "stop",
            actual: secondJson.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(3, actual: secondJson.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());

        Assert.Equal(expected: "data: [DONE]\n\n", actual: Encoding.UTF8.GetString(flush));
    }

    [Fact]
    public void LlamaStreamChunkTranslator_EmitsDeltaContent_AndFinishOnStopReason()
    {
        var translator = new LlamaStreamChunkTranslator("bedrock-llama");

        var first = translator.TranslateChunk("""{"generation":"Hel"}"""u8.ToArray());
        var second = translator.TranslateChunk("""{"generation":"lo","prompt_token_count":4,"generation_token_count":2,"stop_reason":"stop"}"""u8.ToArray());

        using var firstJson = JsonDocument.Parse(ExtractData(first));
        Assert.Equal(expected: "Hel",
            actual: firstJson.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content")
                .GetString());
        Assert.Equal(expected: "bedrock-llama", actual: firstJson.RootElement.GetProperty("model").GetString());

        using var secondJson = JsonDocument.Parse(ExtractData(second));
        Assert.Equal(expected: "stop",
            actual: secondJson.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(4, actual: secondJson.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
    }

    [Fact]
    public void BedrockRuntimeClientFactory_MissingRegion_Throws()
    {
        var route = new ResolvedModelRoute(ModelName: "m", Provider: "bedrock-anthropic",
            ProviderModelId: "anthropic.claude-3-5-sonnet-20241022-v2:0",
            UpstreamBaseUrl: new Uri("https://example.com"), AuthHeaderName: "Authorization", ExtraHeaders: []);
        var factory = new BedrockRuntimeClientFactory();

        Assert.Throws<InvalidOperationException>(() => factory.Create(route));
    }

    private static string ExtractData(byte[] sseLine)
    {
        var text = Encoding.UTF8.GetString(sseLine);
        const string prefix = "data: ";
        var start = text.IndexOf(value: prefix, comparisonType: StringComparison.Ordinal) + prefix.Length;
        var end = text.IndexOf(value: "\n\n", startIndex: start, comparisonType: StringComparison.Ordinal);
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
        var frame = EncodeEventStreamMessage(eventType: eventType, payloadJson: wrapped);
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

        AddHeader(name: ":message-type", value: "event");
        AddHeader(name: ":event-type", value: eventType);
        AddHeader(name: ":content-type", value: "application/json");

        var headerBytes = headers.ToArray();
        var totalLength = 4 + 4 + 4 + headerBytes.Length + payload.Length + 4;

        var message = new byte[totalLength];
        var offset = 0;

        WriteUInt32BE(buffer: message, offset: ref offset, value: (uint)totalLength);
        WriteUInt32BE(buffer: message, offset: ref offset, value: (uint)headerBytes.Length);
        WriteUInt32BE(buffer: message, offset: ref offset, value: Crc32(message.AsSpan(0, 8)));

        Array.Copy(sourceArray: headerBytes, 0, destinationArray: message, destinationIndex: offset,
            length: headerBytes.Length);
        offset += headerBytes.Length;
        Array.Copy(sourceArray: payload, 0, destinationArray: message, destinationIndex: offset,
            length: payload.Length);
        offset += payload.Length;

        WriteUInt32BE(buffer: message, offset: ref offset, value: Crc32(message.AsSpan(0, length: offset)));

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

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;

            table[i] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];

        return crc ^ 0xFFFFFFFFu;
    }

    private sealed class FakeBedrockRuntimeClientFactory : IBedrockRuntimeClientFactory
    {
        private readonly IAmazonBedrockRuntime _client;

        public FakeBedrockRuntimeClientFactory(IAmazonBedrockRuntime client)
        {
            _client = client;
        }

        public IAmazonBedrockRuntime Create(ResolvedModelRoute route)
        {
            return _client;
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