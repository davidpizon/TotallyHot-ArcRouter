using Grpc.Core;
using System.Runtime.CompilerServices;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a regret-harness call fails. Carries a message fit to render in the Governance panel
/// rather than a raw <see cref="RpcException"/>, mirroring <see cref="LogRegModelAdminException"/>. See
/// <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class RegretHarnessAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="RegretHarnessAdminException"/> class.</summary>
    public RegretHarnessAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message: message, innerException: innerException, isUnavailable: isUnavailable)
    {
    }
}

/// <summary>The result category of one run, mirroring <c>RegretHarnessRunResultKind</c>.</summary>
public enum RegretHarnessRunResultKindInfo
{
    /// <summary>The full comparison report was built for both splits.</summary>
    Completed,

    /// <summary>The synced corpus was not ready; no report was built.</summary>
    Declined,

    /// <summary>A run was already in progress; this call was skipped rather than queued.</summary>
    AlreadyRunning
}

/// <summary>A coarse stage of a run, mirroring <c>RegretHarnessStage</c>.</summary>
public enum RegretHarnessStageInfo
{
    /// <summary>Reading the probing/OOD/ID-test outcome rows and the probing-split prior from the corpus.</summary>
    LoadingCorpus,

    /// <summary>Training the standalone TF-IDF <c>logreg</c> baseline.</summary>
    TrainingLogReg,

    /// <summary>Embedding the OOD split to build the kNN retrieval index.</summary>
    BuildingKnnIndex,

    /// <summary>Building the isolated, two-voter Orchestrator arm.</summary>
    BuildingOrchestratorArm,

    /// <summary>Replaying every baseline and the Orchestrator arm over both splits and formatting the report.</summary>
    BuildingReports
}

/// <summary>One split's formatted comparison report.</summary>
/// <param name="SplitName">The split's name (e.g. <c>"ID test"</c> or <c>"OOD"</c>).</param>
/// <param name="MarkdownTable">The split's report, formatted as a Markdown table.</param>
public sealed record RegretHarnessSplitReportInfo(string SplitName, string MarkdownTable);

/// <summary>
/// The last completed run's report, or the honest "no run yet this process" state
/// (<see cref="HasRun"/> false, every other field at its default).
/// </summary>
/// <param name="HasRun">Whether any run has completed since the router process started.</param>
/// <param name="RanAtUtc">When the last run completed, or <see langword="null"/> if <paramref name="HasRun"/> is <see langword="false"/>.</param>
/// <param name="Message">The last run's human-readable outcome message.</param>
/// <param name="Splits">The last run's formatted report for each split.</param>
public sealed record RegretHarnessStatusInfo(
    bool HasRun,
    DateTimeOffset? RanAtUtc,
    string? Message,
    IReadOnlyList<RegretHarnessSplitReportInfo> Splits);

/// <summary>The run's outcome: its result category, a human-readable message, when it completed, and the formatted report for each split.</summary>
/// <param name="Kind">The result category.</param>
/// <param name="Message">A human-readable explanation, suitable for the panel's status line.</param>
/// <param name="RanAtUtc">When this run completed, or <see langword="null"/> unless <paramref name="Kind"/> is <see cref="RegretHarnessRunResultKindInfo.Completed"/>.</param>
/// <param name="Splits">The formatted report for each split, empty unless <paramref name="Kind"/> is <see cref="RegretHarnessRunResultKindInfo.Completed"/>.</param>
public sealed record RegretHarnessRunResultInfo(
    RegretHarnessRunResultKindInfo Kind,
    string Message,
    DateTimeOffset? RanAtUtc,
    IReadOnlyList<RegretHarnessSplitReportInfo> Splits);

/// <summary>
/// One message on the run stream: a <see cref="StageProgress"/> tick, or - exactly once, as the final
/// message - the <see cref="Result"/>. Exactly one of the two is non-null, mirroring the wire contract's
/// <c>oneof</c>.
/// </summary>
/// <param name="StageProgress">A coarse stage-progress tick, or <see langword="null"/> for the result message.</param>
/// <param name="Result">The run's outcome, set only on the final message.</param>
public sealed record RegretHarnessRunEvent(RegretHarnessStageInfo? StageProgress, RegretHarnessRunResultInfo? Result);

/// <summary>
/// Client for the proxy's <c>RegretHarnessAdminService</c> - the Governance → Regret Harness panel's read
/// and run surface. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project
/// so CI can unit-test it, exactly like <see cref="LogRegModelAdminClient"/>.
/// </summary>
public sealed class RegretHarnessAdminClient
    : GrpcAdminClientBase<Contract.RegretHarnessAdminService.RegretHarnessAdminServiceClient, RegretHarnessAdminException>,
        IRegretHarnessAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegretHarnessAdminClient"/> class, creating and owning
    /// a channel to <paramref name="serverAddress"/>.
    /// </summary>
    public RegretHarnessAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress: serverAddress,
            createClient: callInvoker =>
                new Contract.RegretHarnessAdminService.RegretHarnessAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RegretHarnessAdminClient"/> class over a
    /// caller-supplied generated client. The seam tests use to substitute a fake without a live server;
    /// the caller owns the channel's lifetime.
    /// </summary>
    public RegretHarnessAdminClient(Contract.RegretHarnessAdminService.RegretHarnessAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<RegretHarnessStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetRegretHarnessStatusAsync(request: new Contract.GetRegretHarnessStatusRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RegretHarnessStatusInfo(
                HasRun: response.HasRun,
                RanAtUtc: response.HasRun ? response.RanAtUtc?.ToDateTimeOffset() : null,
                Message: response.HasRun ? response.Message : null,
                Splits: [.. response.Splits.Select(MapSplit)]);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the regret harness status");
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RegretHarnessRunEvent> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = Client.RunRegretHarness(request: new Contract.RunRegretHarnessRequest(),
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
                throw Wrap(ex: ex, action: "Regret harness run failed");
            }

            if (!hasNext) yield break;

            yield return MapEvent(stream.Current);
        }
    }

    /// <summary>Converts a gRPC-contract split report into the client's <see cref="RegretHarnessSplitReportInfo"/>.</summary>
    private static RegretHarnessSplitReportInfo MapSplit(Contract.RegretHarnessSplitReport split)
    {
        return new RegretHarnessSplitReportInfo(SplitName: split.SplitName, MarkdownTable: split.MarkdownTable);
    }

    /// <summary>
    /// Converts a gRPC-contract run stream message into the client's <see cref="RegretHarnessRunEvent"/>.
    /// Switches explicitly on every defined <see cref="Contract.RegretHarnessStreamEvent.EventOneofCase"/>,
    /// including <c>None</c> - mirrors <c>LogRegModelAdminClient.MapEvent</c>'s reasoning.
    /// </summary>
    private static RegretHarnessRunEvent MapEvent(Contract.RegretHarnessStreamEvent wire)
    {
        return wire.EventCase switch
        {
            Contract.RegretHarnessStreamEvent.EventOneofCase.StageProgress =>
                new RegretHarnessRunEvent(StageProgress: MapStage(wire.StageProgress.Stage), null),
            Contract.RegretHarnessStreamEvent.EventOneofCase.Result =>
                new RegretHarnessRunEvent(
                    null,
                    Result: new RegretHarnessRunResultInfo(
                        Kind: MapResultKind(wire.Result.Kind),
                        Message: wire.Result.Message,
                        RanAtUtc: wire.Result.Kind == Contract.RegretHarnessRunResultKind.Completed
                            ? wire.Result.RanAtUtc?.ToDateTimeOffset()
                            : null,
                        Splits: [.. wire.Result.Splits.Select(MapSplit)])),
            _ => new RegretHarnessRunEvent(null, null)
        };
    }

    /// <summary>
    /// Maps the wire result kind onto the client's enum. <c>REGRET_HARNESS_RUN_RESULT_KIND_UNSPECIFIED</c>
    /// and any future value degrade to <see cref="RegretHarnessRunResultKindInfo.Declined"/> - the panel
    /// treats an unrecognized outcome as "nothing was produced" rather than implying success.
    /// </summary>
    private static RegretHarnessRunResultKindInfo MapResultKind(Contract.RegretHarnessRunResultKind kind)
    {
        return kind switch
        {
            Contract.RegretHarnessRunResultKind.Completed => RegretHarnessRunResultKindInfo.Completed,
            Contract.RegretHarnessRunResultKind.Declined => RegretHarnessRunResultKindInfo.Declined,
            Contract.RegretHarnessRunResultKind.AlreadyRunning => RegretHarnessRunResultKindInfo.AlreadyRunning,
            _ => RegretHarnessRunResultKindInfo.Declined
        };
    }

    /// <summary>Maps the wire stage onto the client's enum.</summary>
    private static RegretHarnessStageInfo MapStage(Contract.RegretHarnessStage stage)
    {
        return stage switch
        {
            Contract.RegretHarnessStage.LoadingCorpus => RegretHarnessStageInfo.LoadingCorpus,
            Contract.RegretHarnessStage.TrainingLogReg => RegretHarnessStageInfo.TrainingLogReg,
            Contract.RegretHarnessStage.BuildingKnnIndex => RegretHarnessStageInfo.BuildingKnnIndex,
            Contract.RegretHarnessStage.BuildingOrchestratorArm => RegretHarnessStageInfo.BuildingOrchestratorArm,
            _ => RegretHarnessStageInfo.BuildingReports
        };
    }

    /// <inheritdoc/>
    protected override RegretHarnessAdminException CreateException(string message, Exception? innerException,
        bool isUnavailable)
    {
        return new RegretHarnessAdminException(message: message, innerException: innerException,
            isUnavailable: isUnavailable);
    }
}
