using Moq;
using System.Net;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Coverage for the provider endpoint-flavor scan (<c>docs/router/tool-call-normalization.md</c> §3.3,
/// Phase 2). Every probe is stubbed - no live provider, matching the verification approach
/// <c>unified-api-translation.md</c> §2 settled on for the translator work.
/// <para>
/// The cases that carry weight are the version-suffix handling (LM Studio and Ollama are normally
/// configured with a <c>/v1</c> base, but their native APIs sit at the root, so probing the wrong URL would
/// silently report the native flavor absent and cost tier-1 template detection its best source) and the
/// insistence that an unreachable provider records a result rather than throwing.
/// </para>
/// </summary>
public sealed class ProviderEndpointScannerTests
{
    private const string OpenAiBody = """{"object":"list","data":[{"id":"gpt-5.4"}]}""";

    private const string AnthropicBody =
        """{"data":[{"id":"claude-4","type":"model"}],"has_more":false,"first_id":"claude-4"}""";

    private const string LmStudioBody = """{"object":"list","data":[{"id":"qwen2.5-coder"}]}""";
    private const string OllamaTagsBody = """{"models":[{"name":"qwen2.5-coder:7b"}]}""";

    private const string AnthropicMessageBody =
        """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"hi"}]}""";

    private const string AnthropicErrorBody =
        """{"type":"error","error":{"type":"not_found_error","message":"model: arcrouter-capability-probe"}}""";

    private const string OpenAiStyleErrorBody =
        """{"error":{"message":"Unknown model","type":"invalid_request_error"}}""";

    /// <summary>Answers 200 for any URL whose path is in <paramref name="okPaths"/>, 404 otherwise.</summary>
    private static ProviderEndpointScanner ScannerFor(
        IReadOnlyDictionary<string, string> okPaths,
        List<string>? recordedUrls = null)
    {
        var handler = new DelegatingHandlerStub(request =>
        {
            var url = request.RequestUri!.ToString();
            recordedUrls?.Add(url);

            var match = okPaths.FirstOrDefault(kvp =>
                url.EndsWith(value: kvp.Key, comparisonType: StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(match.Key is not null
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(match.Value) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        return new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());
    }

    /// <summary>
    /// Answers <c>GET /v1/models</c> with <paramref name="modelsBody"/> and <c>POST /v1/messages</c> with
    /// <paramref name="messagesStatus"/>/<paramref name="messagesBody"/>, everything else 404 - the fixture
    /// the opt-in messages-probe tests build on.
    /// </summary>
    private static ProviderEndpointScanner ScannerForMessagesProbe(
        string modelsBody,
        HttpStatusCode messagesStatus,
        string messagesBody,
        List<HttpRequestMessage>? recordedRequests = null)
    {
        var handler = new DelegatingHandlerStub(request =>
        {
            recordedRequests?.Add(request);

            if (request.Method == HttpMethod.Get && request.RequestUri!.ToString()
                    .EndsWith(value: "/v1/models", comparisonType: StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(modelsBody) });

            if (request.Method == HttpMethod.Post && request.RequestUri!.ToString()
                    .EndsWith(value: "/v1/messages", comparisonType: StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(messagesStatus)
                { Content = new StringContent(messagesBody) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        return new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());
    }

    private static ProviderOptions Provider(string baseUrl, bool probeAnthropicMessages = false)
    {
        return new ProviderOptions { BaseUrl = baseUrl, ProbeAnthropicMessages = probeAnthropicMessages };
    }

    // ----- Each flavor detected -----

    [Fact]
    public async Task AnOpenAiShapedEndpoint_IsDetected()
    {
        var scanner = ScannerFor(new Dictionary<string, string> { ["/v1/models"] = OpenAiBody });

        var result = await scanner.ScanAsync(providerKey: "openai", provider: Provider("https://api.openai.com"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.OpenAiCompatible);
        Assert.False(result.AnthropicCompatible);
        Assert.False(result.LmStudioNative);
        Assert.False(result.OllamaNative);
        Assert.Null(result.ScanError);
    }

    [Fact]
    public async Task AnAnthropicShapedModelList_IsDistinguishedFromOpenAi()
    {
        // Both serve GET /v1/models with a `data` array, so the status code cannot separate them - only
        // Anthropic's pagination markers can.
        var scanner = ScannerFor(new Dictionary<string, string> { ["/v1/models"] = AnthropicBody });

        var result = await scanner.ScanAsync(providerKey: "anthropic", provider: Provider("https://api.anthropic.com"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.AnthropicCompatible);
        Assert.False(result.OpenAiCompatible);
    }

    [Fact]
    public async Task LmStudioNative_IsDetectedAlongsideItsOpenAiRoutes()
    {
        var scanner = ScannerFor(new Dictionary<string, string>
        {
            ["/v1/models"] = OpenAiBody,
            ["/api/v0/models"] = LmStudioBody
        });

        var result = await scanner.ScanAsync(providerKey: "lmstudio", provider: Provider("http://localhost:1234/v1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.OpenAiCompatible);
        Assert.True(result.LmStudioNative);
        Assert.False(result.OllamaNative);
    }

    // ----- Opt-in Anthropic /v1/messages probe -----

    [Fact]
    public async Task TheMessagesProbe_IsNotSentByDefault()
    {
        // ProbeAnthropicMessages defaults to false - the whole point of the opt-in gate.
        var requests = new List<HttpRequestMessage>();
        var scanner = ScannerForMessagesProbe(modelsBody: OpenAiBody, messagesStatus: HttpStatusCode.OK,
            messagesBody: AnthropicMessageBody, recordedRequests: requests);

        var result = await scanner.ScanAsync(providerKey: "lmstudio", provider: Provider("http://localhost:1234/v1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.AnthropicCompatible);
        Assert.DoesNotContain(collection: requests, filter: r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TheMessagesProbe_DetectsCompatibility_WhenOptedInAndTheModelListDidNot()
    {
        // LM Studio's /v1/models is plain OpenAI shape, but it separately answers /v1/messages.
        var scanner = ScannerForMessagesProbe(modelsBody: OpenAiBody, messagesStatus: HttpStatusCode.OK,
            messagesBody: AnthropicMessageBody);

        var result = await scanner.ScanAsync(
            providerKey: "lmstudio", provider: Provider(baseUrl: "http://localhost:1234/v1", true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.OpenAiCompatible);
        Assert.True(result.AnthropicCompatible);
    }

    [Fact]
    public async Task TheMessagesProbe_DetectsCompatibility_ViaAnAnthropicShapedErrorEnvelope()
    {
        // The sentinel model id never exists on a real deployment - a well-formed Anthropic error response
        // still proves the endpoint speaks the dialect, the expected outcome for almost every real probe.
        var scanner = ScannerForMessagesProbe(modelsBody: OpenAiBody, messagesStatus: HttpStatusCode.NotFound,
            messagesBody: AnthropicErrorBody);

        var result = await scanner.ScanAsync(
            providerKey: "lmstudio", provider: Provider(baseUrl: "http://localhost:1234/v1", true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.AnthropicCompatible);
    }

    [Fact]
    public async Task TheMessagesProbe_DoesNotTreatAGenericErrorAsAnthropicCompatible()
    {
        // A plain OpenAI-style error body (no top-level "type":"error" envelope) must not be mistaken for
        // Anthropic's own error shape - most 404s for an unrecognized route will look like this or worse.
        var scanner = ScannerForMessagesProbe(modelsBody: OpenAiBody, messagesStatus: HttpStatusCode.NotFound,
            messagesBody: OpenAiStyleErrorBody);

        var result = await scanner.ScanAsync(
            providerKey: "openai", provider: Provider(baseUrl: "https://api.openai.com", true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.AnthropicCompatible);
    }

    [Fact]
    public async Task TheMessagesProbe_IsSkipped_WhenTheModelListAlreadyProvedAnthropicCompatibility()
    {
        // Opting in must never double a network call the scan would have made anyway.
        var requests = new List<HttpRequestMessage>();
        var scanner = ScannerForMessagesProbe(modelsBody: AnthropicBody, messagesStatus: HttpStatusCode.OK,
            messagesBody: AnthropicMessageBody, recordedRequests: requests);

        var result = await scanner.ScanAsync(
            providerKey: "anthropic", provider: Provider(baseUrl: "https://api.anthropic.com", true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.AnthropicCompatible);
        Assert.DoesNotContain(collection: requests, filter: r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TheMessagesProbe_SendsTheSentinelModelIdAndAOneTokenBudget_NotARealModel()
    {
        HttpRequestMessage? captured = null;
        string? capturedContent = null;
        var handler = new DelegatingHandlerStub(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                captured = request;
                capturedContent = await request.Content!.ReadAsStringAsync();
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(OpenAiBody) };
        });
        var scanner = new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        await scanner.ScanAsync(
            providerKey: "lmstudio", provider: Provider(baseUrl: "http://localhost:1234/v1", true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains(expectedSubstring: "arcrouter-capability-probe", actualString: capturedContent);
        Assert.Contains(expectedSubstring: "\"max_tokens\":1", actualString: capturedContent);
    }

    [Fact]
    public async Task TheMessagesProbe_TargetsMessages_NotModels()
    {
        var urls = new List<string>();
        var handler = new DelegatingHandlerStub(request =>
        {
            urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var scanner = new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        await scanner.ScanAsync(
            providerKey: "lmstudio", provider: Provider(baseUrl: "http://localhost:1234/v1", true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expected: "http://localhost:1234/v1/messages", collection: urls);
    }

    [Fact]
    public async Task AnUnreachableEndpoint_WithMessagesProbingEnabled_StillRecordsAResultRatherThanThrowing()
    {
        var handler = new DelegatingHandlerStub(_ => throw new HttpRequestException("connection refused"));
        var scanner = new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var result = await scanner.ScanAsync(
            providerKey: "dead", provider: Provider(baseUrl: "http://localhost:9/v1", true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.AnthropicCompatible);
        Assert.Contains(expectedSubstring: "connection refused", actualString: result.ScanError);
    }

    [Fact]
    public async Task AMessagesProbeThatAnsweredButDidNotMatch_IsNamedInScanError_WhenNothingElseAnswered()
    {
        // A 404 with an unrelated body is not an exception, so it must not vanish from ScanError just
        // because ProbeAnthropicMessagesAsync has no exception message to report.
        var scanner = ScannerForMessagesProbe(
            modelsBody: "<html>not a model list</html>",
            messagesStatus: HttpStatusCode.NotFound,
            messagesBody: OpenAiStyleErrorBody);

        var result = await scanner.ScanAsync(
            providerKey: "dead", provider: Provider(baseUrl: "http://localhost:9/v1", true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.AnthropicCompatible);
        Assert.Contains(expectedSubstring: "/v1/messages", actualString: result.ScanError);
        Assert.Contains(expectedSubstring: "did not match the Anthropic Messages API shape",
            actualString: result.ScanError);
    }

    // ----- json_schema support is inferred from the native flavors, never probed -----

    [Theory]
    [InlineData("/api/v0/models", LmStudioBody)]
    [InlineData("/api/tags", OllamaTagsBody)]
    public async Task ANativeLocalRuntime_ReportsJsonSchemaSupport(string nativePath, string nativeBody)
    {
        // Both back response_format json_schema with real grammar-constrained sampling, which is what makes
        // the `constrained` dialect usable for their models.
        var scanner = ScannerFor(new Dictionary<string, string>
        {
            ["/v1/models"] = OpenAiBody,
            [nativePath] = nativeBody
        });

        var result = await scanner.ScanAsync(providerKey: "local", provider: Provider("http://localhost:1234/v1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.JsonSchemaResponseFormat);
    }

    [Fact]
    public async Task APlainOpenAiEndpoint_DoesNotReportJsonSchemaSupport()
    {
        // Deliberately conservative. OpenAiCompatible is true for every cloud provider, so inferring schema
        // support from it would attach a response_format to requests bound for upstreams that may reject it
        // outright - and constrained mode exists for local models, not for these.
        var scanner = ScannerFor(new Dictionary<string, string> { ["/v1/models"] = OpenAiBody });

        var result = await scanner.ScanAsync(providerKey: "openai", provider: Provider("https://api.openai.com"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.JsonSchemaResponseFormat);
    }

    [Fact]
    public async Task AnUnreachableProvider_ReportsNoJsonSchemaSupport()
    {
        var scanner = ScannerFor(new Dictionary<string, string>());

        var result = await scanner.ScanAsync(providerKey: "dead", provider: Provider("http://localhost:9/v1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.JsonSchemaResponseFormat);
        Assert.NotNull(result.ScanError);
    }

    [Fact]
    public async Task OllamaNative_IsDetectedAlongsideItsOpenAiRoutes()
    {
        var scanner = ScannerFor(new Dictionary<string, string>
        {
            ["/v1/models"] = OpenAiBody,
            ["/api/tags"] = OllamaTagsBody
        });

        var result = await scanner.ScanAsync(providerKey: "ollama", provider: Provider("http://localhost:11434/v1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.OpenAiCompatible);
        Assert.True(result.OllamaNative);
    }

    // ----- URL construction: the part that silently breaks detection if wrong -----

    [Fact]
    public async Task NativeProbes_TargetTheHostRoot_NotTheV1Base()
    {
        // Probing http://localhost:11434/v1/api/tags would 404 on every Ollama install, recording the
        // native flavor as absent and costing tier-1 template detection its best source.
        var urls = new List<string>();
        var scanner = ScannerFor(okPaths: new Dictionary<string, string>(), recordedUrls: urls);

        await scanner.ScanAsync(providerKey: "ollama", provider: Provider("http://localhost:11434/v1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expected: "http://localhost:11434/api/tags", collection: urls);
        Assert.Contains(expected: "http://localhost:11434/api/v0/models", collection: urls);
        Assert.DoesNotContain(collection: urls,
            filter: u => u.Contains(value: "/v1/api/", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABaseWithoutAVersionSegment_StillProbesV1Models()
    {
        var urls = new List<string>();
        var scanner = ScannerFor(okPaths: new Dictionary<string, string>(), recordedUrls: urls);

        await scanner.ScanAsync(providerKey: "openai", provider: Provider("https://api.openai.com"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expected: "https://api.openai.com/v1/models", collection: urls);
    }

    [Fact]
    public async Task AV1BetaBase_GetsModelsAppended_RatherThanASecondVersionSegment()
    {
        // Gemini-style bases carry /v1beta, which ManagementFacade.BuildModelsUrl matches with Contains
        // rather than EndsWith. Appending /v1/models here would produce .../v1beta/v1/models and 404.
        var urls = new List<string>();
        var scanner = ScannerFor(okPaths: new Dictionary<string, string>(), recordedUrls: urls);

        await scanner.ScanAsync(
            providerKey: "gemini",
            provider: Provider("https://generativelanguage.googleapis.com/v1beta"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expected: "https://generativelanguage.googleapis.com/v1beta/models", collection: urls);
        Assert.DoesNotContain(collection: urls,
            filter: u => u.Contains(value: "/v1beta/v1/", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATrailingSlash_DoesNotProduceADoubleSlash()
    {
        var urls = new List<string>();
        var scanner = ScannerFor(okPaths: new Dictionary<string, string>(), recordedUrls: urls);

        await scanner.ScanAsync(providerKey: "ollama", provider: Provider("http://localhost:11434/v1/"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(collection: urls,
            filter: u => u.Contains(value: "//api/", comparisonType: StringComparison.Ordinal));
        Assert.Contains(expected: "http://localhost:11434/api/tags", collection: urls);
    }

    // ----- Failure handling: a scan must always produce a result -----

    [Fact]
    public async Task AProviderThatAnswersNothing_RecordsAllFalseWithAnError_RatherThanThrowing()
    {
        var scanner = ScannerFor(new Dictionary<string, string>());

        var result = await scanner.ScanAsync(providerKey: "dead", provider: Provider("http://localhost:9/v1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.OpenAiCompatible);
        Assert.False(result.LmStudioNative);
        Assert.False(result.OllamaNative);
        Assert.False(result.AnthropicCompatible);
        Assert.False(string.IsNullOrWhiteSpace(result.ScanError));
    }

    [Fact]
    public async Task AnOrdinaryCloudProvider_ReportsNoError_DespiteItsNativeProbes404ing()
    {
        // A provider that speaks OpenAI and nothing else is completely healthy. Reporting the two expected
        // 404s as an error would make every normal cloud provider look broken in the GUI.
        var scanner = ScannerFor(new Dictionary<string, string> { ["/v1/models"] = OpenAiBody });

        var result = await scanner.ScanAsync(providerKey: "openai", provider: Provider("https://api.openai.com"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.ScanError);
    }

    [Fact]
    public async Task AnUnreachableHost_IsRecordedRatherThanThrown()
    {
        var handler = new DelegatingHandlerStub(_ => throw new HttpRequestException("connection refused"));
        var scanner = new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var result = await scanner.ScanAsync(providerKey: "dead", provider: Provider("http://localhost:9/v1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.OpenAiCompatible);
        Assert.Contains(expectedSubstring: "connection refused", actualString: result.ScanError);
    }

    [Fact]
    public async Task AMalformedBaseUrl_IsReportedWithoutAnyProbe()
    {
        var urls = new List<string>();
        var scanner = ScannerFor(okPaths: new Dictionary<string, string>(), recordedUrls: urls);

        var result = await scanner.ScanAsync(providerKey: "broken", provider: Provider("not-a-url"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(urls);
        Assert.Contains(expectedSubstring: "Invalid BaseUrl", actualString: result.ScanError);
    }

    [Fact]
    public async Task A200WithANonJsonBody_IsNotMistakenForAModelList()
    {
        // A captive portal or reverse proxy answering 200 with HTML must not read as OpenAI-compatible.
        var scanner = ScannerFor(new Dictionary<string, string> { ["/v1/models"] = "<html>hello</html>" });

        var result = await scanner.ScanAsync(providerKey: "weird", provider: Provider("https://example.invalid"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.OpenAiCompatible);
        Assert.False(result.AnthropicCompatible);
    }

    [Fact]
    public async Task A200WithANonJsonBody_IsExplainedInScanError()
    {
        // The probe succeeded at the HTTP level, so it carries no Error of its own - without explicit
        // handling the operator would see only the two expected native 404s and conclude the endpoint was
        // unreachable, when in fact it answered with something unusable.
        var scanner = ScannerFor(new Dictionary<string, string> { ["/v1/models"] = "<html>hello</html>" });

        var result = await scanner.ScanAsync(providerKey: "weird", provider: Provider("https://example.invalid"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expectedSubstring: "not valid JSON", actualString: result.ScanError);
    }

    [Fact]
    public async Task A200WithJsonButNoDataArray_IsExplainedInScanError()
    {
        var scanner = ScannerFor(new Dictionary<string, string> { ["/v1/models"] = """{"message":"unauthorized"}""" });

        var result = await scanner.ScanAsync(providerKey: "weird", provider: Provider("https://example.invalid"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.OpenAiCompatible);
        Assert.Contains(expectedSubstring: "no 'data' array", actualString: result.ScanError);
    }

    // ----- Credentials are applied exactly as the forwarding path applies them -----

    [Fact]
    public async Task TheProvidersCustomHeadersAreSent_SoAnAnthropicProviderCanAnswerAtAll()
    {
        // Anthropic rejects a request with no anthropic-version. The header lives in provider config rather
        // than in code, so the probe must send the same set the forwarding path does.
        HttpRequestMessage? captured = null;
        var handler = new DelegatingHandlerStub(request =>
        {
            captured ??= request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var scanner = new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var provider = new ProviderOptions
        {
            BaseUrl = "https://api.anthropic.com",
            AuthHeaderName = "x-api-key",
            Headers =
            [
                new ProviderHeader { Name = "x-api-key", Value = "sk-test" },
                new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01" }
            ]
        };

        await scanner.ScanAsync(providerKey: "anthropic", provider: provider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.Contains("anthropic-version"));
        Assert.True(captured.Headers.Contains("x-api-key"));
    }

    [Fact]
    public async Task AnInvalidHeaderName_DoesNotThrow_AndStillProducesAResult()
    {
        // ScanAsync's contract is that no runtime condition escapes as an exception, and a provider can be
        // written with a malformed header name (ModelRoutingOptions.EnsureValid rejects an empty one, but
        // not every write path runs it, and "x y" is invalid without being empty). TryAddWithoutValidation
        // is documented to answer false rather than throw for a name that is not a valid HTTP token - this
        // pins that, so the behavior is a tested guarantee rather than an assumption about the BCL.
        HttpRequestMessage? captured = null;
        var handler = new DelegatingHandlerStub(request =>
        {
            captured ??= request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(OpenAiBody) });
        });
        var scanner = new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var provider = new ProviderOptions
        {
            BaseUrl = "https://example.invalid",
            Headers =
            [
                new ProviderHeader { Name = "not a valid header name", Value = "sk-test" },
                new ProviderHeader { Name = "also invalid", Value = "x" }
            ]
        };

        var result = await scanner.ScanAsync(providerKey: "broken-headers", provider: provider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.OpenAiCompatible);
        Assert.NotNull(captured);

        // Enumerated rather than asked via Headers.Contains(name): that method validates the name it is
        // given and throws FormatException for a malformed one, so probing with the bad name would test
        // HttpHeaders rather than the scanner.
        Assert.DoesNotContain(collection: captured!.Headers, filter: header =>
            header.Key.Contains(' ', comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInvalidHeaderName_IsNamedInTheScanError_RatherThanLookingLikeABadCredential()
    {
        // The failure this prevents is a misdiagnosis, not a crash: the auth header is silently dropped, the
        // provider answers 401 because the request arrived unauthenticated, and an operator reading
        // "returned 401" goes hunting for a wrong API key when the actual fault is the header name.
        var handler = new DelegatingHandlerStub(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var scanner = new ProviderEndpointScanner(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var provider = new ProviderOptions
        {
            BaseUrl = "https://example.invalid",
            Headers = [new ProviderHeader { Name = "not a valid header name", Value = "sk-test" }]
        };

        var result = await scanner.ScanAsync(providerKey: "broken-headers", provider: provider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expectedSubstring: "not a valid header name", actualString: result.ScanError);
        Assert.Contains(expectedSubstring: "not valid HTTP header names", actualString: result.ScanError);
        // The value is a secret and must not be echoed alongside the name.
        Assert.DoesNotContain(expectedSubstring: "sk-test", actualString: result.ScanError,
            comparisonType: StringComparison.Ordinal);
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
}