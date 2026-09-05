using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// gRPC service backing the Governance → Regret Harness panel (docs/router/regret-evaluation-harness-plan.md
/// N6): reports the last completed run's report and re-runs the N5 comparison report with streamed coarse
/// stage progress. Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the same loopback
/// TLS endpoint as <c>TelemetryService</c> and the other admin services, unconditionally (unlike
/// <c>ClusterModelAdminGrpcService</c>/<c>LogRegModelAdminGrpcService</c>, its one dependency is never an
/// optional feature).
/// </summary>
public sealed class RegretHarnessAdminGrpcService : Contract.RegretHarnessAdminService.RegretHarnessAdminServiceBase
{
    private readonly IRegretHarnessRunner _runner;

    /// <summary>Initializes a new instance of the <see cref="RegretHarnessAdminGrpcService"/> class.</summary>
    /// <param name="runner">Reports the last run's result and performs a new run for the panel's Run button.</param>
    public RegretHarnessAdminGrpcService(IRegretHarnessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <inheritdoc/>
    public override Task<Contract.RegretHarnessStatusResponse> GetRegretHarnessStatus(
        Contract.GetRegretHarnessStatusRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(BuildStatus());
    }

    /// <inheritdoc/>
    public override async Task RunRegretHarness(
        Contract.RunRegretHarnessRequest request,
        IServerStreamWriter<Contract.RegretHarnessStreamEvent> responseStream,
        ServerCallContext context)
    {
        var progress = new StreamingStageProgress(responseStream);
        var outcome = await _runner
            .RunAsync(stageProgress: progress, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        await responseStream.WriteAsync(new Contract.RegretHarnessStreamEvent
        {
            Result = new Contract.RegretHarnessRunResult
            {
                Kind = MapResultKind(outcome.Kind),
                Message = outcome.Message,
                Splits = { outcome.Splits.Select(MapSplit) },
                RanAtUtc = outcome.RanAtUtc is { } ranAtUtc ? Timestamp.FromDateTimeOffset(ranAtUtc) : null
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Builds the current status from <see cref="IRegretHarnessRunner.LastResult"/>.</summary>
    private Contract.RegretHarnessStatusResponse BuildStatus()
    {
        var last = _runner.LastResult;
        if (last is null) return new Contract.RegretHarnessStatusResponse { HasRun = false };

        var response = new Contract.RegretHarnessStatusResponse
        {
            HasRun = true,
            Message = last.Message,
            Splits = { last.Splits.Select(MapSplit) }
        };
        if (last.RanAtUtc is { } ranAtUtc) response.RanAtUtc = Timestamp.FromDateTimeOffset(ranAtUtc);

        return response;
    }

    /// <summary>Converts one domain split report onto its wire message.</summary>
    private static Contract.RegretHarnessSplitReport MapSplit(RegretHarnessSplitReport split)
    {
        return new Contract.RegretHarnessSplitReport { SplitName = split.SplitName, MarkdownTable = split.MarkdownTable };
    }

    /// <summary>Maps the domain result kind onto its wire enum.</summary>
    private static Contract.RegretHarnessRunResultKind MapResultKind(RegretHarnessRunResultKind kind)
    {
        return kind switch
        {
            RegretHarnessRunResultKind.Completed => Contract.RegretHarnessRunResultKind.Completed,
            RegretHarnessRunResultKind.Declined => Contract.RegretHarnessRunResultKind.Declined,
            RegretHarnessRunResultKind.AlreadyRunning => Contract.RegretHarnessRunResultKind.AlreadyRunning,
            _ => Contract.RegretHarnessRunResultKind.Unspecified
        };
    }

    /// <summary>Maps the domain stage onto its wire enum.</summary>
    private static Contract.RegretHarnessStage MapStage(RegretHarnessStage stage)
    {
        return stage switch
        {
            RegretHarnessStage.LoadingCorpus => Contract.RegretHarnessStage.LoadingCorpus,
            RegretHarnessStage.TrainingLogReg => Contract.RegretHarnessStage.TrainingLogReg,
            RegretHarnessStage.BuildingKnnIndex => Contract.RegretHarnessStage.BuildingKnnIndex,
            RegretHarnessStage.BuildingOrchestratorArm => Contract.RegretHarnessStage.BuildingOrchestratorArm,
            RegretHarnessStage.BuildingReports => Contract.RegretHarnessStage.BuildingReports,
            _ => Contract.RegretHarnessStage.Unspecified
        };
    }

    /// <summary>
    /// Bridges <see cref="IRegretHarnessRunner.RunAsync"/>'s synchronous <see cref="IProgress{T}"/> stage
    /// callback onto the async gRPC response stream. Blocking on <c>WriteAsync</c> inside
    /// <see cref="Report"/> is safe for the same reason as <c>ClusterModelAdminGrpcService.StreamingBootstrapProgress</c>:
    /// ASP.NET Core Kestrel handlers run without a captured <see cref="SynchronizationContext"/>, and
    /// <see cref="RegretHarnessRunner"/> reports every stage sequentially from a single run, so writes are
    /// never issued concurrently.
    /// </summary>
    private sealed class StreamingStageProgress : IProgress<RegretHarnessStage>
    {
        private readonly IServerStreamWriter<Contract.RegretHarnessStreamEvent> _stream;

        /// <summary>Initializes a new instance of the <see cref="StreamingStageProgress"/> class.</summary>
        /// <param name="stream">The gRPC response stream to write each stage-progress event to.</param>
        public StreamingStageProgress(IServerStreamWriter<Contract.RegretHarnessStreamEvent> stream)
        {
            _stream = stream;
        }

        /// <inheritdoc/>
        public void Report(RegretHarnessStage stage)
        {
            _stream.WriteAsync(new Contract.RegretHarnessStreamEvent
            {
                StageProgress = new Contract.RegretHarnessStageProgress { Stage = MapStage(stage) }
            }).GetAwaiter().GetResult();
        }
    }
}
