using System.Runtime.CompilerServices;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a benchmark-data management call fails. Carries a message fit to render in the Governance
/// panel rather than a raw <see cref="RpcException"/>, mirroring <see cref="PriceSourceAdminException"/>.
/// See <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class BenchmarkDataAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="BenchmarkDataAdminException"/> class.</summary>
    public BenchmarkDataAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message: message, innerException: innerException, isUnavailable: isUnavailable)
    {
    }
}

/// <summary>The CodeRouterBench corpus's freshness relative to the published Hugging Face dataset.</summary>
public enum BenchmarkDataAdminState
{
    /// <summary>Every file has a ledger row matching its published checksum.</summary>
    Current,

    /// <summary>At least one file has never synced, or its ledger row no longer matches the published checksum.</summary>
    Update,

    /// <summary>The checksum probe could not reach Hugging Face; freshness is unknown.</summary>
    CheckFailed
}

/// <summary>One corpus file's last-recorded sync, as rendered by the Governance → Benchmark Data panel.</summary>
/// <param name="FileName">The file's name in the published Hugging Face dataset.</param>
/// <param name="Synced">Whether this file has ever synced successfully.</param>
/// <param name="SizeBytes">The synced file's byte size, or 0 if it has never synced.</param>
/// <param name="RowCount">The synced file's imported row count, or 0 if it has never synced.</param>
/// <param name="SyncedAtUtc">When this file last synced, or <see langword="null"/> if it never has.</param>
public sealed record BenchmarkFileStatusInfo(
    string FileName,
    bool Synced,
    long SizeBytes,
    int RowCount,
    DateTimeOffset? SyncedAtUtc);

/// <summary>The corpus's freshness state and every file's ledger status.</summary>
/// <param name="State">The computed freshness state.</param>
/// <param name="Reason">
/// The probe failure reason, when <paramref name="State"/> is
/// <see cref="BenchmarkDataAdminState.CheckFailed"/>; otherwise <see langword="null"/>.
/// </param>
/// <param name="CheckedAtUtc">When this status was computed.</param>
/// <param name="Files">Every corpus file's ledger status, whether synced or not.</param>
public sealed record BenchmarkDataStatusInfo(
    BenchmarkDataAdminState State,
    string? Reason,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<BenchmarkFileStatusInfo> Files);

/// <summary>The stage of one file's sync, as streamed by <see cref="IBenchmarkDataAdminClient.SyncAsync"/>.</summary>
public enum BenchmarkSyncStageInfo
{
    /// <summary>Bytes are being downloaded from Hugging Face.</summary>
    Downloading,

    /// <summary>The downloaded bytes' checksum is being verified against the published value.</summary>
    Verifying,

    /// <summary>The verified bytes are being parsed and written to the database.</summary>
    Importing,

    /// <summary>The file's sync completed successfully.</summary>
    Completed,

    /// <summary>The file's sync failed; its prior table rows and ledger entry are untouched.</summary>
    Failed
}

/// <summary>One file's progress update during a sync.</summary>
/// <param name="FileName">The file this update is about.</param>
/// <param name="Stage">The stage the file is currently in.</param>
/// <param name="BytesTransferred">Bytes downloaded so far, when known.</param>
/// <param name="RowsImported">Rows imported, once <see cref="BenchmarkSyncStageInfo.Completed"/> is reached.</param>
/// <param name="Error">Why the file failed, set only on a terminal <see cref="BenchmarkSyncStageInfo.Failed"/> update.</param>
/// <param name="TotalBytes">The file's published size in bytes, when known.</param>
public sealed record BenchmarkSyncProgressInfo(
    string FileName,
    BenchmarkSyncStageInfo Stage,
    long? BytesTransferred,
    int? RowsImported,
    string? Error,
    long? TotalBytes = null);

/// <summary>One file a sync is about to download, from the up-front <see cref="BenchmarkSyncPlanInfo"/>.</summary>
/// <param name="FileName">The file's name in the published Hugging Face dataset.</param>
/// <param name="SizeBytes">The file's published size in bytes.</param>
public sealed record BenchmarkSyncPlanFileInfo(string FileName, long SizeBytes);

/// <summary>
/// The set of files a sync will download and their combined size, reported once as the first message
/// on the stream so a cumulative progress display has a stable denominator from the first byte.
/// </summary>
/// <param name="Files">The files that will be downloaded. A file already current is not listed.</param>
/// <param name="TotalBytes">The combined published size of every file in <see cref="Files"/>.</param>
public sealed record BenchmarkSyncPlanInfo(IReadOnlyList<BenchmarkSyncPlanFileInfo> Files, long TotalBytes);

/// <summary>
/// One message on the sync stream: the up-front <see cref="Plan"/> (always first), a per-file
/// <see cref="Progress"/> update, or - exactly once, as the final message - the aggregate
/// <see cref="FinalStatus"/> after every file has been attempted. Exactly one of the three is
/// non-null, mirroring the wire contract's <c>oneof</c>.
/// </summary>
/// <param name="Plan">The sync's up-front plan, set only on the first message.</param>
/// <param name="Progress">A per-file progress update, or <see langword="null"/> for the plan/final message.</param>
/// <param name="FinalStatus">The aggregate status, set only on the final message.</param>
public sealed record BenchmarkSyncEvent(
    BenchmarkSyncPlanInfo? Plan,
    BenchmarkSyncProgressInfo? Progress,
    BenchmarkDataStatusInfo? FinalStatus);

/// <summary>
/// Client for the proxy's <c>BenchmarkDataAdminService</c> - the Governance → Benchmark Data panel's read
/// and sync surface. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project
/// so CI can unit-test it, exactly like <see cref="PriceSourceAdminClient"/>.
/// </summary>
public sealed class BenchmarkDataAdminClient
    : GrpcAdminClientBase<Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceClient,
            BenchmarkDataAdminException>,
        IBenchmarkDataAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BenchmarkDataAdminClient"/> class, creating and
    /// owning a channel to <paramref name="serverAddress"/>.
    /// </summary>
    public BenchmarkDataAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress: serverAddress,
            createClient: callInvoker =>
                new Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BenchmarkDataAdminClient"/> class over a
    /// caller-supplied generated client. The seam tests use to substitute a fake without a live server;
    /// the caller owns the channel's lifetime.
    /// </summary>
    public BenchmarkDataAdminClient(Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<BenchmarkDataStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetBenchmarkStatusAsync(request: new Contract.GetBenchmarkStatusRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the benchmark data status");
        }
    }

    /// <inheritdoc/>
    public async Task<BenchmarkDataStatusInfo> RecheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .RecheckBenchmarkDataAsync(request: new Contract.RecheckBenchmarkDataRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not recheck the benchmark data");
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<BenchmarkSyncEvent> SyncAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = Client.SyncBenchmarkData(request: new Contract.SyncBenchmarkDataRequest(),
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
                throw Wrap(ex: ex, action: "Benchmark data sync failed");
            }

            if (!hasNext) yield break;

            yield return MapEvent(stream.Current);
        }
    }

    /// <summary>Converts a gRPC-contract status response into the client's <see cref="BenchmarkDataStatusInfo"/>.</summary>
    private static BenchmarkDataStatusInfo MapStatus(Contract.BenchmarkStatusResponse response)
    {
        return new BenchmarkDataStatusInfo(
            State: MapState(response.State),
            Reason: response.HasReason ? response.Reason : null,
            CheckedAtUtc: response.CheckedAtUtc?.ToDateTimeOffset() ?? default,
            Files: [.. response.Files.Select(MapFile)]);
    }

    /// <summary>Converts a gRPC-contract file status into the client's <see cref="BenchmarkFileStatusInfo"/>.</summary>
    private static BenchmarkFileStatusInfo MapFile(Contract.BenchmarkFile file)
    {
        return new BenchmarkFileStatusInfo(
            FileName: file.FileName,
            Synced: file.Synced,
            SizeBytes: file.SizeBytes,
            RowCount: file.RowCount,
            SyncedAtUtc: file.SyncedAtUtc?.ToDateTimeOffset());
    }

    /// <summary>
    /// Converts a gRPC-contract sync stream message into the client's <see cref="BenchmarkSyncEvent"/>.
    /// Switches explicitly on every defined <see cref="Contract.BenchmarkSyncStreamEvent.EventOneofCase"/>,
    /// including <c>None</c> - an empty oneof (e.g. a stream implementation that sends a bare keepalive
    /// message) must not be misread as a progress event and dereference an unset <c>Progress</c> field.
    /// </summary>
    private static BenchmarkSyncEvent MapEvent(Contract.BenchmarkSyncStreamEvent wire)
    {
        return wire.EventCase switch
        {
            Contract.BenchmarkSyncStreamEvent.EventOneofCase.Plan =>
                new BenchmarkSyncEvent(Plan: MapPlan(wire.Plan), null, null),
            Contract.BenchmarkSyncStreamEvent.EventOneofCase.Progress =>
                new BenchmarkSyncEvent(
                    null,
                    Progress: new BenchmarkSyncProgressInfo(
                        FileName: wire.Progress.FileName,
                        Stage: MapStage(wire.Progress.Stage),
                        BytesTransferred: wire.Progress.HasBytesTransferred ? wire.Progress.BytesTransferred : null,
                        RowsImported: wire.Progress.HasRowsImported ? wire.Progress.RowsImported : null,
                        Error: wire.Progress.HasError ? wire.Progress.Error : null,
                        TotalBytes: wire.Progress.HasTotalBytes ? wire.Progress.TotalBytes : null),
                    null),
            Contract.BenchmarkSyncStreamEvent.EventOneofCase.FinalStatus =>
                new BenchmarkSyncEvent(null, null, FinalStatus: MapStatus(wire.FinalStatus)),
            _ => new BenchmarkSyncEvent(null, null, null)
        };
    }

    /// <summary>Converts a gRPC-contract plan event into the client's <see cref="BenchmarkSyncPlanInfo"/>.</summary>
    private static BenchmarkSyncPlanInfo MapPlan(Contract.BenchmarkSyncPlanEvent plan)
    {
        return new BenchmarkSyncPlanInfo(
            Files:
            [
                .. plan.Files.Select(file =>
                    new BenchmarkSyncPlanFileInfo(FileName: file.FileName, SizeBytes: file.SizeBytes))
            ],
            TotalBytes: plan.TotalBytes);
    }

    /// <summary>
    /// Maps the wire freshness state onto the client's enum. Every defined value is mapped explicitly and
    /// anything else - <c>BENCHMARK_DATA_STATE_UNSPECIFIED</c>, which the service emits for a state it
    /// cannot map, or a value added to the contract after this build - degrades to
    /// <see cref="BenchmarkDataAdminState.CheckFailed"/>: the panel turns an unknown freshness into a
    /// recheck, whereas defaulting to <see cref="BenchmarkDataAdminState.Update"/> would let the operator
    /// start a blind multi-hundred-megabyte sync off a state nobody actually asserted.
    /// </summary>
    private static BenchmarkDataAdminState MapState(Contract.BenchmarkDataState state)
    {
        return state switch
        {
            Contract.BenchmarkDataState.Current => BenchmarkDataAdminState.Current,
            Contract.BenchmarkDataState.Update => BenchmarkDataAdminState.Update,
            Contract.BenchmarkDataState.CheckFailed => BenchmarkDataAdminState.CheckFailed,
            _ => BenchmarkDataAdminState.CheckFailed
        };
    }

    /// <summary>Maps the wire sync stage onto the client's enum.</summary>
    private static BenchmarkSyncStageInfo MapStage(Contract.BenchmarkSyncStage stage)
    {
        return stage switch
        {
            Contract.BenchmarkSyncStage.Downloading => BenchmarkSyncStageInfo.Downloading,
            Contract.BenchmarkSyncStage.Verifying => BenchmarkSyncStageInfo.Verifying,
            Contract.BenchmarkSyncStage.Importing => BenchmarkSyncStageInfo.Importing,
            Contract.BenchmarkSyncStage.Completed => BenchmarkSyncStageInfo.Completed,
            _ => BenchmarkSyncStageInfo.Failed
        };
    }

    /// <inheritdoc/>
    protected override BenchmarkDataAdminException CreateException(string message, Exception? innerException,
        bool isUnavailable)
    {
        return new BenchmarkDataAdminException(message: message, innerException: innerException,
            isUnavailable: isUnavailable);
    }
}