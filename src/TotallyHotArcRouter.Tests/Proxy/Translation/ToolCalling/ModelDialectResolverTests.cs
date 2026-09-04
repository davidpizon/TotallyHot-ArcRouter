using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Coverage for tiers 1-3 of model dialect detection (<c>docs/router/tool-call-normalization.md</c> §3.2,
/// Phase 3). Every probe is stubbed - no live provider.
/// <para>
/// Two of the cases below encode judgment calls rather than mechanics, and they are the ones worth
/// protecting. <see cref="ATemplateThatMatchesNothing_IsConclusive_AndDoesNotFallBackToTheModelId"/> pins
/// that ground truth beats a filename even when the ground truth says "none of the above", and
/// <see cref="AGenericLlamaArchitecture_FallsThroughToTheModelId"/> pins that the architecture read is
/// deliberately conservative, because <c>llama</c> is reported by a huge population of fine-tunes whose
/// templates are not Llama's.
/// </para>
/// </summary>
public sealed class ModelDialectResolverTests
{
    // Condensed from the real Ollama templates for these models: the tool-call framing is reproduced
    // verbatim and the surrounding message-loop boilerplate is dropped, since only the framing is read.
    private const string QwenTemplate = """
                                        {{- if .Tools }}
                                        You are provided with function signatures within <tools></tools> XML tags:
                                        <tools>
                                        {{- range .Tools }}
                                        {"type": "function", "function": {{ .Function }}}
                                        {{- end }}
                                        </tools>

                                        For each function call, return a json object within <tool_call></tool_call> XML tags:
                                        <tool_call>
                                        {"name": <function-name>, "arguments": <args-json-object>}
                                        </tool_call>
                                        {{- end }}
                                        """;

    private const string Llama3Template = """
                                          {{- if .Tools }}
                                          Given the following functions, respond with a JSON for a function call.
                                          {{- end }}
                                          {{- range .ToolCalls }}<|python_tag|>{"name": "{{ .Function.Name }}", "parameters": {{ .Function.Arguments }}}{{ end }}
                                          """;

    private const string MistralTemplate = """
                                           {{- if .Tools }}[AVAILABLE_TOOLS] {{ .Tools }}[/AVAILABLE_TOOLS]{{ end }}
                                           {{- if .ToolCalls }}[TOOL_CALLS] [{{ range .ToolCalls }}{"name": "{{ .Function.Name }}", "arguments": {{ .Function.Arguments }}}{{ end }}]{{ end }}
                                           """;

    // A template that plainly supports tools but frames them in no dialect the registry knows - the shape
    // an unregistered family (DeepSeek today) presents.
    private const string UnknownDialectTemplate = """
                                                  {{- if .Tools }}<|tool▁calls▁begin|>{{ .Tools }}<|tool▁calls▁end|>{{ end }}
                                                  """;

    // A template with no tool support whatsoever - no dialect framing and no .Tools/.ToolCalls to render
    // schemas or calls with. This is the shape that selects emulation (Phase 5).
    private const string NoToolSupportTemplate = """
                                                 {{- range .Messages }}<|im_start|>{{ .Role }}
                                                 {{ .Content }}<|im_end|>
                                                 {{ end }}<|im_start|>assistant
                                                 """;

    private static ProviderOptions Provider(string baseUrl = "http://localhost:11434/v1")
    {
        return new ProviderOptions { BaseUrl = baseUrl };
    }

    private static ProviderEndpointCapabilities Flavors(bool ollama = false, bool lmStudio = false)
    {
        return new ProviderEndpointCapabilities(ProviderKey: "local", true, LmStudioNative: lmStudio,
            OllamaNative: ollama,
            false, JsonSchemaResponseFormat: lmStudio || ollama,
            ScannedAtUtc: DateTimeOffset.UtcNow);
    }

    private static string ShowBody(string template)
    {
        return JsonSerializer.Serialize(new { template });
    }

    private static string LmStudioBody(params (string Id, string Arch)[] models)
    {
        return JsonSerializer.Serialize(new { data = models.Select(m => new { id = m.Id, arch = m.Arch }) });
    }

    /// <summary>Answers 200 with the body whose path fragment matches, 404 otherwise.</summary>
    private static ModelDialectResolver ResolverFor(
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

        return new ModelDialectResolver(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());
    }

    // ----- Tier 1: the literal chat template -----

    [Theory]
    [InlineData("qwen2.5-coder:7b", "hermes", "<tool_call>")]
    [InlineData("llama3.1:8b", "llama3-json", "<|python_tag|>")]
    [InlineData("mistral-nemo:latest", "mistral", "[TOOL_CALLS]")]
    public async Task ATemplateRead_ClassifiesTheModel_AtTemplateConfidence(
        string modelId, string expectedDialect, string expectedDelimiter)
    {
        var template = expectedDialect switch
        {
            "hermes" => QwenTemplate,
            "llama3-json" => Llama3Template,
            _ => MistralTemplate
        };
        var resolver = ResolverFor(new Dictionary<string, string> { ["/api/show"] = ShowBody(template) });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true), modelName: modelId,
            providerModelId: modelId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Capability);
        Assert.Equal(expected: expectedDialect, actual: result.Capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Template, actual: result.Capability!.Confidence);

        // The evidence names the delimiter that matched, never any of the template text around it.
        Assert.Contains(expectedSubstring: expectedDelimiter, actualString: result.Capability!.Evidence,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemplateThatMatchesNothing_IsConclusive_AndDoesNotFallBackToTheModelId()
    {
        // The model id says "qwen", which tier 3 would happily read as hermes. The template - which is what
        // actually decides the model's reply syntax - says otherwise, so recording nothing is correct and
        // recording hermes would arm a scanner that can never match.
        var resolver = ResolverFor(new Dictionary<string, string> { ["/api/show"] = ShowBody(UnknownDialectTemplate) });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "deepseek-qwen-distill", providerModelId: "deepseek-qwen-distill",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Capability);
    }

    [Fact]
    public async Task ATemplateThatRendersNoToolsAtAll_SelectsEmulation()
    {
        // The Phase 5 selection signal. Unlike the unmatched-response counter Phase 4 rejected, this is not
        // an inference from behavior: the chat template is the mechanism that would render a tool schema,
        // read directly off the model, and it has no path by which one could reach the weights.
        var resolver = ResolverFor(new Dictionary<string, string> { ["/api/show"] = ShowBody(NoToolSupportTemplate) });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "tinyllama:1.1b", providerModelId: "tinyllama:1.1b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "emulated", actual: result.Capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Template, actual: result.Capability!.Confidence);
    }

    [Fact]
    public async Task ATemplateWithToolsInAnUnknownDialect_IsNotCondemnedToEmulation()
    {
        // The guard that keeps "no dialect matched" from being read as "has no tool calling". A DeepSeek
        // template supports tools perfectly well in framing this build has not registered; emulating it
        // would strip the native tool support it actually has. Recording nothing leaves it to tier 4.
        var resolver = ResolverFor(new Dictionary<string, string> { ["/api/show"] = ShowBody(UnknownDialectTemplate) });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "deepseek-r1:7b", providerModelId: "deepseek-r1:7b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Capability);
    }

    [Fact]
    public async Task WhenOllamaCannotAnswerForTheModel_TheModelIdHeuristicStillApplies()
    {
        // A 404 from /api/show is "no template was read", which is a different thing from a template that
        // matched nothing - so the lower tiers must still run.
        var resolver = ResolverFor(new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "qwen2.5-coder", providerModelId: "qwen2.5-coder",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Capability);
        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Heuristic, actual: result.Capability!.Confidence);
    }

    // ----- Tier 2: the architecture in the model file's metadata -----

    [Fact]
    public async Task AnLmStudioArchitecture_ClassifiesTheModel_AtTemplateConfidence()
    {
        // The model id here is a bare alias that tells tier 3 nothing; arch is the only source that can
        // answer, which is the case this tier exists for.
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/v0/models"] = LmStudioBody(("local-model-a", "qwen2"))
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider("http://localhost:1234/v1"),
            endpointCapabilities: Flavors(lmStudio: true), modelName: "local-model-a", providerModelId: "local-model-a",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Capability);
        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Template, actual: result.Capability!.Confidence);
        Assert.Contains(expectedSubstring: "qwen2", actualString: result.Capability!.Evidence,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGenericLlamaArchitecture_FallsThroughToTheModelId()
    {
        // `llama` is reported by Llama 2, Llama 3, and every fine-tune built on them - including Hermes,
        // whose template is not Llama's. Mapping it would produce a Template-confidence row that is wrong
        // for a large share of the models carrying it, *and* outrank the tier-3 read that gets this right.
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/v0/models"] = LmStudioBody(("hermes-3-llama-3.1-8b", "llama"))
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider("http://localhost:1234/v1"),
            endpointCapabilities: Flavors(lmStudio: true),
            modelName: "hermes-3-llama-3.1-8b", providerModelId: "hermes-3-llama-3.1-8b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Capability);
        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Heuristic, actual: result.Capability!.Confidence);
    }

    [Fact]
    public async Task AModelMissingFromTheLmStudioList_FallsThrough()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/v0/models"] = LmStudioBody(("some-other-model", "qwen2"))
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider("http://localhost:1234/v1"),
            endpointCapabilities: Flavors(lmStudio: true), modelName: "mistral-7b", providerModelId: "mistral-7b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Capability);
        Assert.Equal(expected: "mistral", actual: result.Capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Heuristic, actual: result.Capability!.Confidence);
    }

    // ----- Tier 3: the model id -----

    [Theory]
    [InlineData("qwen2.5-coder-7b-instruct", "hermes")]
    [InlineData("Hermes-2-Pro-Mistral-7B", "hermes")]
    [InlineData("mixtral-8x7b-instruct", "mistral")]
    [InlineData("Meta-Llama-3.1-8B-Instruct", "llama3-json")]
    [InlineData("llama3.2:3b", "llama3-json")]
    public async Task TheModelId_ClassifiesKnownFamilies(string modelId, string expected)
    {
        var resolver = ResolverFor(new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), null, modelName: modelId, providerModelId: modelId,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Capability);
        Assert.Equal(expected: expected, actual: result.Capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Heuristic, actual: result.Capability!.Confidence);
    }

    [Fact]
    public async Task AHermesFineTuneIsAttributedToHermes_NotToTheBaseItNames()
    {
        // "Hermes-2-Pro-Mistral-7B" contains both tokens. Hermes ships its own template regardless of base,
        // so matching the base would get the best-known case exactly backwards - which is why the heuristic
        // table's order is load-bearing rather than incidental.
        var resolver = ResolverFor(new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), null, modelName: "Hermes-2-Pro-Mistral-7B",
            providerModelId: "Hermes-2-Pro-Mistral-7B",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
    }

    [Theory]
    [InlineData("gpt-5.4")]
    [InlineData("claude-sonnet-5")]
    [InlineData("llama-2-13b-chat")]
    public async Task AnUnrecognizedModel_RecordsNothing(string modelId)
    {
        // Llama 2 is in this list on purpose: it has no tool-call template at all, so matching it on the
        // "llama" substring would arm a scanner against a model that can never satisfy it.
        var resolver = ResolverFor(new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), null, modelName: modelId, providerModelId: modelId,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Capability);
    }

    [Fact]
    public async Task TheClientFacingAliasIsConsulted_WhenTheUpstreamIdSaysNothing()
    {
        var resolver = ResolverFor(new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), null,
            modelName: "qwen-local", providerModelId: "model-a",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
        Assert.Equal(expected: "qwen-local", actual: result.Capability!.ModelName);
    }

    // ----- Probing discipline -----

    [Fact]
    public async Task NoNativeProbeIsIssued_ForAProviderKnownNotToAnswerOne()
    {
        // A cloud provider answers neither native path. Probing them anyway would add two guaranteed-404
        // round trips to every model added, which is exactly what the Phase 2 flags exist to prevent.
        var urls = new List<string>();
        var resolver = ResolverFor(okPaths: new Dictionary<string, string>(), recordedUrls: urls);

        await resolver.ResolveAsync(
            providerKey: "openai", provider: Provider("https://api.openai.com/v1"), endpointCapabilities: Flavors(),
            modelName: "qwen-hosted", providerModelId: "qwen-hosted",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(urls);
    }

    [Fact]
    public async Task NoNativeProbeIsIssued_WhenTheProviderHasNeverBeenScanned()
    {
        var urls = new List<string>();
        var resolver = ResolverFor(okPaths: new Dictionary<string, string>(), recordedUrls: urls);

        await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), null, modelName: "qwen2.5", providerModelId: "qwen2.5",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(urls);
    }

    [Fact]
    public async Task TheNativeProbesTargetTheHostRoot_NotTheV1Base()
    {
        // The same trap ProviderEndpointScanner documents: LM Studio and Ollama are configured with a /v1
        // base because that is where their OpenAI-compatible routes live, but their native APIs sit at the
        // root. Probing http://localhost:11434/v1/api/show would 404 on every install.
        var urls = new List<string>();
        var resolver = ResolverFor(okPaths: new Dictionary<string, string>(), recordedUrls: urls);

        await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(true, true),
            modelName: "some-model", providerModelId: "some-model",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expected: "http://localhost:11434/api/show", collection: urls);
        Assert.Contains(expected: "http://localhost:11434/api/v0/models", collection: urls);
        Assert.DoesNotContain(collection: urls,
            filter: url => url.Contains(value: "/v1/api/", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOllamaProbeAsksAboutTheUpstreamId_NotTheClientFacingAlias()
    {
        // Ollama has never heard of the operator's alias; asking with it 404s and silently costs tier 1.
        string? requestBody = null;
        var handler = new DelegatingHandlerStub(async request =>
        {
            requestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ShowBody(QwenTemplate)) };
        });
        var resolver = new ModelDialectResolver(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "fast-coder", providerModelId: "qwen2.5-coder:7b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(requestBody);
        Assert.Contains(expectedSubstring: "qwen2.5-coder:7b", actualString: requestBody,
            comparisonType: StringComparison.Ordinal);
    }

    // ----- Failure is never an exception -----

    [Fact]
    public async Task AnUnreachableProvider_ResolvesToTheHeuristic_RatherThanThrowing()
    {
        var handler = new DelegatingHandlerStub(_ => throw new HttpRequestException("connection refused"));
        var resolver = new ModelDialectResolver(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(true, true),
            modelName: "qwen2.5-coder", providerModelId: "qwen2.5-coder",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
    }

    [Fact]
    public async Task AMalformedBody_DoesNotThrow()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/show"] = "<html>not json at all</html>",
            ["/api/v0/models"] = "{ truncated"
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(true, true),
            modelName: "unknown-model", providerModelId: "unknown-model",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Capability);
    }

    [Fact]
    public async Task AnOversizedShowResponse_IsAbandoned_RatherThanBuffered()
    {
        // /api/show returns the model's whole Modelfile, license text included, and only the template is
        // wanted from it. A provider answering with something far larger than any real template - or a
        // misrouted endpoint streaming something else entirely - must not be read into memory whole. Sent
        // without a Content-Length so this also pins that the cap is enforced on the stream rather than on a
        // header the sender can simply omit.
        var oversized = new string('x', count: 2 * 1024 * 1024);
        var handler = new DelegatingHandlerStub(_ =>
        {
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { template = oversized + "<tool_call>" }))));
            content.Headers.ContentLength = null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var resolver = new ModelDialectResolver(httpClient: new HttpClient(handler),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        // The body does contain a hermes delimiter, so a resolver that read it whole would answer hermes at
        // Template confidence. Falling back to the model id is the proof it never read that far.
        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "mixtral-8x7b", providerModelId: "mixtral-8x7b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "mistral", actual: result.Capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Heuristic, actual: result.Capability!.Confidence);
    }

    [Fact]
    public async Task AnInvalidBaseUrl_DoesNotThrow()
    {
        var resolver = ResolverFor(new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider("not a url"), endpointCapabilities: Flavors(true, true),
            modelName: "qwen2.5", providerModelId: "qwen2.5",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
    }

    // ----- Argument guards -----

    [Fact]
    public async Task ABlankProviderKeyOrModelName_IsAProgrammingError()
    {
        var resolver = ResolverFor(new Dictionary<string, string>());

        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(
            providerKey: " ", provider: Provider(), null, modelName: "qwen", providerModelId: "qwen",
            cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), null, modelName: " ", providerModelId: "qwen",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // ----- Context windows: the exits that used to discard the whole probe -----

    private static string ShowBodyWithModelInfo(string? template, string architecture, long contextLength)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["template"] = template,
            ["model_info"] = new Dictionary<string, object>
            {
                ["general.architecture"] = architecture,
                [$"{architecture}.context_length"] = contextLength
            }
        });
    }

    [Fact]
    public async Task ATemplateRead_AlsoRecordsTheContextLength()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/show"] = ShowBodyWithModelInfo(template: QwenTemplate, architecture: "qwen2", 32768)
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "qwen2.5-coder:7b", providerModelId: "qwen2.5-coder:7b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
        Assert.Equal(32768, actual: result.ContextWindow!.ContextLength);
        Assert.Equal(expected: "qwen2", actual: result.ContextWindow!.Architecture);
    }

    // Exit B: the template rendered tools in a dialect this build does not know, so no capability is
    // recorded. This path previously discarded the entire probe - including the most authoritative context
    // reading obtainable, since the template was read successfully.
    [Fact]
    public async Task ATemplateThatMatchesNothing_StillRecordsTheContextLength()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/show"] = ShowBodyWithModelInfo(template: UnknownDialectTemplate, architecture: "deepseek2", 65536)
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "deepseek-qwen-distill", providerModelId: "deepseek-qwen-distill",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Capability);
        Assert.Equal(65536, actual: result.ContextWindow!.ContextLength);
    }

    // Exit F: no tier could classify anything, but the probe still read a window.
    [Fact]
    public async Task AModelIdHeuristicMiss_StillRecordsTheContextLength()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/show"] = ShowBodyWithModelInfo(null, architecture: "phi3", 4096)
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "an-unrecognizable-model", providerModelId: "an-unrecognizable-model",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Capability);
        Assert.Equal(4096, actual: result.ContextWindow!.ContextLength);
    }

    [Fact]
    public async Task AShowResponseWithoutModelInfo_RecordsNoContextWindow()
    {
        var resolver = ResolverFor(new Dictionary<string, string> { ["/api/show"] = ShowBody(QwenTemplate) });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "qwen2.5-coder:7b", providerModelId: "qwen2.5-coder:7b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Capability);
        Assert.Null(result.ContextWindow);
    }

    // The suffix-scan fallback: no general.architecture to indirect through, so the key itself names it.
    [Fact]
    public async Task AShowResponseWithoutAGeneralArchitectureKey_StillFindsTheContextLength()
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["template"] = QwenTemplate,
            ["model_info"] = new Dictionary<string, object> { ["qwen2.context_length"] = 16384 }
        });
        var resolver = ResolverFor(new Dictionary<string, string> { ["/api/show"] = body });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "qwen2.5-coder:7b", providerModelId: "qwen2.5-coder:7b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(16384, actual: result.ContextWindow!.ContextLength);
        Assert.Equal(expected: "qwen2", actual: result.ContextWindow!.Architecture);
    }

    // A value no int can represent must read as unknown rather than throw inside a best-effort probe.
    [Fact]
    public async Task AContextLengthTooLargeForInt32_IsIgnored_RatherThanThrowing()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/show"] = ShowBodyWithModelInfo(template: QwenTemplate, architecture: "qwen2", 9_000_000_000L)
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(ollama: true),
            modelName: "qwen2.5-coder:7b", providerModelId: "qwen2.5-coder:7b",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "hermes", actual: result.Capability!.Dialect);
        Assert.Null(result.ContextWindow);
    }

    // ----- Tier 2: LM Studio -----

    private static string LmStudioBodyWithContext(
        string id, string arch, int? maxContext, int? loadedContext)
    {
        var entry = new Dictionary<string, object> { ["id"] = id, ["arch"] = arch };
        if (maxContext is not null) entry["max_context_length"] = maxContext.Value;

        if (loadedContext is not null) entry["loaded_context_length"] = loadedContext.Value;

        return JsonSerializer.Serialize(new { data = new[] { entry } });
    }

    // Verified against a live LM Studio: both fields are present on every entry. `loaded` is what the
    // runtime will actually accept, and over-reporting causes a hard upstream rejection.
    [Fact]
    public async Task LoadedContextLength_WinsOverMaxContextLength()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/v0/models"] = LmStudioBodyWithContext(id: "qwen-local", arch: "qwen2", 32768, 8192)
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(lmStudio: true),
            modelName: "qwen-local", providerModelId: "qwen-local",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8192, actual: result.ContextWindow!.ContextLength);
    }

    [Fact]
    public async Task MaxContextLength_IsUsed_WhenNoLoadedContextIsReported()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/v0/models"] = LmStudioBodyWithContext(id: "qwen-local", arch: "qwen2", 32768, null)
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(lmStudio: true),
            modelName: "qwen-local", providerModelId: "qwen-local",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(32768, actual: result.ContextWindow!.ContextLength);
    }

    // The tier-2 fall-through: an unmapped architecture moves on to tier 3 for the *dialect*, but the
    // window read on the way past must survive. This path also previously dropped it.
    [Fact]
    public async Task AnUnmappedLmStudioArchitecture_StillRecordsTheContextLength()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            ["/api/v0/models"] = LmStudioBodyWithContext(id: "some-model", arch: "llama", 128000, null)
        });

        var result = await resolver.ResolveAsync(
            providerKey: "local", provider: Provider(), endpointCapabilities: Flavors(lmStudio: true),
            modelName: "some-model", providerModelId: "some-model",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(128000, actual: result.ContextWindow!.ContextLength);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}