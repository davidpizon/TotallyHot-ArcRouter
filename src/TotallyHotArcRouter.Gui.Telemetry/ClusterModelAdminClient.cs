using System.Runtime.CompilerServices;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a cluster-model management call fails. Carries a message fit to render in the Governance
/// panel rather than a raw <see cref="RpcException"/>, mirroring <see cref="BenchmarkDataAdminException"/>.
/// </summary>
public sealed class ClusterModelAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ClusterModelAdminException"/> class.</summary>
    public ClusterModelAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
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

/// <summary>The result category of one retrain, mirroring <c>ClusterTrainingResultKind</c>.</summary>
public enum ClusterRetrainResultKindInfo
{
    /// <summary>A new artifact was trained, validated, and written.</summary>
    Trained,

    /// <summary>Too few training samples were available; the prior artifact (if any) is untouched.</summary>
    Declined,

    /// <summary>A retrain was already in progress; this call was skipped rather than queued.</summary>
    AlreadyRunning,
}

/// <summary>One trained cluster, as rendered by the Governance → Cluster Model panel.</summary>
/// <param name="Size">The number of training samples assigned to this cluster.</param>
/// <param name="Name">A human-readable name for this cluster - dominant dimension and/or top TF-IDF terms.</param>
public sealed record ClusterInfo(int Size, string Name);

/// <summary>
/// The trained cluster model's status, or the honest "no artifact yet" state
/// (<see cref="ArtifactPresent"/> false, every other artifact-derived field at its default) on a fresh
/// install. <see cref="RetentionDays"/>, <see cref="MaxTranscriptRows"/>, and
/// <see cref="CurrentTranscriptRowCount"/> are always populated - they describe the transcript corpus
/// this model trains from, not the model itself.
/// </summary>
/// <param name="ArtifactPresent">Whether a trained artifact currently exists on disk.</param>
/// <param name="ChosenK">The number of clusters the k-sweep selected, or 0 if no artifact exists.</param>
/// <param name="TrainedAtUtc">When the current artifact was trained, or <see langword="null"/> if none exists.</param>
/// <param name="TrainedFrom">Human-readable provenance: source mix, the k-selection sweep's outcome, and the training date.</param>
/// <param name="Clusters">Every trained cluster's size and name, in artifact order.</param>
/// <param name="BootstrapTaskCount">The number of OOD bootstrap tasks that contributed to the current artifact.</param>
/// <param name="MemoryEntryCount">The number of live memory entries that contributed to the current artifact.</param>
/// <param name="EntriesSinceLastRetrain">Live memory entries accumulated since the current artifact's <see cref="MemoryEntryCount"/> was recorded, or every live entry if no artifact exists.</param>
/// <param name="RetentionDays">The configured transcript retention window, in days.</param>
/// <param name="MaxTranscriptRows">The configured maximum transcript row count before oldest-first eviction.</param>
/// <param name="CurrentTranscriptRowCount">The transcript table's current row count.</param>
public sealed record ClusterModelStatusInfo(
    bool ArtifactPresent,
    int ChosenK,
    DateTimeOffset? TrainedAtUtc,
    string TrainedFrom,
    IReadOnlyList<ClusterInfo> Clusters,
    int BootstrapTaskCount,
    int MemoryEntryCount,
    int EntriesSinceLastRetrain,
    int RetentionDays,
    int MaxTranscriptRows,
    int CurrentTranscriptRowCount);

/// <summary>One OOD bootstrap-embedding progress tick during a retrain.</summary>
/// <param name="TasksEmbedded">The number of OOD bootstrap tasks embedded so far.</param>
public sealed record ClusterRetrainBootstrapProgressInfo(int TasksEmbedded);

/// <summary>The retrain's outcome: its result category, a human-readable message, and the fresh status.</summary>
/// <param name="Kind">The result category.</param>
/// <param name="Message">A human-readable explanation, suitable for the panel's status line.</param>
/// <param name="Status">The status computed immediately after the retrain attempt.</param>
public sealed record ClusterRetrainResultInfo(ClusterRetrainResultKindInfo Kind, string Message, ClusterModelStatusInfo Status);

/// <summary>
/// One message on the retrain stream: a <see cref="BootstrapProgress"/> tick, or - exactly once, as the
/// final message - the <see cref="Result"/>. Exactly one of the two is non-null, mirroring the wire
/// contract's <c>oneof</c>.
/// </summary>
/// <param name="BootstrapProgress">A bootstrap-embedding progress tick, or <see langword="null"/> for the result message.</param>
/// <param name="Result">The retrain's outcome, set only on the final message.</param>
public sealed record ClusterRetrainEvent(ClusterRetrainBootstrapProgressInfo? BootstrapProgress, ClusterRetrainResultInfo? Result);

/// <summary>
/// Client for the proxy's <c>ClusterModelAdminService</c> - the Governance → Cluster Model panel's read and
/// retrain surface. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project so
/// CI can unit-test it, exactly like <see cref="BenchmarkDataAdminClient"/>.
/// </summary>
public sealed class ClusterModelAdminClient : IClusterModelAdminClient, IDisposable
{
    private readonly Contract.ClusterModelAdminService.ClusterModelAdminServiceClient _client;
    private readonly IDisposable? _ownedChannel;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterModelAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public ClusterModelAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        var channel = TelemetryChannelFactory.Create(serverAddress);
        _ownedChannel = channel;
        _client = new Contract.ClusterModelAdminService.ClusterModelAdminServiceClient(TelemetryChannelFactory.Authenticated(channel));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterModelAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public ClusterModelAdminClient(Contract.ClusterModelAdminService.ClusterModelAdminServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedChannel = null;
    }

    /// <inheritdoc />
    public async Task<ClusterModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .GetClusterModelStatusAsync(new Contract.GetClusterModelStatusRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not read the cluster model status");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ClusterRetrainEvent> RetrainAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.RetrainClusterModel(new Contract.RetrainClusterModelRequest(), cancellationToken: cancellationToken);
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
                throw Wrap(ex, "Cluster model retrain failed");
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return MapEvent(stream.Current);
        }
    }

    /// <summary>Converts a gRPC-contract status response into the client's <see cref="ClusterModelStatusInfo"/>.</summary>
    private static ClusterModelStatusInfo MapStatus(Contract.ClusterModelStatusResponse response) => new(
        response.ArtifactPresent,
        response.ChosenK,
        response.TrainedAtUtc?.ToDateTimeOffset(),
        response.TrainedFrom,
        [.. response.ClusterSizes.Zip(response.ClusterNames, (size, name) => new ClusterInfo(size, name))],
        response.BootstrapTaskCount,
        response.MemoryEntryCount,
        response.EntriesSinceLastRetrain,
        response.RetentionDays,
        response.MaxTranscriptRows,
        response.CurrentTranscriptRowCount);

    /// <summary>
    /// Converts a gRPC-contract retrain stream message into the client's <see cref="ClusterRetrainEvent"/>.
    /// Switches explicitly on every defined <see cref="Contract.ClusterRetrainStreamEvent.EventOneofCase"/>,
    /// including <c>None</c> - mirrors <c>BenchmarkDataAdminClient.MapEvent</c>'s reasoning.
    /// </summary>
    private static ClusterRetrainEvent MapEvent(Contract.ClusterRetrainStreamEvent wire) => wire.EventCase switch
    {
        Contract.ClusterRetrainStreamEvent.EventOneofCase.BootstrapProgress =>
            new ClusterRetrainEvent(
                new ClusterRetrainBootstrapProgressInfo(wire.BootstrapProgress.TasksEmbedded),
                Result: null),
        Contract.ClusterRetrainStreamEvent.EventOneofCase.Result =>
            new ClusterRetrainEvent(
                BootstrapProgress: null,
                new ClusterRetrainResultInfo(MapResultKind(wire.Result.Kind), wire.Result.Message, MapStatus(wire.Result.Status))),
        _ => new ClusterRetrainEvent(BootstrapProgress: null, Result: null),
    };

    /// <summary>
    /// Maps the wire result kind onto the client's enum. <c>CLUSTER_RETRAIN_RESULT_KIND_UNSPECIFIED</c> and
    /// any future value degrade to <see cref="ClusterRetrainResultKindInfo.Declined"/> - the panel treats an
    /// unrecognized outcome as "nothing was written" rather than implying success.
    /// </summary>
    private static ClusterRetrainResultKindInfo MapResultKind(Contract.ClusterRetrainResultKind kind) => kind switch
    {
        Contract.ClusterRetrainResultKind.Trained => ClusterRetrainResultKindInfo.Trained,
        Contract.ClusterRetrainResultKind.Declined => ClusterRetrainResultKindInfo.Declined,
        Contract.ClusterRetrainResultKind.AlreadyRunning => ClusterRetrainResultKindInfo.AlreadyRunning,
        _ => ClusterRetrainResultKindInfo.Declined,
    };

    // Unavailable means the proxy isn't running - an ordinary state for a GUI that can outlive it - so it
    // gets a plain-language message rather than a gRPC status dump, and is flagged so the caller can tell a
    // dead connection from a rejected request without parsing the text.
    /// <summary>Wraps an <see cref="RpcException"/> into a <see cref="ClusterModelAdminException"/>, flagging router-unreachable errors distinctly.</summary>
    private static ClusterModelAdminException Wrap(RpcException ex, string action) =>
        ex.StatusCode == StatusCode.Unavailable
            ? new ClusterModelAdminException($"{action}: the router is not reachable.", ex, isUnavailable: true)
            : new ClusterModelAdminException($"{action}: {ex.Status.Detail}", ex);

    /// <inheritdoc />
    public void Dispose() => _ownedChannel?.Dispose();
}
