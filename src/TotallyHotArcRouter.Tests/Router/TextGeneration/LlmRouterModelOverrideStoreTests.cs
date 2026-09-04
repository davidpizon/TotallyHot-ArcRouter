using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.TextGeneration;

namespace TotallyHot.ArcRouter.Tests.Router.TextGeneration;

/// <summary>
/// Covers <see cref="LlmRouterModelOverrideStore"/>: first-run seed validation (including rejecting
/// mismatched artifact-URL prefixes), seed-in-memory-until-edit behavior, validated persistence, version
/// bumps, and cache-directory slug stability/distinctness.
/// </summary>
public sealed class LlmRouterModelOverrideStoreTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(path1: Path.GetTempPath(), path2: $"llm-router-override-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private static LlmRouterOptions DefaultOptions()
    {
        return new LlmRouterOptions();
    }

    private LlmRouterModelOverrideStore CreateStore(LlmRouterOptions seed)
    {
        return new LlmRouterModelOverrideStore(
            logger: Mock.Of<ILogger<LlmRouterModelOverrideStore>>(),
            seed: Options.Create(seed),
            options: Options.Create(new LlmRouterModelOverrideStoreOptions { FilePath = _tempPath }));
    }

    [Fact]
    public void Constructor_NoFile_SeedsFromOptionsInMemory_WithoutWritingFile()
    {
        var store = CreateStore(DefaultOptions());

        Assert.Equal(0, actual: store.Snapshot.Version);
        Assert.Equal(
            expected:
            "https://huggingface.co/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
            actual: store.Snapshot.Override.BaseUrl);
        // Seeding must not touch disk: an installation that never switches models leaves no file behind.
        Assert.False(File.Exists(_tempPath));
    }

    [Fact]
    public void Constructor_MismatchedArtifactPrefix_ThrowsInvalidOperationException()
    {
        var seed = new LlmRouterOptions
        {
            ModelOnnxUrl = "https://huggingface.co/some-other-org/some-other-repo/resolve/main/model.onnx"
        };

        Assert.Throws<InvalidOperationException>(() => CreateStore(seed));
    }

    [Fact]
    public void Constructor_SeedUrlsCarryQueryString_SeedsSuccessfully()
    {
        const string folder =
            "https://huggingface.co/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";
        var seed = new LlmRouterOptions
        {
            GenAiConfigUrl = $"{folder}/genai_config.json?download=true",
            TokenizerJsonUrl = $"{folder}/tokenizer.json?download=true",
            TokenizerConfigJsonUrl = $"{folder}/tokenizer_config.json?download=true",
            ModelOnnxUrl = $"{folder}/model.onnx?download=true",
            ModelOnnxDataUrl = $"{folder}/model.onnx.data?download=true"
        };

        var store = CreateStore(seed);

        Assert.Equal(expected: folder, actual: store.Snapshot.Override.BaseUrl);
    }

    [Fact]
    public void Constructor_SeedUrlsDifferOnlyByHostCasingAndDefaultPort_SeedsSuccessfully()
    {
        const string folder =
            "https://huggingface.co/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";
        const string differentlyCasedFolder =
            "https://HuggingFace.co:443/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";
        var seed = new LlmRouterOptions
        {
            GenAiConfigUrl = $"{differentlyCasedFolder}/genai_config.json",
            TokenizerJsonUrl = $"{folder}/tokenizer.json",
            TokenizerConfigJsonUrl = $"{folder}/tokenizer_config.json",
            ModelOnnxUrl = $"{folder}/model.onnx",
            ModelOnnxDataUrl = $"{folder}/model.onnx.data"
        };

        var store = CreateStore(seed);

        Assert.Equal(expected: folder, actual: store.Snapshot.Override.BaseUrl);
    }

    [Fact]
    public async Task SetBaseUrlAsync_PersistsAndBumpsVersion()
    {
        var store = CreateStore(DefaultOptions());
        const string newBaseUrl = "https://huggingface.co/some-org/some-model/resolve/main/subfolder";

        await store.SetBaseUrlAsync(baseUrl: newBaseUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: newBaseUrl, actual: store.Snapshot.Override.BaseUrl);
        Assert.Equal(1, actual: store.Snapshot.Version);
        Assert.True(File.Exists(_tempPath));
    }

    [Fact]
    public async Task SetBaseUrlAsync_RaisesChanged()
    {
        var store = CreateStore(DefaultOptions());
        var raised = false;
        store.Changed += () => raised = true;

        await store.SetBaseUrlAsync(baseUrl: "https://huggingface.co/some-org/some-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(raised);
    }

    [Fact]
    public async Task SetBaseUrlAsync_NotAbsoluteUri_Throws()
    {
        var store = CreateStore(DefaultOptions());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SetBaseUrlAsync(baseUrl: "not-a-url", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_NonHttpScheme_Throws()
    {
        var store = CreateStore(DefaultOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetBaseUrlAsync(baseUrl: "file:///some/local/path",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_WithQueryString_Throws()
    {
        var store = CreateStore(DefaultOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetBaseUrlAsync(
            baseUrl: "https://huggingface.co/some-org/some-model/resolve/main/subfolder?download=true",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_WithFragment_Throws()
    {
        var store = CreateStore(DefaultOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetBaseUrlAsync(
            baseUrl: "https://huggingface.co/some-org/some-model/resolve/main/subfolder#section",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_PersistsAtomically_LeavesNoTemporaryFileBehind()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync(baseUrl: "https://huggingface.co/some-org/some-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_tempPath));
        Assert.False(File.Exists(_tempPath + ".tmp"));
    }

    [Fact]
    public async Task SetBaseUrlAsync_ThenReconstructed_LoadsPersistedOverride()
    {
        var store = CreateStore(DefaultOptions());
        const string newBaseUrl = "https://huggingface.co/some-org/some-model/resolve/main/subfolder";
        await store.SetBaseUrlAsync(baseUrl: newBaseUrl, cancellationToken: TestContext.Current.CancellationToken);

        var reloaded = CreateStore(DefaultOptions());

        Assert.Equal(expected: newBaseUrl, actual: reloaded.Snapshot.Override.BaseUrl);
    }

    [Fact]
    public async Task SetBaseUrlAsync_SameUrlTwice_ProducesSameSlug()
    {
        var store = CreateStore(DefaultOptions());
        const string baseUrl = "https://huggingface.co/some-org/some-model/resolve/main/subfolder";

        await store.SetBaseUrlAsync(baseUrl: baseUrl, cancellationToken: TestContext.Current.CancellationToken);
        var firstSlug = store.Snapshot.Override.CacheDirectorySlug;

        await store.SetBaseUrlAsync(baseUrl: "https://huggingface.co/other-org/other-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);
        await store.SetBaseUrlAsync(baseUrl: baseUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: firstSlug, actual: store.Snapshot.Override.CacheDirectorySlug);
    }

    [Fact]
    public async Task SetBaseUrlAsync_DifferentUrls_ProduceDifferentSlugs()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync(baseUrl: "https://huggingface.co/some-org/some-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);
        var firstSlug = store.Snapshot.Override.CacheDirectorySlug;

        await store.SetBaseUrlAsync(baseUrl: "https://huggingface.co/other-org/other-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);
        var secondSlug = store.Snapshot.Override.CacheDirectorySlug;

        Assert.NotEqual(expected: firstSlug, actual: secondSlug);
    }

    [Fact]
    public async Task SetBaseUrlAsync_UrlsDifferingOnlyByPathCasing_ProduceDifferentSlugs()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync(baseUrl: "https://huggingface.co/Some-Org/Some-Model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);
        var firstSlug = store.Snapshot.Override.CacheDirectorySlug;

        await store.SetBaseUrlAsync(baseUrl: "https://huggingface.co/some-org/some-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);
        var secondSlug = store.Snapshot.Override.CacheDirectorySlug;

        Assert.NotEqual(expected: firstSlug, actual: secondSlug);
    }

    [Fact]
    public async Task SetBaseUrlAsync_UrlsDifferingOnlyByHostCasing_ProduceSameSlug()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync(baseUrl: "https://HuggingFace.co/some-org/some-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);
        var firstSlug = store.Snapshot.Override.CacheDirectorySlug;

        await store.SetBaseUrlAsync(baseUrl: "https://huggingface.co/some-org/some-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);
        var secondSlug = store.Snapshot.Override.CacheDirectorySlug;

        Assert.Equal(expected: firstSlug, actual: secondSlug);
    }

    [Fact]
    public async Task SetBaseUrlAsync_HostCasingAndDefaultPort_NormalizesBaseUrl()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync(
            baseUrl: "https://HuggingFace.co:443/some-org/some-model/resolve/main/subfolder",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            expected: "https://huggingface.co/some-org/some-model/resolve/main/subfolder",
            actual: store.Snapshot.Override.BaseUrl);
    }

    [Fact]
    public async Task Constructor_FileHasTamperedSlug_RecomputesSlugFromBaseUrl_IgnoringPersistedValue()
    {
        const string baseUrl = "https://huggingface.co/some-org/some-model/resolve/main/subfolder";

        // Legitimately produced by SetBaseUrlAsync, so we know exactly what slug should come back.
        var reference = CreateStore(DefaultOptions());
        await reference.SetBaseUrlAsync(baseUrl: baseUrl, cancellationToken: TestContext.Current.CancellationToken);
        var expectedSlug = reference.Snapshot.Override.CacheDirectorySlug;

        // Now hand-tamper the persisted file with a path-traversal slug and reload.
        await File.WriteAllTextAsync(path: _tempPath,
            contents: $$"""{"BaseUrl":"{{baseUrl}}","CacheDirectorySlug":"../../evil"}""",
            cancellationToken: TestContext.Current.CancellationToken);
        var store = CreateStore(DefaultOptions());

        Assert.Equal(expected: baseUrl, actual: store.Snapshot.Override.BaseUrl);
        Assert.Equal(expected: expectedSlug, actual: store.Snapshot.Override.CacheDirectorySlug);
    }
}