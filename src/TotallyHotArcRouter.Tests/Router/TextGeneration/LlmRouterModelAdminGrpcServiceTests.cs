using System.Net;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.CodeRouterBench;
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
    private static ServerCallContext CreateContext(CancellationToken cancellationToken) =>
        TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: cancellationToken,
            peer: "test-peer",
            authContext: null!,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });

    [Fact]
    public async Task GetLlmRouterModelStatus_NoFilesCached_OnlyModelOnnxDataIsOptional()
    {
        using var scope = new TempOverrideScope();
        var service = CreateService(scope.OverrideStore);

        var response = await service.GetLlmRouterModelStatus(
            new Contract.GetLlmRouterModelStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(LlmRouterModelFiles.All.Count, response.Files.Count);
        foreach (var file in response.Files)
        {
            Assert.False(file.Synced);
            Assert.Equal(file.FileName == LlmRouterModelFiles.ModelOnnxDataFileName, file.IsOptional);
        }
    }

    [Fact]
    public async Task GetLlmRouterModelStatus_ModelOnnxDataCached_StillReportsItOptional()
    {
        using var scope = new TempOverrideScope();
        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(cacheDirectory, LlmRouterModelFiles.ModelOnnxDataFileName),
            "weights",
            TestContext.Current.CancellationToken);

        var service = CreateService(scope.OverrideStore);

        var response = await service.GetLlmRouterModelStatus(
            new Contract.GetLlmRouterModelStatusRequest(), CreateContext(TestContext.Current.CancellationToken));

        var modelOnnxData = Assert.Single(response.Files, f => f.FileName == LlmRouterModelFiles.ModelOnnxDataFileName);
        Assert.True(modelOnnxData.Synced);
        Assert.True(modelOnnxData.IsOptional);

        var required = Assert.Single(response.Files, f => f.FileName == "model.onnx");
        Assert.False(required.IsOptional);
    }

    [Fact]
    public async Task SetLlmRouterModelBaseUrl_PersistenceFailure_ThrowsInternalRpcException()
    {
        var overrideStore = new ThrowingLlmRouterModelOverrideStore(new IOException("disk full"));
        var service = CreateService(overrideStore);

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.SetLlmRouterModelBaseUrl(
            new Contract.SetLlmRouterModelBaseUrlRequest { BaseUrl = "https://huggingface.co/some-org/some-model/resolve/main" },
            CreateContext(TestContext.Current.CancellationToken)));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
    }

    [Fact]
    public async Task SyncLlmRouterModel_StreamsThePlanFirstListingEveryFile_WhenNoneAreCachedYet()
    {
        using var scope = new TempOverrideScope();
        var fixtures = LlmRouterModelFiles.All.ToDictionary(f => f, f => $"content-of-{f}");
        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.AbsolutePath.Contains("/api/models/", StringComparison.Ordinal)
                ? ServeTree(fixtures)
                : ServeFixture(request, fixtures));
        var writer = new FakeServerStreamWriter<Contract.LlmRouterModelSyncStreamEvent>();

        await service.SyncLlmRouterModel(new Contract.SyncLlmRouterModelRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var firstEvent = Assert.Single(writer.Written, e => e.EventCase == Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.Plan);
        Assert.Same(writer.Written[0], firstEvent);
        Assert.Equal(LlmRouterModelFiles.All.Count, firstEvent.Plan.Files.Count);
        Assert.Equal(firstEvent.Plan.Files.Sum(f => f.SizeBytes), firstEvent.Plan.TotalBytes);
        Assert.True(firstEvent.Plan.TotalBytes > 0);
    }

    [Fact]
    public async Task SyncLlmRouterModel_FileAlreadyCurrent_IsOmittedFromThePlanAndStreamsNoFailedEvent()
    {
        using var scope = new TempOverrideScope();
        var cacheDirectory = scope.OverrideStore.Snapshot.Override.ResolveCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);
        var fixtures = LlmRouterModelFiles.All.ToDictionary(f => f, f => $"content-of-{f}");
        await File.WriteAllTextAsync(
            Path.Combine(cacheDirectory, "genai_config.json"), fixtures["genai_config.json"], TestContext.Current.CancellationToken);

        var service = CreateService(scope.OverrideStore, request =>
            request.RequestUri!.AbsolutePath.Contains("/api/models/", StringComparison.Ordinal)
                ? ServeTree(fixtures)
                : ServeFixture(request, fixtures));
        var writer = new FakeServerStreamWriter<Contract.LlmRouterModelSyncStreamEvent>();

        await service.SyncLlmRouterModel(new Contract.SyncLlmRouterModelRequest(), writer, CreateContext(TestContext.Current.CancellationToken));

        var plan = Assert.Single(writer.Written, e => e.EventCase == Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.Plan).Plan;
        Assert.DoesNotContain(plan.Files, f => f.FileName == "genai_config.json");
        Assert.Equal(LlmRouterModelFiles.All.Count - 1, plan.Files.Count);

        Assert.DoesNotContain(
            writer.Written,
            e => e.EventCase == Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.Progress &&
                 e.Progress.FileName == "genai_config.json" &&
                 e.Progress.Stage == Contract.LlmRouterModelSyncStage.Failed);

        var finalEvent = Assert.Single(writer.Written, e => e.EventCase == Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.FinalStatus);
        Assert.True(finalEvent.FinalStatus.Current);
    }

    private static HttpResponseMessage ServeFixture(HttpRequestMessage request, Dictionary<string, string> fixtures)
    {
        var fileName = request.RequestUri!.Segments[^1];
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixtures[fileName], System.Text.Encoding.UTF8) };
    }

    private static HttpResponseMessage ServeTree(Dictionary<string, string> fixtures)
    {
        // TempOverrideScope's base URL has no folder suffix beyond the repo root, so the tree API's
        // pathPrefix is empty and each entry's path is just the bare file name.
        var entries = fixtures.Select(kvp =>
        {
            var oid = GitBlobHash.Compute(System.Text.Encoding.UTF8.GetBytes(kvp.Value));
            return $$"""{ "type": "file", "path": "{{kvp.Key}}", "oid": "{{oid}}", "size": {{System.Text.Encoding.UTF8.GetByteCount(kvp.Value)}} }""";
        });
        var json = $"[{string.Join(",", entries)}]";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
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

        public event Action? Changed { add { } remove { } }

        public Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default) => throw exception;
    }

    private static LlmRouterModelAdminGrpcService CreateService(ILlmRouterModelOverrideStore overrideStore) =>
        CreateService(overrideStore, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

    private static LlmRouterModelAdminGrpcService CreateService(
        ILlmRouterModelOverrideStore overrideStore,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = new FakeHttpClientFactory(new FakeHttpMessageHandler(respond));
        var probe = new LlmRouterModelChecksumProbe(factory, NullLogger<LlmRouterModelChecksumProbe>.Instance);
        var syncService = new LlmRouterModelSyncService(factory, probe, overrideStore, NullLogger<LlmRouterModelSyncService>.Instance);
        return new LlmRouterModelAdminGrpcService(overrideStore, syncService, NullLogger<LlmRouterModelAdminGrpcService>.Instance);
    }

    private sealed class TempOverrideScope : IDisposable
    {
        public FakeLlmRouterModelOverrideStore OverrideStore { get; }

        public TempOverrideScope()
        {
            var overrideValue = new LlmRouterModelOverride(
                "https://huggingface.co/some-org/some-model/resolve/main", $"test-{Guid.NewGuid():N}");
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
