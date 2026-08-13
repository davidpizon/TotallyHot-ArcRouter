using System.Runtime.CompilerServices;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a benchmark-data management call fails. Carries a message fit to render in the Governance
/// panel rather than a raw <see cref="RpcException"/>, mirroring <see cref="PriceSourceAdminException"/>.
/// </summary>
public sealed class BenchmarkDataAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="BenchmarkDataAdminException"/> class.</summary>
    public BenchmarkDataAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException)
    {
        IsUnavailable = isUnavailable;
    }

    /// <summary>
    /// Gets whether the call failed because the router could not be reached, as opposed to being rejected
    /// by it. See <see cref="PriceSourceAdminException.IsUnavailable"/>'s remarks for why this distinction
    /// is load-bearing for the caller.
    /// </summary>
    public bool IsUnavailable { get; }
}

/// <summary>The CodeRouterBench corpus's freshness relative to the published Hugging Face dataset.</summary>
public enum BenchmarkDataAdminState
{
    /// <summary>Every file has a ledger row matching its published checksum.</summary>
    Current,

    /// <summary>At least one file has never synced, or its ledger row no longer matches the published checksum.</summary>
    Update,

    /// <summary>The checksum probe could not reach Hugging Face; freshness is unknown.</summary>
    CheckFailed,
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
/// <param name="Reason">The probe failure reason, when <paramref name="State"/> is <see cref="BenchmarkDataAdminState.CheckFailed"/>; otherwise <see langword="null"/>.</param>
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
    Failed,
}

/// <summary>One file's progress update during a sync.</summary>
/// <param name="FileName">The file this update is about.</param>
/// <param name="Stage">The stage the file is currently in.</param>
/// <param name="BytesTransferred">Bytes downloaded so far, when known.</param>
/// <param name="RowsImported">Rows imported, once <see cref="BenchmarkSyncStageInfo.Completed"/> is reached.</param>
/// <param name="Error">Why the file failed, set only on a terminal <see cref="BenchmarkSyncStageInfo.Failed"/> update.</param>
public sealed record BenchmarkSyncProgressInfo(
    string FileName,
    BenchmarkSyncStageInfo Stage,
    long? BytesTransferred,
    int? RowsImported,
    string? Error);

/// <summary>
/// One message on the sync stream: either a per-file <see cref="Progress"/> update, or - exactly once, as
/// the final message - the aggregate <see cref="FinalStatus"/> after every file has been attempted.
/// Exactly one of the two is non-null, mirroring the wire contract's <c>oneof</c>.
/// </summary>
/// <param name="Progress">A per-file progress update, or <see langword="null"/> for the final message.</param>
/// <param name="FinalStatus">The aggregate status, set only on the final message.</param>
public sealed record BenchmarkSyncEvent(BenchmarkSyncProgressInfo? Progress, BenchmarkDataStatusInfo? FinalStatus);

/// <summary>
/// Client for the proxy's <c>BenchmarkDataAdminService</c> - the Governance → Benchmark Data panel's read
/// and sync surface. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project
/// so CI can unit-test it, exactly like <see cref="PriceSourceAdminClient"/>.
/// </summary>
public sealed class BenchmarkDataAdminClient : IBenchmarkDataAdminClient, IDisposable
{
    private readonly Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceClient _client;
    private readonly IDisposable? _ownedChannel;

    /// <summary>
    /// Initializes a new instance of the <see cref="BenchmarkDataAdminClient"/> class, creating and
    /// owning a channel to <paramref name="serverAddress"/>.
    /// </summary>
    public BenchmarkDataAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        var channel = TelemetryChannelFactory.Create(serverAddress);
        _ownedChannel = channel;
        _client = new Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceClient(TelemetryChannelFactory.Authenticated(channel));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BenchmarkDataAdminClient"/> class over a
    /// caller-supplied generated client. The seam tests use to substitute a fake without a live server;
    /// the caller owns the channel's lifetime.
    /// </summary>
    public BenchmarkDataAdminClient(Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedChannel = null;
    }

    /// <inheritdoc />
    public async Task<BenchmarkDataStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .GetBenchmarkStatusAsync(new Contract.GetBenchmarkStatusRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not read the benchmark data status");
        }
    }

    /// <inheritdoc />
    public async Task<BenchmarkDataStatusInfo> RecheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .RecheckBenchmarkDataAsync(new Contract.RecheckBenchmarkDataRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not recheck the benchmark data");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BenchmarkSyncEvent> SyncAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.SyncBenchmarkData(new Contract.SyncBenchmarkDataRequest(), cancellationToken: cancellationToken);
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
                throw Wrap(ex, "Benchmark data sync failed");
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return MapEvent(stream.Current);
        }
    }

    /// <summary>Converts a gRPC-contract status response into the client's <see cref="BenchmarkDataStatusInfo"/>.</summary>
    private static BenchmarkDataStatusInfo MapStatus(Contract.BenchmarkStatusResponse response) => new(
        MapState(response.State),
        response.HasReason ? response.Reason : null,
        response.CheckedAtUtc?.ToDateTimeOffset() ?? default,
        [.. response.Files.Select(MapFile)]);

    /// <summary>Converts a gRPC-contract file status into the client's <see cref="BenchmarkFileStatusInfo"/>.</summary>
    private static BenchmarkFileStatusInfo MapFile(Contract.BenchmarkFile file) => new(
        file.FileName,
        file.Synced,
        file.SizeBytes,
        file.RowCount,
        file.SyncedAtUtc?.ToDateTimeOffset());

    /// <summary>Converts a gRPC-contract sync stream message into the client's <see cref="BenchmarkSyncEvent"/>.</summary>
    private static BenchmarkSyncEvent MapEvent(Contract.BenchmarkSyncStreamEvent wire) =>
        wire.EventCase == Contract.BenchmarkSyncStreamEvent.EventOneofCase.FinalStatus
            ? new BenchmarkSyncEvent(Progress: null, FinalStatus: MapStatus(wire.FinalStatus))
            : new BenchmarkSyncEvent(
                Progress: new BenchmarkSyncProgressInfo(
                    wire.Progress.FileName,
                    MapStage(wire.Progress.Stage),
                    wire.Progress.HasBytesTransferred ? wire.Progress.BytesTransferred : null,
                    wire.Progress.HasRowsImported ? wire.Progress.RowsImported : null,
                    wire.Progress.HasError ? wire.Progress.Error : null),
                FinalStatus: null);

    /// <summary>Maps the wire freshness state onto the client's enum.</summary>
    private static BenchmarkDataAdminState MapState(Contract.BenchmarkDataState state) => state switch
    {
        Contract.BenchmarkDataState.Current => BenchmarkDataAdminState.Current,
        Contract.BenchmarkDataState.CheckFailed => BenchmarkDataAdminState.CheckFailed,
        _ => BenchmarkDataAdminState.Update,
    };

    /// <summary>Maps the wire sync stage onto the client's enum.</summary>
    private static BenchmarkSyncStageInfo MapStage(Contract.BenchmarkSyncStage stage) => stage switch
    {
        Contract.BenchmarkSyncStage.Downloading => BenchmarkSyncStageInfo.Downloading,
        Contract.BenchmarkSyncStage.Verifying => BenchmarkSyncStageInfo.Verifying,
        Contract.BenchmarkSyncStage.Importing => BenchmarkSyncStageInfo.Importing,
        Contract.BenchmarkSyncStage.Completed => BenchmarkSyncStageInfo.Completed,
        _ => BenchmarkSyncStageInfo.Failed,
    };

    // Unavailable means the proxy isn't running - an ordinary state for a GUI that can outlive it - so it
    // gets a plain-language message rather than a gRPC status dump, and is flagged so the caller can tell
    // a dead connection from a rejected request without parsing the text.
    /// <summary>Wraps an <see cref="RpcException"/> into a <see cref="BenchmarkDataAdminException"/>, flagging router-unreachable errors distinctly.</summary>
    private static BenchmarkDataAdminException Wrap(RpcException ex, string action) =>
        ex.StatusCode == StatusCode.Unavailable
            ? new BenchmarkDataAdminException($"{action}: the router is not reachable.", ex, isUnavailable: true)
            : new BenchmarkDataAdminException($"{action}: {ex.Status.Detail}", ex);

    /// <inheritdoc />
    public void Dispose() => _ownedChannel?.Dispose();
}
