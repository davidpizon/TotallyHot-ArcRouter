using Grpc.Core;
using System.Runtime.CompilerServices;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a llm_router model management call fails. Carries a message fit to render in the
/// Governance panel rather than a raw <see cref="RpcException"/>, mirroring
/// <see cref="BenchmarkDataAdminException"/>. See <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class LlmRouterModelAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="LlmRouterModelAdminException"/> class.</summary>
    public LlmRouterModelAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message: message, innerException: innerException, isUnavailable: isUnavailable)
    {
    }
}

/// <summary>One of the llm_router voter's 5 model files, as rendered by the "Local Voter Model" section.</summary>
/// <param name="FileName">The file's name.</param>
/// <param name="Synced">Whether this file is present in the active model's cache directory.</param>
/// <param name="SizeBytes">The cached file's byte size, or 0 if it has never synced.</param>
/// <param name="SyncedAtUtc">When this file was last written to the cache, or <see langword="null"/> if it never has.</param>
/// <param name="ChecksumVerified">Whether the most recent sync verified this file's checksum against Hugging Face.</param>
/// <param name="IsOptional">
/// True only for model.onnx.data: some exports inline all weights into model.onnx and never publish this
/// file, so the panel can render its expected, permanent absence apart from an unsynced required file.
/// </param>
public sealed record LlmRouterModelFileStatusInfo(
    string FileName,
    bool Synced,
    long SizeBytes,
    DateTimeOffset? SyncedAtUtc,
    bool ChecksumVerified,
    bool IsOptional);

/// <summary>The llm_router voter's active model, its 5 files' cache status, and whether all 5 are present.</summary>
/// <param name="BaseUrl">The active model's base folder URL.</param>
/// <param name="Files">Every file the voter needs, whether cached or not.</param>
/// <param name="Current">Whether every file in <paramref name="Files"/> is cached.</param>
public sealed record LlmRouterModelStatusInfo(
    string BaseUrl,
    IReadOnlyList<LlmRouterModelFileStatusInfo> Files,
    bool Current);

/// <summary>The stage of one file's sync, as streamed by <see cref="ILlmRouterModelAdminClient.SyncAsync"/>.</summary>
public enum LlmRouterModelSyncStageInfo
{
    /// <summary>Bytes are being downloaded from the model's base URL.</summary>
    Downloading,

    /// <summary>The downloaded bytes' checksum is being compared to the published value, when available.</summary>
    Verifying,

    /// <summary>The file's sync completed successfully.</summary>
    Completed,

    /// <summary>The file's sync failed; any previously cached copy of this file is untouched.</summary>
    Failed
}

/// <summary>One file's progress update during a sync.</summary>
/// <param name="FileName">The file this update is about.</param>
/// <param name="Stage">The stage the file is currently in.</param>
/// <param name="BytesTransferred">Bytes downloaded so far, when known.</param>
/// <param name="Error">
/// Why the file failed, set only on a terminal <see cref="LlmRouterModelSyncStageInfo.Failed"/>
/// update.
/// </param>
/// <param name="TotalBytes">The file's published size in bytes, when known.</param>
public sealed record LlmRouterModelSyncProgressInfo(
    string FileName,
    LlmRouterModelSyncStageInfo Stage,
    long? BytesTransferred,
    string? Error,
    long? TotalBytes = null);

/// <summary>One file a sync is about to download, from the up-front <see cref="LlmRouterModelSyncPlanInfo"/>.</summary>
/// <param name="FileName">The file's name.</param>
/// <param name="SizeBytes">The file's published size in bytes.</param>
public sealed record LlmRouterModelSyncPlanFileInfo(string FileName, long SizeBytes);

/// <summary>
/// The sync's plan, delivered once before any file downloads: which files are stale and how many bytes
/// the whole run will transfer, so the panel's cumulative progress bar has a stable denominator from the
/// first byte.
/// </summary>
/// <param name="Files">The files that will be downloaded. A file omitted here was already current.</param>
/// <param name="TotalBytes">The combined published size of every file in <paramref name="Files"/>.</param>
public sealed record LlmRouterModelSyncPlanInfo(IReadOnlyList<LlmRouterModelSyncPlanFileInfo> Files, long TotalBytes);

/// <summary>
/// One message on the sync stream: the one-time <see cref="Plan"/>, a per-file <see cref="Progress"/>
/// update, or - exactly once, as the final message - the aggregate <see cref="FinalStatus"/> after every
/// file has been attempted. Exactly one of the three is non-null, mirroring the wire contract's
/// <c>oneof</c>.
/// </summary>
/// <param name="Plan">The sync's plan, set only on the first message when present.</param>
/// <param name="Progress">A per-file progress update, or <see langword="null"/> for the plan/final messages.</param>
/// <param name="FinalStatus">The aggregate status, set only on the final message.</param>
public sealed record LlmRouterModelSyncEvent(
    LlmRouterModelSyncPlanInfo? Plan,
    LlmRouterModelSyncProgressInfo? Progress,
    LlmRouterModelStatusInfo? FinalStatus);

/// <summary>
/// The llm_router model management operations the Governance → Benchmark Data panel's "Local Voter
/// Model" section needs. An interface so <c>LlmRouterModelStore</c> can be unit-tested against a fake
/// without a live proxy or a gRPC channel, mirroring <see cref="IBenchmarkDataAdminClient"/>.
/// </summary>
public interface ILlmRouterModelAdminClient
{
    /// <summary>Reads the active model's base URL and every file's cache status.</summary>
    /// <exception cref="LlmRouterModelAdminException">The call failed or the router is unreachable.</exception>
    Task<LlmRouterModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Switches the active model to <paramref name="baseUrl"/>. Does not download.</summary>
    /// <exception cref="LlmRouterModelAdminException">The call failed, was rejected, or the router is unreachable.</exception>
    Task<LlmRouterModelStatusInfo> SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and checksum-verifies (where possible) every file of the active model, yielding one
    /// <see cref="LlmRouterModelSyncEvent"/> per stage transition, plus one final event carrying the
    /// aggregate status once every file has been attempted.
    /// </summary>
    /// <exception cref="LlmRouterModelAdminException">The call failed or the router is unreachable.</exception>
    IAsyncEnumerable<LlmRouterModelSyncEvent> SyncAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for the proxy's <c>LlmRouterModelAdminService</c> - the Governance → Benchmark Data panel's
/// "Local Voter Model" read, switch, and sync surface. Lives in this plain <c>net10.0</c> library rather
/// than the Windows-only MAUI project so CI can unit-test it, exactly like
/// <see cref="BenchmarkDataAdminClient"/>.
/// </summary>
public sealed class LlmRouterModelAdminClient
    : GrpcAdminClientBase<Contract.LlmRouterModelAdminService.LlmRouterModelAdminServiceClient,
            LlmRouterModelAdminException>,
        ILlmRouterModelAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LlmRouterModelAdminClient"/> class, creating and
    /// owning a channel to <paramref name="serverAddress"/>.
    /// </summary>
    public LlmRouterModelAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress: serverAddress,
            createClient: callInvoker =>
                new Contract.LlmRouterModelAdminService.LlmRouterModelAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmRouterModelAdminClient"/> class over a
    /// caller-supplied generated client. The seam tests use to substitute a fake without a live server;
    /// the caller owns the channel's lifetime.
    /// </summary>
    public LlmRouterModelAdminClient(Contract.LlmRouterModelAdminService.LlmRouterModelAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<LlmRouterModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetLlmRouterModelStatusAsync(request: new Contract.GetLlmRouterModelStatusRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the llm_router model status");
        }
    }

    /// <inheritdoc/>
    public async Task<LlmRouterModelStatusInfo> SetBaseUrlAsync(string baseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        try
        {
            var response = await Client
                .SetLlmRouterModelBaseUrlAsync(
                    request: new Contract.SetLlmRouterModelBaseUrlRequest { BaseUrl = baseUrl },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not switch the llm_router model");
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<LlmRouterModelSyncEvent> SyncAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = Client.SyncLlmRouterModel(request: new Contract.SyncLlmRouterModelRequest(),
            cancellationToken: cancellationToken);
        var stream = call.ResponseStream;

        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await stream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                // Not caught inside a try that also yields: an iterator cannot yield from within a catch
                // block, so MoveNext's outcome is captured here and acted on outside the try.
                throw Wrap(ex: ex, action: "llm_router model sync failed");
            }

            if (!hasNext) yield break;

            yield return MapEvent(stream.Current);
        }
    }

    /// <summary>Converts a gRPC-contract status response into the client's <see cref="LlmRouterModelStatusInfo"/>.</summary>
    private static LlmRouterModelStatusInfo MapStatus(Contract.LlmRouterModelStatusResponse response)
    {
        return new LlmRouterModelStatusInfo(
            BaseUrl: response.BaseUrl,
            Files: [.. response.Files.Select(MapFile)],
            Current: response.Current);
    }

    /// <summary>Converts a gRPC-contract file status into the client's <see cref="LlmRouterModelFileStatusInfo"/>.</summary>
    private static LlmRouterModelFileStatusInfo MapFile(Contract.LlmRouterModelFile file)
    {
        return new LlmRouterModelFileStatusInfo(
            FileName: file.FileName,
            Synced: file.Synced,
            SizeBytes: file.SizeBytes,
            SyncedAtUtc: file.SyncedAtUtc?.ToDateTimeOffset(),
            ChecksumVerified: file.ChecksumVerified,
            IsOptional: file.IsOptional);
    }

    /// <summary>Converts a gRPC-contract sync stream message into the client's <see cref="LlmRouterModelSyncEvent"/>.</summary>
    /// <exception cref="LlmRouterModelAdminException">
    /// <paramref name="wire"/> carries none of <c>Plan</c>, <c>Progress</c>, or <c>FinalStatus</c> - a
    /// malformed message the wire contract's <c>oneof</c> should never actually produce.
    /// </exception>
    private static LlmRouterModelSyncEvent MapEvent(Contract.LlmRouterModelSyncStreamEvent wire)
    {
        return wire.EventCase switch
        {
            Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.Plan =>
                new LlmRouterModelSyncEvent(
                    Plan: new LlmRouterModelSyncPlanInfo(
                        Files:
                        [
                            .. wire.Plan.Files.Select(file =>
                                new LlmRouterModelSyncPlanFileInfo(FileName: file.FileName, SizeBytes: file.SizeBytes))
                        ],
                        TotalBytes: wire.Plan.TotalBytes),
                    null,
                    null),
            Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.FinalStatus =>
                new LlmRouterModelSyncEvent(null, null, FinalStatus: MapStatus(wire.FinalStatus)),
            Contract.LlmRouterModelSyncStreamEvent.EventOneofCase.Progress =>
                new LlmRouterModelSyncEvent(
                    null,
                    Progress: new LlmRouterModelSyncProgressInfo(
                        FileName: wire.Progress.FileName,
                        Stage: MapStage(wire.Progress.Stage),
                        BytesTransferred: wire.Progress.HasBytesTransferred ? wire.Progress.BytesTransferred : null,
                        Error: wire.Progress.HasError ? wire.Progress.Error : null,
                        TotalBytes: wire.Progress.HasTotalBytes ? wire.Progress.TotalBytes : null),
                    null),
            _ => throw new LlmRouterModelAdminException("llm_router model sync stream sent an empty message")
        };
    }

    /// <summary>Maps the wire sync stage onto the client's enum.</summary>
    private static LlmRouterModelSyncStageInfo MapStage(Contract.LlmRouterModelSyncStage stage)
    {
        return stage switch
        {
            Contract.LlmRouterModelSyncStage.Downloading => LlmRouterModelSyncStageInfo.Downloading,
            Contract.LlmRouterModelSyncStage.Verifying => LlmRouterModelSyncStageInfo.Verifying,
            Contract.LlmRouterModelSyncStage.Completed => LlmRouterModelSyncStageInfo.Completed,
            _ => LlmRouterModelSyncStageInfo.Failed
        };
    }

    /// <inheritdoc/>
    protected override LlmRouterModelAdminException CreateException(string message, Exception? innerException,
        bool isUnavailable)
    {
        return new LlmRouterModelAdminException(message: message, innerException: innerException,
            isUnavailable: isUnavailable);
    }
}