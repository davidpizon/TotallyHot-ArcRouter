using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// gRPC service backing the Governance → Router Model panel (docs/router/live-feedback-learning-plan.md
/// Phase 5): reports the trained <c>logreg</c> voter's status and runs a retrain with streamed
/// bootstrap-embedding progress. Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the
/// same loopback TLS endpoint as <c>TelemetryService</c> and the other admin services, mirroring
/// <see cref="ClusterModelAdminGrpcService"/>'s shape exactly.
/// </summary>
public sealed class LogRegModelAdminGrpcService : Contract.RouterModelAdminService.RouterModelAdminServiceBase
{
    private readonly IEmbeddingLogRegTrainingService _trainingService;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly RoutingOptions _routingOptions;
    private readonly string _modelPath;
    private readonly ILogger<LogRegModelAdminGrpcService> _logger;

    /// <summary>Initializes a new instance of the <see cref="LogRegModelAdminGrpcService"/> class.</summary>
    /// <param name="trainingService">Runs the guarded retrain sequence for the panel's retrain button.</param>
    /// <param name="memoryEntryStore">Supplies the current live memory entry count for "entries since last retrain".</param>
    /// <param name="routingOptions">Supplies the retrain threshold and live-sample-weight context.</param>
    /// <param name="storageOptions">Supplies the logreg model artifact's file path.</param>
    /// <param name="logger">The logger.</param>
    public LogRegModelAdminGrpcService(
        IEmbeddingLogRegTrainingService trainingService,
        IMemoryEntryStore memoryEntryStore,
        IOptions<RoutingOptions> routingOptions,
        IOptions<StorageOptions> storageOptions,
        ILogger<LogRegModelAdminGrpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(trainingService);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(routingOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _trainingService = trainingService;
        _memoryEntryStore = memoryEntryStore;
        _routingOptions = routingOptions.Value;
        _modelPath = storageOptions.Value.ResolveLogRegModelPath();
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<Contract.LogRegModelStatusResponse> GetLogRegModelStatus(
        Contract.GetLogRegModelStatusRequest request,
        ServerCallContext context) =>
        await BuildStatusAsync(context.CancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override async Task RetrainLogRegModel(
        Contract.RetrainLogRegModelRequest request,
        IServerStreamWriter<Contract.LogRegRetrainStreamEvent> responseStream,
        ServerCallContext context)
    {
        var progress = new StreamingBootstrapProgress(responseStream);
        var outcome = await _trainingService.RetrainAsync(progress, context.CancellationToken).ConfigureAwait(false);

        // Re-read from disk rather than deriving the response from the outcome alone, mirroring
        // ClusterModelAdminGrpcService's "report the true post-mutation state" convention - the panel's
        // cards reflect whatever RetrainAsync actually wrote (or left untouched, on a decline) without a
        // follow-up call.
        var status = await BuildStatusAsync(context.CancellationToken).ConfigureAwait(false);

        await responseStream.WriteAsync(new Contract.LogRegRetrainStreamEvent
        {
            Result = new Contract.LogRegRetrainResult
            {
                Kind = MapResultKind(outcome.Kind),
                Message = outcome.Message,
                Status = status,
            },
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the current status: the artifact on disk (if any, tolerating a missing or unreadable file),
    /// plus the retrain threshold/live-sample-weight context that always applies regardless of artifact
    /// presence.
    /// </summary>
    private async Task<Contract.LogRegModelStatusResponse> BuildStatusAsync(CancellationToken cancellationToken)
    {
        var artifact = TryLoadArtifact();
        var currentEntryCount = (await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false)).Count;

        var response = new Contract.LogRegModelStatusResponse
        {
            ArtifactPresent = artifact is not null,
            RetrainThreshold = _routingOptions.LogRegRetrainThreshold,
            LiveSampleWeight = _routingOptions.LogRegLiveSampleWeight,
        };

        if (artifact is null)
        {
            // No retrain has ever written an artifact, so every live entry counted so far is "since the
            // last retrain" - there hasn't been one.
            response.EntriesSinceLastRetrain = currentEntryCount;
            return response;
        }

        response.EmbeddingDimension = artifact.EmbeddingDimension;
        response.TrainedFrom = artifact.TrainedFrom;
        response.BootstrapTaskCount = artifact.BootstrapTaskCount;
        response.MemoryEntryCount = artifact.MemoryEntryCount;
        response.ModelsRepresented = artifact.ClassWeights.Count;
        response.EntriesSinceLastRetrain = Math.Max(0, currentEntryCount - artifact.MemoryEntryCount);

        if (File.Exists(_modelPath))
        {
            response.TrainedAtUtc = Timestamp.FromDateTimeOffset(File.GetLastWriteTimeUtc(_modelPath));
        }

        return response;
    }

    /// <summary>
    /// Reads and deserializes the model artifact from <see cref="_modelPath"/>, tolerating a missing or
    /// unreadable file by returning <see langword="null"/> - the honest "no model yet" state of a fresh
    /// install, mirroring <see cref="LogRegVoter"/>'s own loading behavior.
    /// </summary>
    private EmbeddingLogRegModelArtifact? TryLoadArtifact()
    {
        if (!File.Exists(_modelPath))
        {
            return null;
        }

        try
        {
            return EmbeddingLogRegModelArtifactSerializer.Deserialize(File.ReadAllText(_modelPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to load the logreg voter model from {Path} for the admin panel.", _modelPath);
            return null;
        }
    }

    /// <summary>Maps the domain retrain result category onto its wire enum.</summary>
    private static Contract.LogRegRetrainResultKind MapResultKind(LogRegTrainingResultKind kind) => kind switch
    {
        LogRegTrainingResultKind.Trained => Contract.LogRegRetrainResultKind.Trained,
        LogRegTrainingResultKind.Declined => Contract.LogRegRetrainResultKind.Declined,
        LogRegTrainingResultKind.AlreadyRunning => Contract.LogRegRetrainResultKind.AlreadyRunning,
        _ => Contract.LogRegRetrainResultKind.Unspecified,
    };

    /// <summary>
    /// Bridges <see cref="IEmbeddingLogRegTrainingService.RetrainAsync"/>'s synchronous
    /// <see cref="IProgress{T}"/> bootstrap callback onto the async gRPC response stream. Blocking on
    /// <c>WriteAsync</c> inside <see cref="Report"/> is safe for the same reason as
    /// <c>ClusterModelAdminGrpcService.StreamingBootstrapProgress</c>: ASP.NET Core Kestrel handlers run
    /// without a captured <see cref="SynchronizationContext"/>, and the bootstrap source reports progress
    /// sequentially from a single loading loop, so writes are never issued concurrently.
    /// </summary>
    private sealed class StreamingBootstrapProgress : IProgress<int>
    {
        private readonly IServerStreamWriter<Contract.LogRegRetrainStreamEvent> _stream;

        /// <summary>Initializes a new instance of the <see cref="StreamingBootstrapProgress"/> class.</summary>
        /// <param name="stream">The gRPC response stream to write each progress event to.</param>
        public StreamingBootstrapProgress(IServerStreamWriter<Contract.LogRegRetrainStreamEvent> stream) => _stream = stream;

        /// <inheritdoc/>
        public void Report(int tasksEmbedded) =>
            _stream.WriteAsync(new Contract.LogRegRetrainStreamEvent
            {
                BootstrapProgress = new Contract.LogRegRetrainBootstrapProgress { TasksEmbedded = tasksEmbedded },
            }).GetAwaiter().GetResult();
    }
}
