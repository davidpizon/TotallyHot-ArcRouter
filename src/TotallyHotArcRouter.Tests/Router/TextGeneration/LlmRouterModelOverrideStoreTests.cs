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
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"llm-router-override-{Guid.NewGuid():N}.json");

    private static LlmRouterOptions DefaultOptions() => new();

    private LlmRouterModelOverrideStore CreateStore(LlmRouterOptions seed) => new(
        Mock.Of<Microsoft.Extensions.Logging.ILogger<LlmRouterModelOverrideStore>>(),
        Options.Create(seed),
        Options.Create(new LlmRouterModelOverrideStoreOptions { FilePath = _tempPath }));

    [Fact]
    public void Constructor_NoFile_SeedsFromOptionsInMemory_WithoutWritingFile()
    {
        var store = CreateStore(DefaultOptions());

        Assert.Equal(0, store.Snapshot.Version);
        Assert.Equal(
            "https://huggingface.co/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
            store.Snapshot.Override.BaseUrl);
        // Seeding must not touch disk: an installation that never switches models leaves no file behind.
        Assert.False(File.Exists(_tempPath));
    }

    [Fact]
    public void Constructor_MismatchedArtifactPrefix_ThrowsInvalidOperationException()
    {
        var seed = new LlmRouterOptions
        {
            ModelOnnxUrl = "https://huggingface.co/some-other-org/some-other-repo/resolve/main/model.onnx",
        };

        Assert.Throws<InvalidOperationException>(() => CreateStore(seed));
    }

    [Fact]
    public async Task SetBaseUrlAsync_PersistsAndBumpsVersion()
    {
        var store = CreateStore(DefaultOptions());
        const string newBaseUrl = "https://huggingface.co/some-org/some-model/resolve/main/subfolder";

        await store.SetBaseUrlAsync(newBaseUrl, TestContext.Current.CancellationToken);

        Assert.Equal(newBaseUrl, store.Snapshot.Override.BaseUrl);
        Assert.Equal(1, store.Snapshot.Version);
        Assert.True(File.Exists(_tempPath));
    }

    [Fact]
    public async Task SetBaseUrlAsync_RaisesChanged()
    {
        var store = CreateStore(DefaultOptions());
        var raised = false;
        store.Changed += () => raised = true;

        await store.SetBaseUrlAsync("https://huggingface.co/some-org/some-model/resolve/main/subfolder", TestContext.Current.CancellationToken);

        Assert.True(raised);
    }

    [Fact]
    public async Task SetBaseUrlAsync_NotAbsoluteUri_Throws()
    {
        var store = CreateStore(DefaultOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetBaseUrlAsync("not-a-url", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_NonHttpScheme_Throws()
    {
        var store = CreateStore(DefaultOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetBaseUrlAsync("file:///some/local/path", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_WithQueryString_Throws()
    {
        var store = CreateStore(DefaultOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetBaseUrlAsync(
                "https://huggingface.co/some-org/some-model/resolve/main/subfolder?download=true",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_WithFragment_Throws()
    {
        var store = CreateStore(DefaultOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetBaseUrlAsync(
                "https://huggingface.co/some-org/some-model/resolve/main/subfolder#section",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_PersistsAtomically_LeavesNoTemporaryFileBehind()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync("https://huggingface.co/some-org/some-model/resolve/main/subfolder", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_tempPath));
        Assert.False(File.Exists(_tempPath + ".tmp"));
    }

    [Fact]
    public async Task SetBaseUrlAsync_ThenReconstructed_LoadsPersistedOverride()
    {
        var store = CreateStore(DefaultOptions());
        const string newBaseUrl = "https://huggingface.co/some-org/some-model/resolve/main/subfolder";
        await store.SetBaseUrlAsync(newBaseUrl, TestContext.Current.CancellationToken);

        var reloaded = CreateStore(DefaultOptions());

        Assert.Equal(newBaseUrl, reloaded.Snapshot.Override.BaseUrl);
    }

    [Fact]
    public async Task SetBaseUrlAsync_SameUrlTwice_ProducesSameSlug()
    {
        var store = CreateStore(DefaultOptions());
        const string baseUrl = "https://huggingface.co/some-org/some-model/resolve/main/subfolder";

        await store.SetBaseUrlAsync(baseUrl, TestContext.Current.CancellationToken);
        var firstSlug = store.Snapshot.Override.CacheDirectorySlug;

        await store.SetBaseUrlAsync("https://huggingface.co/other-org/other-model/resolve/main/subfolder", TestContext.Current.CancellationToken);
        await store.SetBaseUrlAsync(baseUrl, TestContext.Current.CancellationToken);

        Assert.Equal(firstSlug, store.Snapshot.Override.CacheDirectorySlug);
    }

    [Fact]
    public async Task SetBaseUrlAsync_DifferentUrls_ProduceDifferentSlugs()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync("https://huggingface.co/some-org/some-model/resolve/main/subfolder", TestContext.Current.CancellationToken);
        var firstSlug = store.Snapshot.Override.CacheDirectorySlug;

        await store.SetBaseUrlAsync("https://huggingface.co/other-org/other-model/resolve/main/subfolder", TestContext.Current.CancellationToken);
        var secondSlug = store.Snapshot.Override.CacheDirectorySlug;

        Assert.NotEqual(firstSlug, secondSlug);
    }

    [Fact]
    public async Task SetBaseUrlAsync_UrlsDifferingOnlyByPathCasing_ProduceDifferentSlugs()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync("https://huggingface.co/Some-Org/Some-Model/resolve/main/subfolder", TestContext.Current.CancellationToken);
        var firstSlug = store.Snapshot.Override.CacheDirectorySlug;

        await store.SetBaseUrlAsync("https://huggingface.co/some-org/some-model/resolve/main/subfolder", TestContext.Current.CancellationToken);
        var secondSlug = store.Snapshot.Override.CacheDirectorySlug;

        Assert.NotEqual(firstSlug, secondSlug);
    }

    [Fact]
    public async Task SetBaseUrlAsync_UrlsDifferingOnlyByHostCasing_ProduceSameSlug()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync("https://HuggingFace.co/some-org/some-model/resolve/main/subfolder", TestContext.Current.CancellationToken);
        var firstSlug = store.Snapshot.Override.CacheDirectorySlug;

        await store.SetBaseUrlAsync("https://huggingface.co/some-org/some-model/resolve/main/subfolder", TestContext.Current.CancellationToken);
        var secondSlug = store.Snapshot.Override.CacheDirectorySlug;

        Assert.Equal(firstSlug, secondSlug);
    }

    [Fact]
    public async Task SetBaseUrlAsync_HostCasingAndDefaultPort_NormalizesBaseUrl()
    {
        var store = CreateStore(DefaultOptions());

        await store.SetBaseUrlAsync(
            "https://HuggingFace.co:443/some-org/some-model/resolve/main/subfolder", TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://huggingface.co/some-org/some-model/resolve/main/subfolder", store.Snapshot.Override.BaseUrl);
    }

    [Fact]
    public async Task Constructor_FileHasTamperedSlug_RecomputesSlugFromBaseUrl_IgnoringPersistedValue()
    {
        const string baseUrl = "https://huggingface.co/some-org/some-model/resolve/main/subfolder";

        // Legitimately produced by SetBaseUrlAsync, so we know exactly what slug should come back.
        var reference = CreateStore(DefaultOptions());
        await reference.SetBaseUrlAsync(baseUrl, TestContext.Current.CancellationToken);
        var expectedSlug = reference.Snapshot.Override.CacheDirectorySlug;

        // Now hand-tamper the persisted file with a path-traversal slug and reload.
        File.WriteAllText(_tempPath, $$"""{"BaseUrl":"{{baseUrl}}","CacheDirectorySlug":"../../evil"}""");
        var store = CreateStore(DefaultOptions());

        Assert.Equal(baseUrl, store.Snapshot.Override.BaseUrl);
        Assert.Equal(expectedSlug, store.Snapshot.Override.CacheDirectorySlug);
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            File.Delete(_tempPath);
        }
    }
}
