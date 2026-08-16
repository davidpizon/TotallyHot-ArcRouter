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
[Trait("Category", "Integration")]
public class OnnxTextGenerationClientTests
{
    private const string SkipReason =
        "llm_router model artifacts are not cached locally - run the application once with network " +
        "access to let OnnxTextGenerationClient download them, or place them manually at " +
        "LlmRouterOptions.ModelCacheDirectory.";

    private static LlmRouterOptions CreateOptions() => new();

    /// <summary>
    /// Reports whether every artifact <see cref="OnnxTextGenerationClient"/> needs is already on disk.
    /// <c>model.onnx.data</c> is checked too: the default export keeps its weights external, so a cache
    /// holding only the graph loads no faster than an empty one - it throws. Skipping on a partial cache
    /// keeps that failure out of the suite as a skip rather than a red test.
    /// </summary>
    private static bool ModelIsCached(LlmRouterOptions options)
    {
        var cacheDirectory = options.ResolveModelCacheDirectory();
        return File.Exists(Path.Combine(cacheDirectory, "genai_config.json")) &&
            File.Exists(Path.Combine(cacheDirectory, "tokenizer.json")) &&
            File.Exists(Path.Combine(cacheDirectory, "model.onnx")) &&
            (options.ModelOnnxDataUrl is null || File.Exists(Path.Combine(cacheDirectory, "model.onnx.data")));
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
        Assert.SkipUnless(ModelIsCached(options), SkipReason);

        await using var client = new OnnxTextGenerationClient(
            Options.Create(options),
            new ThrowingHttpClientFactory(),
            NullLogger<OnnxTextGenerationClient>.Instance);

        var prompt =
            "<|im_start|>system\nYou are a coding task router. Candidate models: model-a, model-b. " +
            "Respond with JSON: {\"model\": \"...\", \"reasoning\": \"...\"}.<|im_end|>\n" +
            "<|im_start|>user\nDimension: bug_fixing\nTask: Fix the null reference exception in Foo.cs.<|im_end|>\n" +
            "<|im_start|>assistant\n";

        var response = await client.GenerateAsync(prompt, TestContext.Current.CancellationToken);

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
            [
                options.GenAiConfigUrl,
                options.TokenizerJsonUrl,
                options.TokenizerConfigJsonUrl,
                options.ModelOnnxUrl,
                options.ModelOnnxDataUrl,
            ],
            url => Assert.StartsWith(variantPrefix, url, StringComparison.Ordinal));
    }

    /// <summary>
    /// Fails loudly if a test reaches the network - every artifact should already be present on disk
    /// whenever <see cref="GenerateAsync_OnCachedModel_ProducesNonEmptyText"/> actually runs (it
    /// self-skips otherwise), so a download attempt here indicates the cache check above is wrong.
    /// </summary>
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Unexpected network access: llm_router model artifacts should already be cached.");
    }
}
