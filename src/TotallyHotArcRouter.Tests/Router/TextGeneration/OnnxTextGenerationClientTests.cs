using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.TextGeneration;

namespace TotallyHot.ArcRouter.Tests.Router.TextGeneration;

/// <summary>
/// Exercises <see cref="OnnxTextGenerationClient"/> against the real, locally-cached llm_router model
/// artifacts - PLAN.md Phase L. Skips itself via <see cref="Assert.SkipUnless"/> when the model isn't
/// already cached at <see cref="LlmRouterOptions.ModelCacheDirectory"/>, the same self-skip convention
/// <c>CodeRouterBenchTable10ReconciliationTests</c>/<c>LogRegTrainerReconciliationTests</c> use for a
/// network/artifact-gated precondition that is an expected, non-broken state in CI and on most
/// contributors' machines - a multi-hundred-megabyte model download has no place running unattended in
/// a unit test suite.
/// </summary>
[Trait(name: "Category", value: "Integration")]
public class OnnxTextGenerationClientTests
{
    private const string SkipReason =
        "llm_router model artifacts are not cached locally - run the application once with network " +
        "access to let OnnxTextGenerationClient download them, or place them manually at " +
        "LlmRouterOptions.ModelCacheDirectory.";

    private static LlmRouterOptions CreateOptions()
    {
        return new LlmRouterOptions();
    }

    /// <summary>
    /// Builds an override store seeded from <paramref name="options"/>, backed by a temp file path that
    /// is never actually written (nothing calls <see cref="ILlmRouterModelOverrideStore.SetBaseUrlAsync"/>
    /// in these tests), so each test seeds fresh from <paramref name="options"/> rather than picking up a
    /// leftover override from a previous run.
    /// </summary>
    private static LlmRouterModelOverrideStore CreateOverrideStore(LlmRouterOptions options)
    {
        return new LlmRouterModelOverrideStore(
            logger: NullLogger<LlmRouterModelOverrideStore>.Instance,
            seed: Options.Create(options),
            options: Options.Create(new LlmRouterModelOverrideStoreOptions
            {
                FilePath = Path.Combine(path1: Path.GetTempPath(),
                    path2: $"llm-router-override-test-{Guid.NewGuid():N}.json")
            }));
    }

    /// <summary>
    /// Reports whether every required artifact <see cref="OnnxTextGenerationClient"/> needs is already on
    /// disk, in <paramref name="overrideStore"/>'s active model's cache directory. Skipping on a partial
    /// cache keeps a load failure out of the suite as a skip rather than a red test. A missing
    /// <see cref="LlmRouterModelFiles.IsOptional"/> file doesn't count as partial - inlined-weight exports
    /// legitimately never have model.onnx.data.
    /// </summary>
    private static bool ModelIsCached(ILlmRouterModelOverrideStore overrideStore)
    {
        var cacheDirectory = overrideStore.Snapshot.Override.ResolveCacheDirectory();
        return LlmRouterModelFiles.All.All(fileName =>
            File.Exists(Path.Combine(path1: cacheDirectory, path2: fileName)) ||
            LlmRouterModelFiles.IsOptional(fileName));
    }

    /// <summary>
    /// The exact reproduction recipe for validating a locally-cached llm_router model: generate a
    /// completion for a minimal routing-shaped prompt and confirm it comes back as non-empty text. Run
    /// it locally (<c>dotnet test --filter GenerateAsync_OnCachedModel_ProducesNonEmptyText</c>) after
    /// the application has downloaded the model once.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_OnCachedModel_ProducesNonEmptyText()
    {
        var options = CreateOptions();
        using var overrideStore = CreateOverrideStore(options);
        Assert.SkipUnless(condition: ModelIsCached(overrideStore), reason: SkipReason);

        await using var client = new OnnxTextGenerationClient(
            options: Options.Create(options),
            overrideStore: overrideStore,
            httpClientFactory: new ThrowingHttpClientFactory(),
            logger: NullLogger<OnnxTextGenerationClient>.Instance);

        var prompt =
            "<|im_start|>system\nYou are a coding task router. Candidate models: model-a, model-b. " +
            "Respond with JSON: {\"model\": \"...\", \"reasoning\": \"...\"}.<|im_end|>\n" +
            "<|im_start|>user\nDimension: bug_fixing\nTask: Fix the null reference exception in Foo.cs.<|im_end|>\n" +
            "<|im_start|>assistant\n";

        var response =
            await client.GenerateAsync(prompt: prompt, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(response));
    }

    [Fact]
    public void EnsureValid_DefaultOptions_DoesNotThrow()
    {
        var options = CreateOptions();

        var exception = Record.Exception(options.EnsureValid);

        Assert.Null(exception);
    }

    /// <summary>
    /// Guards the shape of the default artifact URLs, which is easy to get wrong by hand: the upstream
    /// repository hosts no files at its root, so a root-relative URL 404s and the voter abstains forever
    /// with only a log line to show for it. Every URL must name the same execution-provider subfolder -
    /// mixing builds loads a graph whose weights and config belong to a different quantization - and
    /// <see cref="LlmRouterOptions.ModelOnnxDataUrl"/> must be set, since this export's weights are all
    /// external to <c>model.onnx</c>.
    /// </summary>
    [Fact]
    public void DefaultArtifactUrls_AllNameTheSameExecutionProviderSubfolder()
    {
        var options = CreateOptions();
        const string variantPrefix =
            "https://huggingface.co/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/" +
            "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/";

        Assert.NotNull(options.ModelOnnxDataUrl);
        Assert.All(
            collection:
            [
                options.GenAiConfigUrl,
                options.TokenizerJsonUrl,
                options.TokenizerConfigJsonUrl,
                options.ModelOnnxUrl,
                options.ModelOnnxDataUrl
            ],
            action: url => Assert.StartsWith(expectedStartString: variantPrefix, actualString: url,
                comparisonType: StringComparison.Ordinal));
    }

    /// <summary>
    /// Guards that <see cref="OnnxTextGenerationClient"/> derives its download URLs and cache directory
    /// from the injected <see cref="ILlmRouterModelOverrideStore"/> rather than reading
    /// <see cref="LlmRouterOptions"/>'s own URL properties directly - the switch requirement 4's
    /// minimal-disruption design made. Deliberately points the override at a base URL that answers 404
    /// (no artifact is actually downloaded), so this exercises the URL construction path without a real
    /// network dependency and without needing a cached model on disk.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_UsesOverrideStoreBaseUrl_NotOptionsUrls()
    {
        var options = CreateOptions();
        const string overrideBaseUrl = "https://example.invalid/custom-model";
        var slug = $"test-{Guid.NewGuid():N}";
        var fakeStore =
            new FakeLlmRouterModelOverrideStore(new LlmRouterModelOverride(BaseUrl: overrideBaseUrl,
                CacheDirectorySlug: slug));
        var capturingFactory = new CapturingHttpClientFactory();

        try
        {
            await using var client = new OnnxTextGenerationClient(
                options: Options.Create(options),
                overrideStore: fakeStore,
                httpClientFactory: capturingFactory,
                logger: NullLogger<OnnxTextGenerationClient>.Instance);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GenerateAsync(prompt: "prompt", cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(expected: $"{overrideBaseUrl}/genai_config.json",
                actual: capturingFactory.RequestedUrls.Single());
        }
        finally
        {
            var cacheDirectory = fakeStore.Snapshot.Override.ResolveCacheDirectory();
            if (Directory.Exists(cacheDirectory)) Directory.Delete(path: cacheDirectory, true);
        }
    }

    /// <summary>
    /// Fails loudly if a test reaches the network - every artifact should already be present on disk
    /// whenever <see cref="GenerateAsync_OnCachedModel_ProducesNonEmptyText"/> actually runs (it
    /// self-skips otherwise), so a download attempt here indicates the cache check above is wrong.
    /// </summary>
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            throw new InvalidOperationException(
                "Unexpected network access: llm_router model artifacts should already be cached.");
        }
    }

    /// <summary>Records every requested URL and answers 404, so a caller sees a real HTTP failure without a real network call.</summary>
    private sealed class CapturingHttpClientFactory : IHttpClientFactory
    {
        public List<string> RequestedUrls { get; } = [];

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new CapturingHandler(RequestedUrls));
        }

        private sealed class CapturingHandler(List<string> requestedUrls) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                requestedUrls.Add(request.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}