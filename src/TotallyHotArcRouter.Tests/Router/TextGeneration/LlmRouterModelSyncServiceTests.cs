using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Router.TextGeneration;

/// <summary>
/// Covers <see cref="LlmRouterModelSyncService"/> end to end against a fake Hugging Face endpoint: the
/// up-front plan and its size pre-filter, per-file progress sequencing, checksum-verified completion, a
/// checksum mismatch failing only that one file, a required file refusing to install without a published
/// checksum, and files landing in the active override's own cache directory - no real network I/O.
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
    public async Task SyncAsync_NoProbeResult_FailsRequiredFilesAndSkipsOptionalFile_WithoutDownloadingAnything()
    {
        // A model source the probe cannot reach or parse has no published checksum to verify against -
        // this pipeline must refuse to install unverified bytes rather than trusting whatever the server
        // happens to serve.
        using var scope = new TempOverrideScope();
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

        Assert.Empty(downloadedFileNames);
        foreach (var fileName in LlmRouterModelFiles.All)
        {
            var outcome = result.Files.Single(f => f.FileName == fileName);
            if (LlmRouterModelFiles.IsOptional(fileName))
            {
                Assert.True(outcome.Succeeded);
                Assert.False(outcome.ChecksumVerified);
            }
            else
            {
                Assert.False(outcome.Succeeded);
                Assert.False(outcome.ChecksumVerified);
            }
        }

        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Assert.All(LlmRouterModelFiles.All, fileName => Assert.False(File.Exists(Path.Combine(cacheDirectory, fileName))));
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

        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Assert.False(File.Exists(Path.Combine(cacheDirectory, "model.onnx")));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.download"));
    }

    [Fact]
    public async Task SyncAsync_LfsTrackedFile_VerifiesAgainstItsRealContentSha256AndSucceeds()
    {
        // model.onnx and model.onnx.data are almost always Git LFS-tracked on Hugging Face: the tree
        // entry's top-level oid is the small pointer file's git blob SHA-1, not the real content's hash -
        // ServeTree's lfsFileNames option serves a deliberately-wrong top-level oid for this file, so this
        // only passes if the sync actually verifies against lfs.oid (a SHA-256) instead.
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl
                ? ServeTree(lfsFileNames: new HashSet<string> { "model.onnx" })
                : ServeFixture(request));

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "model.onnx");
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.True(outcome.ChecksumVerified);
    }

    [Fact]
    public async Task SyncAsync_CachedLfsTrackedFile_ReVerifiesAgainstItsRealContentSha256_SkipsRedownload()
    {
        using var scope = new TempOverrideScope();
        Directory.CreateDirectory(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory());
        var existingPath = Path.Combine(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory(), "model.onnx");
        await File.WriteAllTextAsync(existingPath, Fixtures["model.onnx"], TestContext.Current.CancellationToken);

        var downloadedFileNames = new List<string>();
        var service = CreateService(scope.OverrideStore, request =>
        {
            if (request.RequestUri!.ToString() == TreeApiUrl)
            {
                return ServeTree(lfsFileNames: new HashSet<string> { "model.onnx" });
            }

            downloadedFileNames.Add(request.RequestUri.Segments[^1]);
            return ServeFixture(request);
        });

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("model.onnx", downloadedFileNames);
        var outcome = result.Files.Single(f => f.FileName == "model.onnx");
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.True(outcome.ChecksumVerified);
    }

    [Fact]
    public async Task SyncAsync_ReportsDownloadingThenCompletedPerFile_WithConstantTotalBytes()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl ? ServeTree() : ServeFixture(request));
        var progress = new RecordingProgress();

        await service.SyncAsync(progress, TestContext.Current.CancellationToken);

        var genAiConfigEvents = progress.Events.Where(e => e.FileName == "genai_config.json").ToList();
        Assert.Equal(
            [
                LlmRouterModelSyncStage.Downloading,
                LlmRouterModelSyncStage.Downloading,
                LlmRouterModelSyncStage.Verifying,
                LlmRouterModelSyncStage.Completed,
            ],
            genAiConfigEvents.Select(e => e.Stage));

        var expectedSize = Encoding.UTF8.GetByteCount(Fixtures["genai_config.json"]);
        Assert.All(genAiConfigEvents, e => Assert.Equal(expectedSize, e.TotalBytes));

        // BytesTransferred is monotonically non-decreasing across the Downloading events specifically
        // (the fixtures are small enough to land in a single throttled report, so equal is expected, not
        // just less-than); Verifying/Completed report no bytes of their own.
        var downloadedBytes = genAiConfigEvents
            .Where(e => e.Stage == LlmRouterModelSyncStage.Downloading)
            .Select(e => e.BytesTransferred ?? 0)
            .ToList();
        Assert.Equal(downloadedBytes.OrderBy(v => v), downloadedBytes);
    }

    [Fact]
    public async Task SyncAsync_ExistingFileMatchesPublishedSizeAndChecksum_SkipsDownload_ReportsCompletedVerified()
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
    public async Task SyncAsync_ExistingFileSizeMismatch_RedownloadsWithoutHashing()
    {
        using var scope = new TempOverrideScope();
        Directory.CreateDirectory(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory());
        var existingPath = Path.Combine(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory(), "genai_config.json");
        // A different length than the published size (29 bytes for "content-of-genai_config.json")
        // - the size pre-filter must catch this and requeue the file for download without ever hashing
        // the multi-hundred-MB-scale file this stands in for.
        await File.WriteAllTextAsync(existingPath, "short", TestContext.Current.CancellationToken);

        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl ? ServeTree() : ServeFixture(request));
        var progress = new RecordingProgress();

        var result = await service.SyncAsync(progress, TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "genai_config.json");
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.True(outcome.ChecksumVerified);
        Assert.Equal(Fixtures["genai_config.json"], await File.ReadAllTextAsync(existingPath, TestContext.Current.CancellationToken));

        // Exactly one Verifying event - from the re-download's own verification - confirms the
        // size-mismatched cached file was never hashed.
        Assert.Single(progress.Events, e => e.FileName == "genai_config.json" && e.Stage == LlmRouterModelSyncStage.Verifying);
    }

    [Fact]
    public async Task SyncAsync_ExistingFileSameSizeDifferentContent_RedownloadsAndReplacesIt()
    {
        using var scope = new TempOverrideScope();
        Directory.CreateDirectory(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory());
        var existingPath = Path.Combine(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory(), "genai_config.json");
        // Same byte length as the published fixture (so the size pre-filter alone cannot catch this) but
        // different content - only a hash comparison can detect this corruption/tampering.
        var sameLengthCorruptedContent = new string('x', Encoding.UTF8.GetByteCount(Fixtures["genai_config.json"]));
        await File.WriteAllTextAsync(existingPath, sameLengthCorruptedContent, TestContext.Current.CancellationToken);

        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl ? ServeTree() : ServeFixture(request));
        var progress = new RecordingProgress();

        var result = await service.SyncAsync(progress, TestContext.Current.CancellationToken);

        var outcome = result.Files.Single(f => f.FileName == "genai_config.json");
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.True(outcome.ChecksumVerified);
        Assert.Equal(Fixtures["genai_config.json"], await File.ReadAllTextAsync(existingPath, TestContext.Current.CancellationToken));

        // Two Verifying events: the initial (same-size) hash attempt that found the mismatch, and the
        // re-download's own verification.
        Assert.Equal(2, progress.Events.Count(e => e.FileName == "genai_config.json" && e.Stage == LlmRouterModelSyncStage.Verifying));
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

        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.download"));
    }

    [Fact]
    public async Task SyncAsync_ModelOnnxDataNotPublished_SkipsOptionalFileWithoutFailing()
    {
        // An export that inlines all weights in model.onnx never publishes model.onnx.data at all - its
        // absence from the published tree must not fail the sync (LlmRouterModelFiles.IsOptional), unlike
        // any of the other four files, and no download of it is even attempted.
        using var scope = new TempOverrideScope();
        var downloadedFileNames = new List<string>();
        var service = CreateService(scope.OverrideStore, request =>
        {
            if (request.RequestUri!.ToString() == TreeApiUrl)
            {
                return ServeTree(excludeFileName: LlmRouterModelFiles.ModelOnnxDataFileName);
            }

            downloadedFileNames.Add(request.RequestUri.Segments[^1]);
            return ServeFixture(request);
        });

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        Assert.All(result.Files, outcome => Assert.True(outcome.Succeeded, $"{outcome.FileName}: {outcome.ErrorMessage}"));
        Assert.DoesNotContain(LlmRouterModelFiles.ModelOnnxDataFileName, downloadedFileNames);
        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Assert.False(File.Exists(Path.Combine(cacheDirectory, LlmRouterModelFiles.ModelOnnxDataFileName)));
    }

    [Fact]
    public async Task SyncAsync_RequiredFileNotPublished_FailsThatFileWithoutDownloadingIt()
    {
        using var scope = new TempOverrideScope();
        var downloadedFileNames = new List<string>();
        var service = CreateService(scope.OverrideStore, request =>
        {
            if (request.RequestUri!.ToString() == TreeApiUrl)
            {
                return ServeTree(excludeFileName: "model.onnx");
            }

            downloadedFileNames.Add(request.RequestUri.Segments[^1]);
            return ServeFixture(request);
        });

        var result = await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        var failed = result.Files.Single(f => f.FileName == "model.onnx");
        Assert.False(failed.Succeeded);
        Assert.DoesNotContain("model.onnx", downloadedFileNames);
    }

    [Fact]
    public async Task SyncAsync_WritesFilesIntoTheOverrideOwnCacheDirectory()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl ? ServeTree() : ServeFixture(request));

        await service.SyncAsync(progress: null, TestContext.Current.CancellationToken);

        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Assert.All(LlmRouterModelFiles.All, fileName => Assert.True(File.Exists(Path.Combine(cacheDirectory, fileName))));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.download"));
    }

    [Fact]
    public async Task SyncAsync_ReportsPlanFirst_ListingOnlyStaleFilesWithTheirPublishedSizes()
    {
        using var scope = new TempOverrideScope();
        Directory.CreateDirectory(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory());
        // genai_config.json is already current; the plan must omit it and its size from the total.
        var currentPath = Path.Combine(scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory(), "genai_config.json");
        await File.WriteAllTextAsync(currentPath, Fixtures["genai_config.json"], TestContext.Current.CancellationToken);

        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.ToString() == TreeApiUrl ? ServeTree() : ServeFixture(request));
        var planProgress = new RecordingPlanProgress();

        await service.SyncAsync(progress: null, TestContext.Current.CancellationToken, planProgress);

        Assert.NotNull(planProgress.Plan);
        var expectedStaleFiles = LlmRouterModelFiles.All.Where(f => f != "genai_config.json").ToList();
        Assert.Equal(expectedStaleFiles.Count, planProgress.Plan!.Files.Count);
        Assert.DoesNotContain(planProgress.Plan.Files, f => f.FileName == "genai_config.json");
        foreach (var planFile in planProgress.Plan.Files)
        {
            Assert.Equal(Encoding.UTF8.GetByteCount(Fixtures[planFile.FileName]), planFile.SizeBytes);
        }

        Assert.Equal(planProgress.Plan.Files.Sum(f => f.SizeBytes), planProgress.Plan.TotalBytes);
    }

    private static HttpResponseMessage ServeFixture(HttpRequestMessage request, Dictionary<string, string>? fixtures = null)
    {
        var fileName = request.RequestUri!.Segments[^1];
        var content = (fixtures ?? Fixtures)[fileName];
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) };
    }

    private static HttpResponseMessage ServeTree(string? excludeFileName = null, IReadOnlySet<string>? lfsFileNames = null)
    {
        var entries = Fixtures
            .Where(kvp => kvp.Key != excludeFileName)
            .Select(kvp =>
            {
                var size = Encoding.UTF8.GetByteCount(kvp.Value);
                if (lfsFileNames?.Contains(kvp.Key) == true)
                {
                    // A deliberately-wrong top-level oid (not the real git blob hash of the served bytes)
                    // proves the sync verifies against lfs.oid, not this one.
                    var lfsOid = ContentSha256Hash.Compute(Encoding.UTF8.GetBytes(kvp.Value));
                    return $$"""{ "type": "file", "path": "{{PathPrefix}}/{{kvp.Key}}", "oid": "0000000000000000000000000000000000wrong", "size": 1, "lfs": { "oid": "{{lfsOid}}", "size": {{size}} } }""";
                }

                var oid = GitBlobHash.Compute(Encoding.UTF8.GetBytes(kvp.Value));
                return $$"""{ "type": "file", "path": "{{PathPrefix}}/{{kvp.Key}}", "oid": "{{oid}}", "size": {{size}} }""";
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

    private sealed class RecordingPlanProgress : IProgress<LlmRouterModelSyncPlan>
    {
        public LlmRouterModelSyncPlan? Plan { get; private set; }

        public void Report(LlmRouterModelSyncPlan value) => Plan = value;
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
