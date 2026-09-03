using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Checksums;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Router.TextGeneration;

/// <summary>
/// Covers <see cref="LlmRouterModelAdminGrpcService.GetLlmRouterModelStatus"/>'s file projection - in
/// particular that <see cref="Contract.LlmRouterModelFile.IsOptional"/> is set for
/// <c>model.onnx.data</c> alone, whether or not that file is cached, so the panel can tell its expected
/// absence apart from an unsynced required file.
/// </summary>
public sealed class LlmRouterModelAdminGrpcServiceTests
{
    private static ServerCallContext CreateContext(CancellationToken cancellationToken)
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: cancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    [Fact]
    public async Task GetLlmRouterModelStatus_NoFilesCached_OnlyModelOnnxDataIsOptional()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore);

        var response = await service.GetLlmRouterModelStatus(
            request: new Contract.GetLlmRouterModelStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(expected: LlmRouterModelFiles.All.Count, actual: response.Files.Count);
        foreach (var file in response.Files)
        {
            Assert.False(file.Synced);
            Assert.Equal(expected: file.FileName == LlmRouterModelFiles.ModelOnnxDataFileName, actual: file.IsOptional);
        }
    }

    [Fact]
    public async Task GetLlmRouterModelStatus_ModelOnnxDataCached_StillReportsItOptional()
    {
        using var scope = new TempOverrideScope();
        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(
            path: Path.Combine(path1: cacheDirectory, path2: LlmRouterModelFiles.ModelOnnxDataFileName),
            contents: "weights",
            cancellationToken: TestContext.Current.CancellationToken);

        var service = CreateService(scope.OverrideStore);

        var response = await service.GetLlmRouterModelStatus(
            request: new Contract.GetLlmRouterModelStatusRequest(),
            context: CreateContext(TestContext.Current.CancellationToken));

        var modelOnnxData = Assert.Single(collection: response.Files,
            predicate: f => f.FileName == LlmRouterModelFiles.ModelOnnxDataFileName);
        Assert.True(modelOnnxData.Synced);
        Assert.True(modelOnnxData.IsOptional);

        var required = Assert.Single(collection: response.Files, predicate: f => f.FileName == "model.onnx");
        Assert.False(required.IsOptional);
    }

    [Fact]
    public async Task SetLlmRouterModelBaseUrl_PersistenceFailure_ThrowsInternalRpcException()
    {
        var overrideStore = new ThrowingLlmRouterModelOverrideStore(new IOException("disk full"));
        var service = CreateService(overrideStore);

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.SetLlmRouterModelBaseUrl(
            request: new Contract.SetLlmRouterModelBaseUrlRequest
                { BaseUrl = "https://huggingface.co/some-org/some-model/resolve/main" },
            context: CreateContext(TestContext.Current.CancellationToken)));

        Assert.Equal(expected: StatusCode.Internal, actual: ex.StatusCode);
    }

    [Fact]
    public async Task SyncLlmRouterModel_StreamsThePlanFirstListingEveryFile_WhenNoneAreCachedYet()
    {
        using var scope = new TempOverrideScope();
        var fixtures =
            LlmRouterModelFiles.All.ToDictionary(keySelector: f => f, elementSelector: f => $"content-of-{f}");
        var service = CreateService(overrideStore: scope.OverrideStore, respond: request =>
            request.RequestUri!.AbsolutePath.Contains(value: "/api/models/", comparisonType: StringComparison.Ordinal)
                ? ServeTree(fixtures)
                : ServeFixture(request: request, fixtures: fixtures));
        var writer = new FakeServerStreamWriter<Contract.LlmRouterModelSyncStreamEvent>();

        await service.SyncLlmRouterModel(request: new Contract.SyncLlmRouterModelRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var firstEvent = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.Plan);
        Assert.Same(expected: writer.Written[0], actual: firstEvent);
        Assert.Equal(expected: LlmRouterModelFiles.All.Count, actual: firstEvent.Plan.Files.Count);
        Assert.Equal(expected: firstEvent.Plan.Files.Sum(f => f.SizeBytes), actual: firstEvent.Plan.TotalBytes);
        Assert.True(firstEvent.Plan.TotalBytes > 0);
    }

    [Fact]
    public async Task SyncLlmRouterModel_FileAlreadyCurrent_IsOmittedFromThePlanAndStreamsNoFailedEvent()
    {
        using var scope = new TempOverrideScope();
        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);
        var fixtures =
            LlmRouterModelFiles.All.ToDictionary(keySelector: f => f, elementSelector: f => $"content-of-{f}");
        await File.WriteAllTextAsync(
            path: Path.Combine(path1: cacheDirectory, path2: "genai_config.json"),
            contents: fixtures["genai_config.json"], cancellationToken: TestContext.Current.CancellationToken);

        var service = CreateService(overrideStore: scope.OverrideStore, respond: request =>
            request.RequestUri!.AbsolutePath.Contains(value: "/api/models/", comparisonType: StringComparison.Ordinal)
                ? ServeTree(fixtures)
                : ServeFixture(request: request, fixtures: fixtures));
        var writer = new FakeServerStreamWriter<Contract.LlmRouterModelSyncStreamEvent>();

        await service.SyncLlmRouterModel(request: new Contract.SyncLlmRouterModelRequest(), responseStream: writer,
            context: CreateContext(TestContext.Current.CancellationToken));

        var plan = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.Plan).Plan;
        Assert.DoesNotContain(collection: plan.Files, filter: f => f.FileName == "genai_config.json");
        Assert.Equal(expected: LlmRouterModelFiles.All.Count - 1, actual: plan.Files.Count);

        Assert.DoesNotContain(
            collection: writer.Written,
            filter: e => e.EventCase == Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.Progress &&
                         e.Progress.FileName == "genai_config.json" &&
                         e.Progress.Stage == Contract.LlmRouterModelSyncStage.Failed);

        var finalEvent = Assert.Single(collection: writer.Written,
            predicate: e => e.EventCase == Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.FinalStatus);
        Assert.True(finalEvent.FinalStatus.Current);
    }

    private static HttpResponseMessage ServeFixture(HttpRequestMessage request, Dictionary<string, string> fixtures)
    {
        var fileName = request.RequestUri!.Segments[^1];
        return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(content: fixtures[fileName], encoding: Encoding.UTF8) };
    }

    private static HttpResponseMessage ServeTree(Dictionary<string, string> fixtures)
    {
        // TempOverrideScope's base URL has no folder suffix beyond the repo root, so the tree API's
        // pathPrefix is empty and each entry's path is just the bare file name.
        var entries = fixtures.Select(kvp =>
        {
            var oid = GitBlobHash.Compute(Encoding.UTF8.GetBytes(kvp.Value));
            return
                $$"""{ "type": "file", "path": "{{kvp.Key}}", "oid": "{{oid}}", "size": {{Encoding.UTF8.GetByteCount(kvp.Value)}} }""";
        });
        var json = $"[{string.Join(separator: ",", values: entries)}]";
        return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(content: json, encoding: Encoding.UTF8, mediaType: "application/json") };
    }

    private static LlmRouterModelAdminGrpcService CreateService(ILlmRouterModelOverrideStore overrideStore)
    {
        return CreateService(overrideStore: overrideStore,
            respond: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static LlmRouterModelAdminGrpcService CreateService(
        ILlmRouterModelOverrideStore overrideStore,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = new FakeHttpClientFactory(new FakeHttpMessageHandler(respond));
        var probe = new LlmRouterModelChecksumProbe(httpClientFactory: factory,
            logger: NullLogger<LlmRouterModelChecksumProbe>.Instance);
        var syncService = new LlmRouterModelSyncService(httpClientFactory: factory, probe: probe,
            overrideStore: overrideStore, logger: NullLogger<LlmRouterModelSyncService>.Instance);
        return new LlmRouterModelAdminGrpcService(overrideStore: overrideStore, syncService: syncService,
            logger: NullLogger<LlmRouterModelAdminGrpcService>.Instance);
    }

    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLlmRouterModelOverrideStore(Exception exception) : ILlmRouterModelOverrideStore
    {
        public LlmRouterModelSnapshot Snapshot => throw exception;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class TempOverrideScope : IDisposable
    {
        public TempOverrideScope()
        {
            var overrideValue = new LlmRouterModelOverride(
                BaseUrl: "https://huggingface.co/some-org/some-model/resolve/main",
                CacheDirectorySlug: $"test-{Guid.NewGuid():N}");
            OverrideStore = new FakeLlmRouterModelOverrideStore(overrideValue);
        }

        public FakeLlmRouterModelOverrideStore OverrideStore { get; }

        public void Dispose()
        {
            var cacheDirectory = OverrideStore.Snapshot.Override.ResolveCacheDirectory();
            if (Directory.Exists(cacheDirectory)) Directory.Delete(path: cacheDirectory, true);
        }
    }
}