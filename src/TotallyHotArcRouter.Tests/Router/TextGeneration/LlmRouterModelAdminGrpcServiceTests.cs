using System.Net;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
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

    private sealed class ThrowingLlmRouterModelOverrideStore(Exception exception) : ILlmRouterModelOverrideStore
    {
        public LlmRouterModelSnapshot Snapshot => throw exception;

        public event Action? Changed { add { } remove { } }

        public Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default) => throw exception;
    }

    private static LlmRouterModelAdminGrpcService CreateService(ILlmRouterModelOverrideStore overrideStore)
    {
        var factory = new FakeHttpClientFactory(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
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
