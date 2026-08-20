using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// gRPC service backing the Governance UI's System Settings window's "Adaptive Routing" row
/// (docs/router/self-organizing-classification-plan.md Phase T6): reads and mutates the two
/// <see cref="RouterSettingsStore"/>-backed overrides layered on top of <see cref="RoutingOptions"/> -
/// <see cref="RoutingOptions.EnableAdaptiveRouting"/> and <see cref="RoutingOptions.EmbeddingMemoryCapacity"/>.
/// Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the same loopback TLS endpoint as
/// <c>TelemetryService</c> and the other admin services.
/// </summary>
/// <remarks>
/// The one admin service on this endpoint that mutates <see cref="RoutingOptions"/> -
/// <see cref="RoutingModeAdminGrpcService"/> stays deliberately read-only (§M3.2's settled "read-only
/// reporting and settings mutation stay separate" convention). A successful <see cref="UpdateRouterSettings"/>
/// persists to <see cref="RouterSettingsStore"/>, triggers <see cref="RouterSettingsReloadToken"/> so
/// <c>IOptionsMonitor&lt;RoutingOptions&gt;</c> recomputes <c>CurrentValue</c> immediately, and - when the
/// capacity was lowered - awaits <see cref="EmbeddingMemory.TrimToCurrentCapacityAsync"/> directly rather
/// than relying solely on that reactive path, so the response's re-read values are guaranteed to reflect
/// the trim rather than racing it.
/// </remarks>
public sealed class RouterSettingsAdminGrpcService : Contract.RouterSettingsAdminService.RouterSettingsAdminServiceBase
{
    /// <summary>The inclusive lower bound <see cref="UpdateRouterSettings"/> enforces on <c>embedding_memory_capacity</c>, matching the GUI's own client-side minimum.</summary>
    public const int MinEmbeddingMemoryCapacity = 500;

    /// <summary>The inclusive upper bound <see cref="UpdateRouterSettings"/> enforces on <c>embedding_memory_capacity</c>, matching the GUI's own client-side maximum.</summary>
    public const int MaxEmbeddingMemoryCapacity = 50_000;

    private readonly RouterSettingsStore _store;
    private readonly IOptionsMonitor<RoutingOptions> _optionsMonitor;
    private readonly RouterSettingsReloadToken _reloadToken;
    private readonly EmbeddingMemory? _embeddingMemory;
    private readonly ILogger<RouterSettingsAdminGrpcService> _logger;

    /// <summary>Initializes a new instance of the <see cref="RouterSettingsAdminGrpcService"/> class.</summary>
    /// <param name="store">The settings store persisted mutations are written to.</param>
    /// <param name="optionsMonitor">Reports the currently effective values after precedence is applied.</param>
    /// <param name="reloadToken">Triggered after a successful write so <paramref name="optionsMonitor"/> recomputes immediately.</param>
    /// <param name="logger">The logger.</param>
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
        RouterSettingsReloadToken reloadToken,
        ILogger<RouterSettingsAdminGrpcService> logger,
        EmbeddingMemory? embeddingMemory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(reloadToken);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _optionsMonitor = optionsMonitor;
        _reloadToken = reloadToken;
        _embeddingMemory = embeddingMemory;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task<Contract.RouterSettingsResponse> GetRouterSettings(
        Contract.GetRouterSettingsRequest request,
        ServerCallContext context) =>
        Task.FromResult(BuildResponse());

    /// <inheritdoc />
    public override async Task<Contract.RouterSettingsResponse> UpdateRouterSettings(
        Contract.UpdateRouterSettingsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EmbeddingMemoryCapacity is < MinEmbeddingMemoryCapacity or > MaxEmbeddingMemoryCapacity)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"embedding_memory_capacity must be between {MinEmbeddingMemoryCapacity} and {MaxEmbeddingMemoryCapacity} (got {request.EmbeddingMemoryCapacity})."));
        }

        _store.SetBool(RouterSettingsStore.AdaptiveRoutingEnabledKey, request.AdaptiveRoutingEnabled);
        _store.SetInt(RouterSettingsStore.EmbeddingMemoryCapacityKey, request.EmbeddingMemoryCapacity);
        _reloadToken.Trigger();

        _logger.LogInformation(
            "Router settings updated: AdaptiveRoutingEnabled={AdaptiveRoutingEnabled} EmbeddingMemoryCapacity={EmbeddingMemoryCapacity}",
            request.AdaptiveRoutingEnabled,
            request.EmbeddingMemoryCapacity);

        // Awaited directly rather than left to EmbeddingMemory's own OnChange subscription, so the
        // re-read below (and thus this response) reflects the trim's completion rather than racing it -
        // mirrors RetrainClusterModel/SyncBenchmarkData's "re-read the true post-mutation state" convention.
        if (_embeddingMemory is not null)
        {
            await _embeddingMemory.TrimToCurrentCapacityAsync(context.CancellationToken).ConfigureAwait(false);
        }

        return BuildResponse();
    }

    /// <summary>Builds the response from the currently effective options - stored override, appsettings.json, or coded default, whichever precedence resolved to.</summary>
    private Contract.RouterSettingsResponse BuildResponse()
    {
        var options = _optionsMonitor.CurrentValue;
        return new Contract.RouterSettingsResponse
        {
            AdaptiveRoutingEnabled = options.EnableAdaptiveRouting,
            EmbeddingMemoryCapacity = options.EmbeddingMemoryCapacity,
        };
    }
}
