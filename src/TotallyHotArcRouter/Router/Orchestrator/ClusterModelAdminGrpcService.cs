using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// gRPC service backing the Governance → Cluster Model panel
/// (docs/router/self-organizing-classification-plan.md Phase T5): reports the trained self-organizing
/// cluster model's status and runs a retrain with streamed bootstrap-embedding progress. Mapped by
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the same loopback TLS endpoint as
/// <c>TelemetryService</c> and the other admin services.
/// </summary>
public sealed class ClusterModelAdminGrpcService : Contract.ClusterModelAdminService.ClusterModelAdminServiceBase
{
    private readonly IClusterTrainingService _trainingService;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly ITranscriptStore _transcriptStore;
    private readonly TranscriptOptions _transcriptOptions;
    private readonly string _modelPath;
    private readonly ILogger<ClusterModelAdminGrpcService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ClusterModelAdminGrpcService"/> class.</summary>
    /// <param name="trainingService">Runs the guarded retrain sequence for the panel's retrain button.</param>
    /// <param name="memoryEntryStore">Supplies the current live memory entry count for "entries since last retrain".</param>
    /// <param name="transcriptStore">Supplies the current transcript row count for the retention context.</param>
    /// <param name="transcriptOptions">Supplies the configured retention days and max row bound.</param>
    /// <param name="storageOptions">Supplies the cluster model artifact's file path.</param>
    /// <param name="logger">The logger, also passed to <see cref="ClusterModelArtifactLoader"/>.</param>
    public ClusterModelAdminGrpcService(
        IClusterTrainingService trainingService,
        IMemoryEntryStore memoryEntryStore,
        ITranscriptStore transcriptStore,
        IOptions<TranscriptOptions> transcriptOptions,
        IOptions<StorageOptions> storageOptions,
        ILogger<ClusterModelAdminGrpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(trainingService);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(transcriptOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _trainingService = trainingService;
        _memoryEntryStore = memoryEntryStore;
        _transcriptStore = transcriptStore;
        _transcriptOptions = transcriptOptions.Value;
        _modelPath = storageOptions.Value.ResolveClusterModelPath();
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<Contract.ClusterModelStatusResponse> GetClusterModelStatus(
        Contract.GetClusterModelStatusRequest request,
        ServerCallContext context) =>
        await BuildStatusAsync(context.CancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override async Task RetrainClusterModel(
        Contract.RetrainClusterModelRequest request,
        IServerStreamWriter<Contract.ClusterRetrainStreamEvent> responseStream,
        ServerCallContext context)
    {
        var progress = new StreamingBootstrapProgress(responseStream);
        var outcome = await _trainingService.RetrainAsync(progress, context.CancellationToken).ConfigureAwait(false);

        // Re-read from disk rather than deriving the response from the outcome alone, mirroring
        // SyncBenchmarkData's "report the true post-mutation state" convention - the panel's cards reflect
        // whatever RetrainAsync actually wrote (or left untouched, on a decline) without a follow-up call.
        var status = await BuildStatusAsync(context.CancellationToken).ConfigureAwait(false);

        await responseStream.WriteAsync(new Contract.ClusterRetrainStreamEvent
        {
            Result = new Contract.ClusterRetrainResult
            {
                Kind = MapResultKind(outcome.Kind),
                Message = outcome.Message,
                Status = status,
            },
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the current status: the artifact on disk (if any, via <see cref="ClusterModelArtifactLoader"/>),
    /// plus the transcript retention context that always applies regardless of artifact presence.
    /// </summary>
    private async Task<Contract.ClusterModelStatusResponse> BuildStatusAsync(CancellationToken cancellationToken)
    {
        var artifact = ClusterModelArtifactLoader.TryLoad(_modelPath, _logger, "cluster model admin");
        var currentEntryCount = (await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false)).Count;
        var transcriptRowCount = await _transcriptStore.GetRowCountAsync(cancellationToken).ConfigureAwait(false);

        var response = new Contract.ClusterModelStatusResponse
        {
            ArtifactPresent = artifact is not null,
            RetentionDays = _transcriptOptions.RetentionDays,
            MaxTranscriptRows = _transcriptOptions.MaxRows,
            CurrentTranscriptRowCount = transcriptRowCount,
        };

        if (artifact is null)
        {
            // No retrain has ever written an artifact, so every live entry counted so far is "since the
            // last retrain" - there hasn't been one.
            response.EntriesSinceLastRetrain = currentEntryCount;
            return response;
        }

        response.ChosenK = artifact.ChosenK;
        response.TrainedAtUtc = Timestamp.FromDateTimeOffset(artifact.TrainedAtUtc);
        response.TrainedFrom = artifact.TrainedFrom;
        response.BootstrapTaskCount = artifact.BootstrapTaskCount;
        response.MemoryEntryCount = artifact.MemoryEntryCount;
        response.EntriesSinceLastRetrain = Math.Max(0, currentEntryCount - artifact.MemoryEntryCount);
        response.ClusterSizes.AddRange(artifact.ClusterSizes);
        response.ClusterNames.AddRange(Enumerable.Range(0, artifact.ChosenK).Select(artifact.DescribeCluster));

        return response;
    }

    /// <summary>Maps the domain retrain result category onto its wire enum.</summary>
    private static Contract.ClusterRetrainResultKind MapResultKind(ClusterTrainingResultKind kind) => kind switch
    {
        ClusterTrainingResultKind.Trained => Contract.ClusterRetrainResultKind.Trained,
        ClusterTrainingResultKind.Declined => Contract.ClusterRetrainResultKind.Declined,
        ClusterTrainingResultKind.AlreadyRunning => Contract.ClusterRetrainResultKind.AlreadyRunning,
        _ => Contract.ClusterRetrainResultKind.Unspecified,
    };

    /// <summary>
    /// Bridges <see cref="IClusterTrainingService.RetrainAsync"/>'s synchronous <see cref="IProgress{T}"/>
    /// bootstrap callback onto the async gRPC response stream. Blocking on <c>WriteAsync</c> inside
    /// <see cref="Report"/> is safe here for the same reason as
    /// <c>CodeRouterBench.BenchmarkDataAdminGrpcService.StreamingSyncProgress</c>: ASP.NET Core Kestrel
    /// handlers run without a captured <see cref="SynchronizationContext"/>, and
    /// <c>ClusterTrainingService.RetrainAsync</c> reports bootstrap progress sequentially from the single
    /// bootstrap-loading loop, so writes are never issued concurrently.
    /// </summary>
    private sealed class StreamingBootstrapProgress : IProgress<int>
    {
        private readonly IServerStreamWriter<Contract.ClusterRetrainStreamEvent> _stream;

        /// <summary>Initializes a new instance of the <see cref="StreamingBootstrapProgress"/> class.</summary>
        /// <param name="stream">The gRPC response stream to write each progress event to.</param>
        public StreamingBootstrapProgress(IServerStreamWriter<Contract.ClusterRetrainStreamEvent> stream) => _stream = stream;

        /// <inheritdoc/>
        public void Report(int tasksEmbedded) =>
            _stream.WriteAsync(new Contract.ClusterRetrainStreamEvent
            {
                BootstrapProgress = new Contract.ClusterRetrainBootstrapProgress { TasksEmbedded = tasksEmbedded },
            }).GetAwaiter().GetResult();
    }
}
