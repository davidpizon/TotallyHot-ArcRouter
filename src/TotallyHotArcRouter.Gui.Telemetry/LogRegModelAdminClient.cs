using System.Runtime.CompilerServices;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a logreg-model management call fails. Carries a message fit to render in the Governance
/// panel rather than a raw <see cref="RpcException"/>, mirroring <see cref="ClusterModelAdminException"/>.
/// See <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class LogRegModelAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="LogRegModelAdminException"/> class.</summary>
    public LogRegModelAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException, isUnavailable)
    {
    }
}

/// <summary>The result category of one retrain, mirroring <c>LogRegTrainingResultKind</c>.</summary>
public enum LogRegRetrainResultKindInfo
{
    /// <summary>A new artifact was trained, validated, and written.</summary>
    Trained,

    /// <summary>Too few training samples were available; the prior artifact (if any) is untouched.</summary>
    Declined,

    /// <summary>A retrain was already in progress; this call was skipped rather than queued.</summary>
    AlreadyRunning,
}

/// <summary>
/// The trained logreg model's status, or the honest "no artifact yet" state
/// (<see cref="ArtifactPresent"/> false, every other artifact-derived field at its default) on a fresh
/// install. <see cref="RetrainThreshold"/> and <see cref="LiveSampleWeight"/> are always populated - they
/// describe the retrain configuration, not the model itself.
/// </summary>
/// <param name="ArtifactPresent">Whether a trained artifact currently exists on disk.</param>
/// <param name="EmbeddingDimension">The embedding dimension the current artifact was trained at, or 0 if no artifact exists.</param>
/// <param name="TrainedAtUtc">The artifact file's on-disk last-write time, or <see langword="null"/> if none exists.</param>
/// <param name="TrainedFrom">Human-readable provenance: source mix, sample counts, and the training date.</param>
/// <param name="BootstrapTaskCount">The number of OOD bootstrap tasks that contributed to the current artifact.</param>
/// <param name="MemoryEntryCount">The number of live memory entries that contributed to the current artifact.</param>
/// <param name="ModelsRepresented">The number of distinct models with trained weights in the current artifact.</param>
/// <param name="EntriesSinceLastRetrain">Live memory entries accumulated since the current artifact's <see cref="MemoryEntryCount"/> was recorded, or every live entry if no artifact exists.</param>
/// <param name="RetrainThreshold">The configured live memory entry count that triggers an automatic retrain.</param>
/// <param name="LiveSampleWeight">The configured weight applied to live samples relative to bootstrap samples during training.</param>
public sealed record LogRegModelStatusInfo(
    bool ArtifactPresent,
    int EmbeddingDimension,
    DateTimeOffset? TrainedAtUtc,
    string TrainedFrom,
    int BootstrapTaskCount,
    int MemoryEntryCount,
    int ModelsRepresented,
    int EntriesSinceLastRetrain,
    int RetrainThreshold,
    double LiveSampleWeight);

/// <summary>One OOD bootstrap-embedding progress tick during a retrain.</summary>
/// <param name="TasksEmbedded">The number of OOD bootstrap tasks embedded so far.</param>
public sealed record LogRegRetrainBootstrapProgressInfo(int TasksEmbedded);

/// <summary>The retrain's outcome: its result category, a human-readable message, and the fresh status.</summary>
/// <param name="Kind">The result category.</param>
/// <param name="Message">A human-readable explanation, suitable for the panel's status line.</param>
/// <param name="Status">The status computed immediately after the retrain attempt.</param>
public sealed record LogRegRetrainResultInfo(LogRegRetrainResultKindInfo Kind, string Message, LogRegModelStatusInfo Status);

/// <summary>
/// One message on the retrain stream: a <see cref="BootstrapProgress"/> tick, or - exactly once, as the
/// final message - the <see cref="Result"/>. Exactly one of the two is non-null, mirroring the wire
/// contract's <c>oneof</c>.
/// </summary>
/// <param name="BootstrapProgress">A bootstrap-embedding progress tick, or <see langword="null"/> for the result message.</param>
/// <param name="Result">The retrain's outcome, set only on the final message.</param>
public sealed record LogRegRetrainEvent(LogRegRetrainBootstrapProgressInfo? BootstrapProgress, LogRegRetrainResultInfo? Result);

/// <summary>
/// Client for the proxy's <c>RouterModelAdminService</c> - the Governance → Router Model panel's read and
/// retrain surface. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project so
/// CI can unit-test it, exactly like <see cref="ClusterModelAdminClient"/>.
/// </summary>
public sealed class LogRegModelAdminClient
    : GrpcAdminClientBase<Contract.RouterModelAdminService.RouterModelAdminServiceClient, LogRegModelAdminException>,
      ILogRegModelAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LogRegModelAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public LogRegModelAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress, callInvoker => new Contract.RouterModelAdminService.RouterModelAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogRegModelAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public LogRegModelAdminClient(Contract.RouterModelAdminService.RouterModelAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc />
    public async Task<LogRegModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetLogRegModelStatusAsync(new Contract.GetLogRegModelStatusRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not read the logreg model status");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LogRegRetrainEvent> RetrainAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = Client.RetrainLogRegModel(new Contract.RetrainLogRegModelRequest(), cancellationToken: cancellationToken);
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
                throw Wrap(ex, "Logreg model retrain failed");
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return MapEvent(stream.Current);
        }
    }

    /// <summary>Converts a gRPC-contract status response into the client's <see cref="LogRegModelStatusInfo"/>.</summary>
    private static LogRegModelStatusInfo MapStatus(Contract.LogRegModelStatusResponse response) => new(
        response.ArtifactPresent,
        response.EmbeddingDimension,
        response.TrainedAtUtc?.ToDateTimeOffset(),
        response.TrainedFrom,
        response.BootstrapTaskCount,
        response.MemoryEntryCount,
        response.ModelsRepresented,
        response.EntriesSinceLastRetrain,
        response.RetrainThreshold,
        response.LiveSampleWeight);

    /// <summary>
    /// Converts a gRPC-contract retrain stream message into the client's <see cref="LogRegRetrainEvent"/>.
    /// Switches explicitly on every defined <see cref="Contract.LogRegRetrainStreamEvent.EventOneofCase"/>,
    /// including <c>None</c> - mirrors <c>ClusterModelAdminClient.MapEvent</c>'s reasoning.
    /// </summary>
    private static LogRegRetrainEvent MapEvent(Contract.LogRegRetrainStreamEvent wire) => wire.EventCase switch
    {
        Contract.LogRegRetrainStreamEvent.EventOneofCase.BootstrapProgress =>
            new LogRegRetrainEvent(
                new LogRegRetrainBootstrapProgressInfo(wire.BootstrapProgress.TasksEmbedded),
                Result: null),
        Contract.LogRegRetrainStreamEvent.EventOneofCase.Result =>
            new LogRegRetrainEvent(
                BootstrapProgress: null,
                new LogRegRetrainResultInfo(MapResultKind(wire.Result.Kind), wire.Result.Message, MapStatus(wire.Result.Status))),
        _ => new LogRegRetrainEvent(BootstrapProgress: null, Result: null),
    };

    /// <summary>
    /// Maps the wire result kind onto the client's enum. <c>LOG_REG_RETRAIN_RESULT_KIND_UNSPECIFIED</c> and
    /// any future value degrade to <see cref="LogRegRetrainResultKindInfo.Declined"/> - the panel treats an
    /// unrecognized outcome as "nothing was written" rather than implying success.
    /// </summary>
    private static LogRegRetrainResultKindInfo MapResultKind(Contract.LogRegRetrainResultKind kind) => kind switch
    {
        Contract.LogRegRetrainResultKind.Trained => LogRegRetrainResultKindInfo.Trained,
        Contract.LogRegRetrainResultKind.Declined => LogRegRetrainResultKindInfo.Declined,
        Contract.LogRegRetrainResultKind.AlreadyRunning => LogRegRetrainResultKindInfo.AlreadyRunning,
        _ => LogRegRetrainResultKindInfo.Declined,
    };

    /// <inheritdoc />
    protected override LogRegModelAdminException CreateException(string message, Exception? innerException, bool isUnavailable) =>
        new(message, innerException, isUnavailable);
}
