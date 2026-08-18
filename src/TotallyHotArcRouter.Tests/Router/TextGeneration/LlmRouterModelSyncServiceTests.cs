using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Router.TextGeneration;

/// <summary>
/// Covers <see cref="LlmRouterModelSyncService"/> end to end against a fake Hugging Face endpoint: per-file
/// progress sequencing, checksum-verified vs. existence-only completion depending on probe availability, a
/// checksum mismatch failing only that one file, and files landing in the active override's own cache
/// directory - no real network I/O.
/// </summary>
public sealed class LlmRouterModelSyncServiceTests
{
    private const string Owner = "some-org";
    private const string Repo = "some-model";
    private const string PathPrefix = "subfolder";
    private const string BaseUrl = $"https://huggingface.co/{Owner}/{Repo}/resolve/main/{PathPrefix}";
    private const string TreeApiUrl = $"https://huggingface.co/api/models/{Owner}/{Repo}/tree/main/{PathPrefix}";

    private static readonly Dictionary<string, string> Fixtures =
        LlmRouterModelFiles.All.ToDictionary(f => f, f => $"content-of-{f}");

    [Fact]
    public async Task SyncAsync_NoChecksumAvailable_CompletesUnverified()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : ServeFixture(request));

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(LlmRouterModelFiles.All.Count, result.Files.Count);
        Assert.All(result.Files, outcome => Assert.True(outcome.Succeeded, $"{outcome.FileName}: {outcome.ErrorMessage}"));
        Assert.All(result.Files, outcome => Assert.False(outcome.ChecksumVerified));
    }

    [Fact]
    public async Task SyncAsync_ChecksumAvailableAndMatching_CompletesVerified()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl ? ServeTree() : ServeFixture(request));

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        Assert.All(result.Files, outcome => Assert.True(outcome.Succeeded, $"{outcome.FileName}: {outcome.ErrorMessage}"));
        Assert.All(result.Files, outcome => Assert.True(outcome.ChecksumVerified));
    }

    [Fact]
    public async Task SyncAsync_ChecksumMismatch_FailsOnlyThatFile()
    {
        using var scope = new TempOverrideScope();
        // The tree API publishes the real fixture's checksum, but the server serves different bytes for
        // model.onnx - exactly what a file tampered with (or truncated) after publication would look like.
        var servedFixtures = new Dictionary<string, string>(Fixtures) { ["model.onnx"] = "tampered-bytes" };
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl ? ServeTree() : ServeFixture(request, servedFixtures));

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        var mismatched = result.Files.Single(f => f.FileName == "model.onnx");
        Assert.False(mismatched.Succeeded);
        Assert.Contains("checksum", mismatched.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LlmRouterModelFiles.All.Count - 1, result.Files.Count(f => f.Succeeded));
    }

    [Fact]
    public async Task SyncAsync_ReportsDownloadingThenCompletedPerFile()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : ServeFixture(request));
        var progress = new RecordingProgress();

        await service.SyncAsync(progress, TestContext.Current.CancellationToken);

        var genAiConfigEvents = progress.Events.Where(e => e.FileName == "genai_config.json").Select(e => e.Stage).ToList();
        Assert.Equal(
            [LlmRouterModelSyncStage.Downloading, LlmRouterModelSyncStage.Downloading, LlmRouterModelSyncStage.Completed],
            genAiConfigEvents);
    }

    [Fact]
    public async Task SyncAsync_ExistingFile_SkipsDownload_ReportsCompletedUnverified()
    {
        using var scope = new TempOverrideScope();
        Directory.CreateDirectory(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory());
        var existingPath = Path.Combine(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory(), "genai_config.json");
        await File.WriteAllTextAsync(existingPath, "already-cached", TestContext.Current.CancellationToken);

        var downloadedFileNames = new List<string>();
        var service = CreateService(scope.OverrideStore, request =>
        {
            if (request.RequestUri!.ToString() == TreeApiUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            downloadedFileNames.Add(request.RequestUri.Segments[^1]);
            return ServeFixture(request);
        });

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("genai_config.json", downloadedFileNames);
        Assert.True(result.Files.Single(f => f.FileName == "genai_config.json").Succeeded);
        Assert.Equal("already-cached", await File.ReadAllTextAsync(existingPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SyncAsync_ExistingFileMatchesPublishedChecksum_SkipsDownload_ReportsCompletedVerified()
    {
        using var scope = new TempOverrideScope();
        Directory.CreateDirectory(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory());
        var existingPath = Path.Combine(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory(), "genai_config.json");
        await File.WriteAllTextAsync(existingPath, Fixtures["genai_config.json"], TestContext.Current.CancellationToken);

        var downloadedFileNames = new List<string>();
        var service = CreateService(scope.OverrideStore, request =>
        {
            if (request.RequestUri!.ToString() == TreeApiUrl)
            {
                return ServeTree();
            }

            downloadedFileNames.Add(request.RequestUri.Segments[^1]);
            return ServeFixture(request);
        });

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("genai_config.json", downloadedFileNames);
        var outcome = result.Files.Single(f => f.FileName == "genai_config.json");
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.ChecksumVerified);
    }

    [Fact]
    public async Task SyncAsync_ExistingFileFailsPublishedChecksum_RedownloadsAndReplacesIt()
    {
        using var scope = new TempOverrideScope();
        Directory.CreateDirectory(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory());
        var existingPath = Path.Combine(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory(), "genai_config.json");
        // The cached file's bytes don't match what the tree API publishes for this file - simulating
        // corruption or tampering since it was last synced.
        await File.WriteAllTextAsync(existingPath, "corrupted-cached-bytes", TestContext.Current.CancellationToken);

        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl ? ServeTree() : ServeFixture(request));

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "genai_config.json");
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.True(outcome.ChecksumVerified);
        Assert.Equal(Fixtures["genai_config.json"], await File.ReadAllTextAsync(existingPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SyncAsync_ExistingFileFailsPublishedChecksumAndRedownloadFails_QuarantinesMismatchedFile()
    {
        using var scope = new TempOverrideScope();
        Directory.CreateDirectory(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory());
        var existingPath = Path.Combine(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory(), "genai_config.json");
        // The cached file's bytes don't match what the tree API publishes, and the re-download that the
        // mismatch triggers also fails - the known-bad cached bytes must not be left in place, or a status
        // check (and OnnxTextGenerationClient's lazy loader) would keep treating this file as synced
        // because they only check File.Exists.
        await File.WriteAllTextAsync(existingPath, "corrupted-cached-bytes", TestContext.Current.CancellationToken);

        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl
                ? ServeTree()
                : request.RequestUri.Segments[^1] == "genai_config.json"
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : ServeFixture(request));

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "genai_config.json");
        Assert.False(outcome.Succeeded);
        Assert.False(File.Exists(existingPath));
    }

    [Fact]
    public async Task SyncAsync_ModelOnnxDataMissing_SkipsOptionalFileWithoutFailing()
    {
        // An export that inlines all weights in model.onnx never publishes model.onnx.data at all - a
        // 404 for that one file must not fail the sync (LlmRouterModelFiles.IsOptional), unlike a 404
        // for any of the other four files.
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
        {
            if (request.RequestUri!.ToString() == TreeApiUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return request.RequestUri.Segments[^1] == LlmRouterModelFiles.ModelOnnxDataFileName
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : ServeFixture(request);
        });

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        Assert.All(result.Files, outcome => Assert.True(outcome.Succeeded, $"{outcome.FileName}: {outcome.ErrorMessage}"));
        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Assert.False(File.Exists(Path.Combine(cacheDirectory, LlmRouterModelFiles.ModelOnnxDataFileName)));
    }

    [Fact]
    public async Task SyncAsync_NonOptionalFileMissing_FailsThatFile()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
        {
            if (request.RequestUri!.ToString() == TreeApiUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return request.RequestUri.Segments[^1] == "model.onnx"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : ServeFixture(request);
        });

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        var failed = result.Files.Single(f => f.FileName == "model.onnx");
        Assert.False(failed.Succeeded);
    }

    [Fact]
    public async Task SyncAsync_WritesFilesIntoTheOverrideOwnCacheDirectory()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : ServeFixture(request));

        await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Assert.All(LlmRouterModelFiles.All, fileName => Assert.True(File.Exists(Path.Combine(cacheDirectory, fileName))));
    }

    private static HttpResponseMessage ServeFixture(HttpRequestMessage request, Dictionary<string, string>? fixtures = null)
    {
        var fileName = request.RequestUri!.Segments[^1];
        var content = (fixtures ?? Fixtures)[fileName];
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) };
    }

    private static HttpResponseMessage ServeTree()
    {
        var entries = Fixtures.Select(kvp =>
        {
            var oid = GitBlobHash.Compute(Encoding.UTF8.GetBytes(kvp.Value));
            return $$"""{ "type": "file", "path": "{{PathPrefix}}/{{kvp.Key}}", "oid": "{{oid}}", "size": {{Encoding.UTF8.GetByteCount(kvp.Value)}} }""";
        });
        var json = $"[{string.Join(",", entries)}]";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static LlmRouterModelSyncService CreateService(
        ILlmRouterModelOverrideStore overrideStore,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = new FakeHttpClientFactory(new FakeHttpMessageHandler(respond));
        var probe = new LlmRouterModelChecksumProbe(factory, NullLogger<LlmRouterModelChecksumProbe>.Instance);
        return new LlmRouterModelSyncService(factory, probe, overrideStore, NullLogger<LlmRouterModelSyncService>.Instance);
    }

    private sealed class RecordingProgress : IProgress<LlmRouterModelSyncProgress>
    {
        public List<LlmRouterModelSyncProgress> Events { get; } = [];

        public void Report(LlmRouterModelSyncProgress value) => Events.Add(value);
    }

    /// <summary>
    /// Wraps a <see cref="FakeLlmRouterModelOverrideStore"/> pointed at a uniquely-slugged (and therefore
    /// collision-free) cache directory under the real <c>%LOCALAPPDATA%</c> models root, and deletes that
    /// directory on dispose so these tests leave nothing behind.
    /// </summary>
    private sealed class TempOverrideScope : IDisposable
    {
        public FakeLlmRouterModelOverrideStore OverrideStore { get; }

        public TempOverrideScope(string baseUrl = BaseUrl)
        {
            var overrideValue = new LlmRouterModelOverride(baseUrl, $"test-{Guid.NewGuid():N}");
            OverrideStore = new FakeLlmRouterModelOverrideStore(overrideValue);
        }

        public void Dispose()
        {
            var cacheDirectory = OverrideStore.Snapshot.Override.ResolveCacheDirectory();
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }
}
