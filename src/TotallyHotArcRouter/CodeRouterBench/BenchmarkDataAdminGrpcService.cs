using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// gRPC service backing the Governance → Benchmark Data panel: reports the CodeRouterBench corpus's
/// freshness (docs/router/coderouterbench-sqlite-migration-plan.md, Phase 4), forces a fresh checksum
/// probe, and runs a sync with streamed per-file progress. Mapped by
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the same loopback TLS endpoint as
/// <c>TelemetryService</c> and <c>PriceSourceAdminService</c>.
/// </summary>
public sealed class BenchmarkDataAdminGrpcService : Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceBase
{
    private readonly BenchmarkDataStatusService _statusService;
    private readonly BenchmarkFileLedger _ledger;
    private readonly BenchmarkSyncService _syncService;
    private readonly BenchmarkSyncOptions _options;

    /// <summary>Initializes a new instance of the <see cref="BenchmarkDataAdminGrpcService"/> class.</summary>
    public BenchmarkDataAdminGrpcService(
        BenchmarkDataStatusService statusService,
        BenchmarkFileLedger ledger,
        BenchmarkSyncService syncService,
        IOptions<BenchmarkSyncOptions> options)
    {
        ArgumentNullException.ThrowIfNull(statusService);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(syncService);
        ArgumentNullException.ThrowIfNull(options);

        _statusService = statusService;
        _ledger = ledger;
        _syncService = syncService;
        _options = options.Value;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the cached status computed by <c>TotallyHot.ArcRouter.Hosting.StartupHealthCheckHostedService</c>'s startup probe.
    /// On the rare path where no probe has ever completed (the panel opened before startup's own probe
    /// finished, or that probe threw an unexpected exception the outer try/catch swallowed), this runs one
    /// itself rather than answering with an undefined state - the panel always has something to render.
    /// </remarks>
    public override async Task<Contract.BenchmarkStatusResponse> GetBenchmarkStatus(
        Contract.GetBenchmarkStatusRequest request,
        ServerCallContext context)
    {
        var status = _statusService.Current
            ?? await _statusService.RecheckAsync(context.CancellationToken).ConfigureAwait(false);
        return BuildStatusResponse(status);
    }

    /// <inheritdoc />
    public override async Task<Contract.BenchmarkStatusResponse> RecheckBenchmarkData(
        Contract.RecheckBenchmarkDataRequest request,
        ServerCallContext context)
    {
        var status = await _statusService.RecheckAsync(context.CancellationToken).ConfigureAwait(false);
        return BuildStatusResponse(status);
    }

    /// <inheritdoc />
    public override async Task SyncBenchmarkData(
        Contract.SyncBenchmarkDataRequest request,
        IServerStreamWriter<Contract.BenchmarkSyncStreamEvent> responseStream,
        ServerCallContext context)
    {
        var progress = new StreamingSyncProgress(responseStream);
        var planProgress = new StreamingSyncPlan(responseStream);
        var result = await _syncService
            .SyncAsync(_options.DatasetRef, progress, context.CancellationToken, planProgress)
            .ConfigureAwait(false);

        // The plain per-file progress events above report a Failed stage but carry no error text
        // (BenchmarkSyncProgress has none - only the terminal BenchmarkSyncResult does). Send one
        // supplemental event per failed file with the reason, so the panel can render it without a
        // second call. Skipped files never failed - they simply weren't downloaded - so they are
        // excluded here the same way a succeeded file is.
        foreach (var outcome in result.Files.Where(f => !f.Succeeded && !f.Skipped))
        {
            await responseStream.WriteAsync(new Contract.BenchmarkSyncStreamEvent
            {
                Progress = new Contract.BenchmarkSyncProgressEvent
                {
                    FileName = outcome.FileName,
                    Stage = Contract.BenchmarkSyncStage.Failed,
                    Error = outcome.ErrorMessage ?? "Sync failed.",
                },
            }).ConfigureAwait(false);
        }

        // Re-probes rather than deriving the state from `result`: the sync itself doesn't know whether
        // its just-written ledger rows now match the published tree (a race with a concurrent upstream
        // publish is possible, however unlikely), and this mirrors RefreshPriceSourcesResponse's
        // convention of reporting the true post-mutation state rather than an inferred one.
        var finalStatus = await _statusService.RecheckAsync(context.CancellationToken).ConfigureAwait(false);
        await responseStream.WriteAsync(new Contract.BenchmarkSyncStreamEvent
        {
            FinalStatus = BuildStatusResponse(finalStatus),
        }).ConfigureAwait(false);
    }

    /// <summary>Builds the wire status response from a domain status, including every file's ledger entry.</summary>
    private Contract.BenchmarkStatusResponse BuildStatusResponse(BenchmarkDataStatus status)
    {
        var response = new Contract.BenchmarkStatusResponse
        {
            State = MapState(status.State),
            CheckedAtUtc = Timestamp.FromDateTimeOffset(status.CheckedAtUtc),
        };

        if (status.Reason is not null)
        {
            response.Reason = status.Reason;
        }

        response.Files.AddRange(BuildFiles());
        return response;
    }

    /// <summary>
    /// Projects every file named in <see cref="BenchmarkFileSpec.All"/> into a wire file status, so a file
    /// that has never synced is still listed (with <c>synced = false</c>) - the same convention
    /// <c>ListPriceSources</c> uses for a disabled source: the panel needs a card for it regardless.
    /// </summary>
    private IEnumerable<Contract.BenchmarkFile> BuildFiles()
    {
        var entries = _ledger.GetAll().ToDictionary(entry => entry.FileName, StringComparer.Ordinal);

        foreach (var spec in BenchmarkFileSpec.All)
        {
            if (entries.TryGetValue(spec.FileName, out var entry))
            {
                yield return new Contract.BenchmarkFile
                {
                    FileName = spec.FileName,
                    Synced = true,
                    SizeBytes = entry.SizeBytes,
                    RowCount = entry.RowCount,
                    SyncedAtUtc = Timestamp.FromDateTimeOffset(entry.SyncedAtUtc),
                };
            }
            else
            {
                yield return new Contract.BenchmarkFile
                {
                    FileName = spec.FileName,
                    Synced = false,
                };
            }
        }
    }

    /// <summary>Maps the domain freshness state onto its wire enum.</summary>
    private static Contract.BenchmarkDataState MapState(BenchmarkDataState state) => state switch
    {
        BenchmarkDataState.Current => Contract.BenchmarkDataState.Current,
        BenchmarkDataState.Update => Contract.BenchmarkDataState.Update,
        BenchmarkDataState.CheckFailed => Contract.BenchmarkDataState.CheckFailed,
        _ => Contract.BenchmarkDataState.Unspecified,
    };

    /// <summary>
    /// Bridges <see cref="BenchmarkSyncService"/>'s synchronous <see cref="IProgress{T}"/> callback onto
    /// the async gRPC response stream. Blocking on <c>WriteAsync</c> inside
    /// <see cref="Report"/> is safe here: ASP.NET Core Kestrel handlers run without a captured
    /// <see cref="SynchronizationContext"/>, so blocking cannot deadlock on a resumption context, and
    /// <see cref="BenchmarkSyncService.SyncAsync"/> issues every <see cref="IProgress{T}.Report"/> call
    /// sequentially from a single loop on one thread - each call returns before the next begins, so writes
    /// are never issued concurrently, which is <see cref="IServerStreamWriter{T}"/>'s one hard constraint.
    /// (Note that the calls are sequential, not necessarily separated by an <c>await</c>: some are
    /// back-to-back around synchronous work such as the import step.)
    /// </summary>
    private sealed class StreamingSyncProgress : IProgress<BenchmarkSyncProgress>
    {
        private readonly IServerStreamWriter<Contract.BenchmarkSyncStreamEvent> _stream;

        /// <summary>Initializes a new instance of the <see cref="StreamingSyncProgress"/> class.</summary>
        /// <param name="stream">The gRPC response stream to write each progress event to.</param>
        public StreamingSyncProgress(IServerStreamWriter<Contract.BenchmarkSyncStreamEvent> stream) => _stream = stream;

        /// <inheritdoc/>
        public void Report(BenchmarkSyncProgress value)
        {
            var wire = new Contract.BenchmarkSyncProgressEvent
            {
                FileName = value.FileName,
                Stage = MapStage(value.Stage),
            };

            if (value.BytesTransferred is long bytes)
            {
                wire.BytesTransferred = bytes;
            }

            if (value.RowsImported is int rows)
            {
                wire.RowsImported = rows;
            }

            if (value.TotalBytes is long totalBytes)
            {
                wire.TotalBytes = totalBytes;
            }

            _stream.WriteAsync(new Contract.BenchmarkSyncStreamEvent { Progress = wire }).GetAwaiter().GetResult();
        }

        /// <summary>Maps the domain sync stage onto its wire enum.</summary>
        private static Contract.BenchmarkSyncStage MapStage(BenchmarkSyncStage stage) => stage switch
        {
            BenchmarkSyncStage.Downloading => Contract.BenchmarkSyncStage.Downloading,
            BenchmarkSyncStage.Verifying => Contract.BenchmarkSyncStage.Verifying,
            BenchmarkSyncStage.Importing => Contract.BenchmarkSyncStage.Importing,
            BenchmarkSyncStage.Completed => Contract.BenchmarkSyncStage.Completed,
            BenchmarkSyncStage.Failed => Contract.BenchmarkSyncStage.Failed,
            _ => Contract.BenchmarkSyncStage.Unspecified,
        };
    }

    /// <summary>
    /// Bridges <see cref="BenchmarkSyncService"/>'s one-time plan callback onto the response stream,
    /// writing it as the very first message on a successful sync - before any
    /// <see cref="StreamingSyncProgress"/> event - so the panel's cumulative progress bar has its
    /// denominator before the first byte arrives. Same single-threaded, blocking-write safety argument
    /// as <see cref="StreamingSyncProgress"/>.
    /// </summary>
    private sealed class StreamingSyncPlan : IProgress<BenchmarkSyncPlan>
    {
        private readonly IServerStreamWriter<Contract.BenchmarkSyncStreamEvent> _stream;

        /// <summary>Initializes a new instance of the <see cref="StreamingSyncPlan"/> class.</summary>
        /// <param name="stream">The gRPC response stream to write the plan event to.</param>
        public StreamingSyncPlan(IServerStreamWriter<Contract.BenchmarkSyncStreamEvent> stream) => _stream = stream;

        /// <inheritdoc/>
        public void Report(BenchmarkSyncPlan value)
        {
            var wire = new Contract.BenchmarkSyncPlanEvent { TotalBytes = value.TotalBytes };
            wire.Files.AddRange(value.Files.Select(file => new Contract.BenchmarkSyncPlanFile
            {
                FileName = file.FileName,
                SizeBytes = file.SizeBytes,
            }));

            _stream.WriteAsync(new Contract.BenchmarkSyncStreamEvent { Plan = wire }).GetAwaiter().GetResult();
        }
    }
}
