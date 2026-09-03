using Grpc.Core;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// gRPC service backing the Governance UI's System Settings window's "Adaptive Routing", "Shadow Judge", and
/// "Transcription Capture" rows (docs/router/self-organizing-classification-plan.md Phase T6;
/// docs/router/geval-shadow-scoring-plan.md): reads and mutates every
/// <see cref="RouterSettingsStore"/>-backed override - <see cref="RoutingOptions.EnableAdaptiveRouting"/>
/// and <see cref="RoutingOptions.EmbeddingMemoryCapacity"/> on <see cref="RoutingOptions"/>,
/// <see cref="JudgeOptions.Enabled"/> and <see cref="JudgeOptions.ModelName"/> on <see cref="JudgeOptions"/>,
/// and <see cref="Transcripts.TranscriptOptions.Enabled"/> on <see cref="Transcripts.TranscriptOptions"/> -
/// plus <see cref="ClearTranscripts"/>, the Transcription Capture row's "Clear" action. Mapped by
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the same loopback TLS endpoint as
/// <c>TelemetryService</c> and the other admin services.
/// </summary>
/// <remarks>
/// <para>
/// The one admin service on this endpoint that mutates <see cref="RoutingOptions"/> -
/// <see cref="RoutingModeAdminGrpcService"/> stays deliberately read-only (§M3.2's settled "read-only
/// reporting and settings mutation stay separate" convention). A successful <see cref="UpdateRouterSettings"/>
/// persists to <see cref="RouterSettingsStore"/>, triggers <see cref="RouterSettingsReloadToken"/> so
/// <c>IOptionsMonitor&lt;RoutingOptions&gt;</c> recomputes <c>CurrentValue</c> immediately, and - when the
/// capacity was lowered - awaits <see cref="EmbeddingMemory.TrimToCurrentCapacityAsync"/> directly rather
/// than relying solely on that reactive path, so the response's re-read values are guaranteed to reflect
/// the trim rather than racing it.
/// </para>
/// <para>
/// The judge settings live here rather than in a service of their own because they share this one's whole
/// mechanism: the same <c>router_settings</c> table, the same reload token (which
/// <see cref="RouterSettingsReloadToken"/> serves for both options types), and the same Save button. Their
/// one addition is a validated field - a chosen judge model must be currently eligible per
/// <see cref="JudgeModelSelector"/>, checked here the way the capacity bounds are.
/// </para>
/// </remarks>
public sealed class RouterSettingsAdminGrpcService : Contract.RouterSettingsAdminService.RouterSettingsAdminServiceBase
{
    /// <summary>
    /// The inclusive lower bound <see cref="UpdateRouterSettings"/> enforces on <c>embedding_memory_capacity</c>,
    /// matching the GUI's own client-side minimum.
    /// </summary>
    public const int MinEmbeddingMemoryCapacity = 500;

    /// <summary>
    /// The inclusive upper bound <see cref="UpdateRouterSettings"/> enforces on <c>embedding_memory_capacity</c>,
    /// matching the GUI's own client-side maximum.
    /// </summary>
    public const int MaxEmbeddingMemoryCapacity = 50_000;

    private readonly EmbeddingMemory? _embeddingMemory;
    private readonly JudgeModelSelector _judgeModelSelector;
    private readonly IOptionsMonitor<JudgeOptions> _judgeOptionsMonitor;
    private readonly ILogger<RouterSettingsAdminGrpcService> _logger;
    private readonly IOptionsMonitor<RoutingOptions> _optionsMonitor;
    private readonly RouterSettingsReloadToken _reloadToken;

    private readonly RouterSettingsStore _store;
    private readonly IOptionsMonitor<TranscriptOptions> _transcriptOptionsMonitor;
    private readonly ITranscriptStore _transcriptStore;

    /// <summary>Initializes a new instance of the <see cref="RouterSettingsAdminGrpcService"/> class.</summary>
    /// <param name="store">The settings store persisted mutations are written to.</param>
    /// <param name="optionsMonitor">Reports the currently effective values after precedence is applied.</param>
    /// <param name="judgeOptionsMonitor">
    /// Reports the shadow judge's currently effective settings, the same way
    /// <paramref name="optionsMonitor"/> does for routing.
    /// </param>
    /// <param name="judgeModelSelector">
    /// Supplies the eligible judge-backbone list, both to populate the dropdown and to
    /// validate a save against it.
    /// </param>
    /// <param name="reloadToken">
    /// Triggered after a successful write so <paramref name="optionsMonitor"/> recomputes
    /// immediately.
    /// </param>
    /// <param name="logger">The logger.</param>
    /// <param name="transcriptOptionsMonitor">
    /// Reports the Transcription Capture toggle's currently effective value, the same
    /// way <paramref name="optionsMonitor"/> does for routing.
    /// </param>
    /// <param name="transcriptStore">Backs <see cref="ClearTranscripts"/>.</param>
    /// <param name="embeddingMemory">
    /// Optional working set to trim synchronously when a save lowers the capacity. Optional only so this
    /// service remains constructible in a context that doesn't wire up embedding memory at all; every real
    /// deployment supplies it, and omitting it just means the reactive <c>OnChange</c> trim (still wired
    /// through <paramref name="optionsMonitor"/> independently) runs on its own schedule instead of being
    /// awaited here.
    /// </param>
    public RouterSettingsAdminGrpcService(
        RouterSettingsStore store,
        IOptionsMonitor<RoutingOptions> optionsMonitor,
        IOptionsMonitor<JudgeOptions> judgeOptionsMonitor,
        JudgeModelSelector judgeModelSelector,
        RouterSettingsReloadToken reloadToken,
        ILogger<RouterSettingsAdminGrpcService> logger,
        IOptionsMonitor<TranscriptOptions> transcriptOptionsMonitor,
        ITranscriptStore transcriptStore,
        EmbeddingMemory? embeddingMemory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(judgeOptionsMonitor);
        ArgumentNullException.ThrowIfNull(judgeModelSelector);
        ArgumentNullException.ThrowIfNull(reloadToken);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcriptOptionsMonitor);
        ArgumentNullException.ThrowIfNull(transcriptStore);

        _store = store;
        _optionsMonitor = optionsMonitor;
        _judgeOptionsMonitor = judgeOptionsMonitor;
        _judgeModelSelector = judgeModelSelector;
        _reloadToken = reloadToken;
        _embeddingMemory = embeddingMemory;
        _transcriptOptionsMonitor = transcriptOptionsMonitor;
        _transcriptStore = transcriptStore;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override Task<Contract.RouterSettingsResponse> GetRouterSettings(
        Contract.GetRouterSettingsRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(BuildResponse());
    }

    /// <inheritdoc/>
    public override async Task<Contract.RouterSettingsResponse> UpdateRouterSettings(
        Contract.UpdateRouterSettingsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EmbeddingMemoryCapacity is < MinEmbeddingMemoryCapacity or > MaxEmbeddingMemoryCapacity)
            throw new RpcException(new Status(
                statusCode: StatusCode.InvalidArgument,
                detail:
                $"embedding_memory_capacity must be between {MinEmbeddingMemoryCapacity} and {MaxEmbeddingMemoryCapacity} (got {request.EmbeddingMemoryCapacity})."));

        // Rejected rather than silently coerced to automatic: saving a model the selector would not
        // actually call leaves the window showing a choice that is not in force, which is precisely the
        // confusion the eligible-list-and-validate pair exists to prevent. Empty is always valid - it is
        // the explicit "automatic" choice, not a missing one.
        var judgeModelName = request.JudgeModelName ?? string.Empty;
        if (!string.IsNullOrEmpty(judgeModelName) &&
            !_judgeModelSelector.ListEligibleModels()
                .Contains(value: judgeModelName, comparer: StringComparer.OrdinalIgnoreCase))
            throw new RpcException(new Status(
                statusCode: StatusCode.InvalidArgument,
                detail:
                $"judge_model_name '{judgeModelName}' is not an eligible judge backbone. It must name a model on a free, enabled provider, or be empty for automatic selection."));

        _store.SetBool(key: RouterSettingsStore.AdaptiveRoutingEnabledKey, value: request.AdaptiveRoutingEnabled);
        _store.SetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, value: request.EmbeddingMemoryCapacity);
        _store.SetBool(key: RouterSettingsStore.JudgeEnabledKey, value: request.JudgeEnabled);
        _store.SetString(key: RouterSettingsStore.JudgeModelNameKey, value: judgeModelName);
        _store.SetBool(key: RouterSettingsStore.TranscriptCaptureEnabledKey, value: request.TranscriptCaptureEnabled);
        _reloadToken.Trigger();

        _logger.LogInformation(
            message:
            "Router settings updated: AdaptiveRoutingEnabled={AdaptiveRoutingEnabled} EmbeddingMemoryCapacity={EmbeddingMemoryCapacity} JudgeEnabled={JudgeEnabled} JudgeModelName={JudgeModelName} TranscriptCaptureEnabled={TranscriptCaptureEnabled}",
            request.AdaptiveRoutingEnabled,
            request.EmbeddingMemoryCapacity,
            request.JudgeEnabled,
            judgeModelName,
            request.TranscriptCaptureEnabled);

        // Awaited directly rather than left to EmbeddingMemory's own OnChange subscription, so the
        // re-read below (and thus this response) reflects the trim's completion rather than racing it -
        // mirrors RetrainClusterModel/SyncBenchmarkData's "re-read the true post-mutation state" convention.
        if (_embeddingMemory is not null)
            await _embeddingMemory.TrimToCurrentCapacityAsync(context.CancellationToken).ConfigureAwait(false);

        return BuildResponse();
    }

    /// <inheritdoc/>
    public override async Task<Contract.ClearTranscriptsResponse> ClearTranscripts(
        Contract.ClearTranscriptsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rowsDeleted = await _transcriptStore.DeleteAllAsync(context.CancellationToken).ConfigureAwait(false);

        _logger.LogInformation(message: "Transcript data cleared: RowsDeleted={RowsDeleted}", rowsDeleted);

        return new Contract.ClearTranscriptsResponse { RowsDeleted = rowsDeleted };
    }

    /// <summary>
    /// Builds the response from the currently effective options - stored override, appsettings.json, or coded
    /// default, whichever precedence resolved to.
    /// </summary>
    private Contract.RouterSettingsResponse BuildResponse()
    {
        var options = _optionsMonitor.CurrentValue;
        var judgeOptions = _judgeOptionsMonitor.CurrentValue;

        var response = new Contract.RouterSettingsResponse
        {
            AdaptiveRoutingEnabled = options.EnableAdaptiveRouting,
            EmbeddingMemoryCapacity = options.EmbeddingMemoryCapacity,
            JudgeEnabled = judgeOptions.Enabled,
            JudgeModelName = judgeOptions.ModelName,
            TranscriptCaptureEnabled = _transcriptOptionsMonitor.CurrentValue.Enabled
        };

        // Recomputed on every read rather than cached: provider and model enablement change from the
        // Providers screen independently of this window, so a list captured earlier could offer a model
        // that has since stopped being eligible.
        response.EligibleJudgeModels.AddRange(_judgeModelSelector.ListEligibleModels());

        return response;
    }
}