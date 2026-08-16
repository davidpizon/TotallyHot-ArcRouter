using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// gRPC service backing the Governance → Benchmark Data panel's "Local Voter Model" section: reports the
/// llm_router ONNX voter's file sync state, switches the active model by base URL, and runs a sync with
/// streamed per-file progress. Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the
/// same loopback TLS endpoint as <c>TelemetryService</c> and <c>BenchmarkDataAdminService</c>.
/// </summary>
public sealed class LlmRouterModelAdminGrpcService : Contract.LlmRouterModelAdminService.LlmRouterModelAdminServiceBase
{
    private readonly ILlmRouterModelOverrideStore _overrideStore;
    private readonly LlmRouterModelSyncService _syncService;

    /// <summary>Initializes a new instance of the <see cref="LlmRouterModelAdminGrpcService"/> class.</summary>
    public LlmRouterModelAdminGrpcService(ILlmRouterModelOverrideStore overrideStore, LlmRouterModelSyncService syncService)
    {
        ArgumentNullException.ThrowIfNull(overrideStore);
        ArgumentNullException.ThrowIfNull(syncService);

        _overrideStore = overrideStore;
        _syncService = syncService;
    }

    /// <inheritdoc />
    public override Task<Contract.LlmRouterModelStatusResponse> GetLlmRouterModelStatus(
        Contract.GetLlmRouterModelStatusRequest request,
        ServerCallContext context) => Task.FromResult(BuildStatusResponse());

    /// <inheritdoc />
    public override async Task<Contract.LlmRouterModelStatusResponse> SetLlmRouterModelBaseUrl(
        Contract.SetLlmRouterModelBaseUrlRequest request,
        ServerCallContext context)
    {
        try
        {
            await _overrideStore.SetBaseUrlAsync(request.BaseUrl, context.CancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        return BuildStatusResponse();
    }

    /// <inheritdoc />
    public override async Task SyncLlmRouterModel(
        Contract.SyncLlmRouterModelRequest request,
        IServerStreamWriter<Contract.LlmRouterModelSyncStreamEvent> responseStream,
        ServerCallContext context)
    {
        var progress = new StreamingSyncProgress(responseStream);
        var result = await _syncService.SyncAsync(progress, context.CancellationToken).ConfigureAwait(false);

        // The plain per-file progress events above report a Failed stage but carry no error text
        // (LlmRouterModelSyncProgress has none - only the terminal LlmRouterModelSyncResult does). Send
        // one supplemental event per failed file with the reason, mirroring
        // BenchmarkDataAdminGrpcService.SyncBenchmarkData's convention.
        foreach (var outcome in result.Files.Where(f => !f.Succeeded))
        {
            await responseStream.WriteAsync(new Contract.LlmRouterModelSyncStreamEvent
            {
                Progress = new Contract.LlmRouterModelSyncProgressEvent
                {
                    FileName = outcome.FileName,
                    Stage = Contract.LlmRouterModelSyncStage.Failed,
                    Error = outcome.ErrorMessage ?? "Sync failed.",
                },
            }).ConfigureAwait(false);
        }

        await responseStream.WriteAsync(new Contract.LlmRouterModelSyncStreamEvent
        {
            FinalStatus = BuildStatusResponse(),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the wire status response from the active model's cache directory contents. Every file in
    /// <see cref="LlmRouterModelFiles.All"/> is listed regardless of whether it has synced (with
    /// <c>synced = false</c>), the same convention <c>BenchmarkStatusResponse</c> uses.
    /// </summary>
    private Contract.LlmRouterModelStatusResponse BuildStatusResponse()
    {
        var activeOverride = _overrideStore.Snapshot.Override;
        var cacheDirectory = activeOverride.ResolveCacheDirectory();

        var response = new Contract.LlmRouterModelStatusResponse { BaseUrl = activeOverride.BaseUrl };

        // Only trust the recorded verification state when it was produced by a sync of this same
        // model - otherwise a prior model's leftover record could misreport this model's files as
        // checksum-verified after a switch.
        var lastVerification = _syncService.LastVerification;
        var verifiedFiles = string.Equals(lastVerification.BaseUrl, activeOverride.BaseUrl, StringComparison.Ordinal)
            ? lastVerification.Files
            : null;

        var allSynced = true;
        foreach (var fileName in LlmRouterModelFiles.All)
        {
            var filePath = Path.Combine(cacheDirectory, fileName);
            var fileInfo = new FileInfo(filePath);

            if (fileInfo.Exists)
            {
                response.Files.Add(new Contract.LlmRouterModelFile
                {
                    FileName = fileName,
                    Synced = true,
                    SizeBytes = fileInfo.Length,
                    SyncedAtUtc = Timestamp.FromDateTime(fileInfo.LastWriteTimeUtc),
                    ChecksumVerified = verifiedFiles?.GetValueOrDefault(fileName) ?? false,
                });
            }
            else
            {
                // A missing optional file (model.onnx.data absent because the export inlines its
                // weights) doesn't mean the model is out of date - only a missing required file does.
                if (!LlmRouterModelFiles.IsOptional(fileName))
                {
                    allSynced = false;
                }

                response.Files.Add(new Contract.LlmRouterModelFile { FileName = fileName, Synced = false });
            }
        }

        response.Current = allSynced;
        return response;
    }

    /// <summary>
    /// Bridges <see cref="LlmRouterModelSyncService"/>'s synchronous <see cref="IProgress{T}"/> callback
    /// onto the async gRPC response stream, mirroring
    /// <c>BenchmarkDataAdminGrpcService.StreamingSyncProgress</c>'s reasoning: blocking on
    /// <c>WriteAsync</c> inside <see cref="Report"/> is safe here because Kestrel handlers run without a
    /// captured <see cref="SynchronizationContext"/> and every <see cref="IProgress{T}.Report"/> call is
    /// issued sequentially from one thread.
    /// </summary>
    private sealed class StreamingSyncProgress : IProgress<LlmRouterModelSyncProgress>
    {
        private readonly IServerStreamWriter<Contract.LlmRouterModelSyncStreamEvent> _stream;

        public StreamingSyncProgress(IServerStreamWriter<Contract.LlmRouterModelSyncStreamEvent> stream) => _stream = stream;

        public void Report(LlmRouterModelSyncProgress value)
        {
            var wire = new Contract.LlmRouterModelSyncProgressEvent
            {
                FileName = value.FileName,
                Stage = MapStage(value.Stage),
            };

            if (value.BytesTransferred is long bytes)
            {
                wire.BytesTransferred = bytes;
            }

            _stream.WriteAsync(new Contract.LlmRouterModelSyncStreamEvent { Progress = wire }).GetAwaiter().GetResult();
        }

        private static Contract.LlmRouterModelSyncStage MapStage(LlmRouterModelSyncStage stage) => stage switch
        {
            LlmRouterModelSyncStage.Downloading => Contract.LlmRouterModelSyncStage.Downloading,
            LlmRouterModelSyncStage.Verifying => Contract.LlmRouterModelSyncStage.Verifying,
            LlmRouterModelSyncStage.Completed => Contract.LlmRouterModelSyncStage.Completed,
            LlmRouterModelSyncStage.Failed => Contract.LlmRouterModelSyncStage.Failed,
            _ => Contract.LlmRouterModelSyncStage.Unspecified,
        };
    }
}
