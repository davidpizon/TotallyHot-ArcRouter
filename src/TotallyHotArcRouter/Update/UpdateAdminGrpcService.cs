using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// gRPC service backing the Governance UI's System Settings window's "Software Update" section
/// (docs/router/auto-update-plan.md Phase 2): reports the Router's last-known self-update check outcome,
/// forces an immediate re-check, and applies a verified update. Mapped by
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> unconditionally onto the same loopback TLS
/// endpoint as <c>TelemetryService</c> and the other admin services - update status is core operational
/// state, the same way <c>RoutingModeAdminService</c> is always mapped.
/// </summary>
public sealed class UpdateAdminGrpcService : Contract.UpdateAdminService.UpdateAdminServiceBase
{
    private readonly IUpdateStateStore _stateStore;
    private readonly IReleaseCheckClient _releaseCheckClient;
    private readonly IUpdateApplier _applier;
    private readonly ILogger<UpdateAdminGrpcService> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateAdminGrpcService"/> class.</summary>
    /// <param name="stateStore">The last-known check outcome <see cref="GetUpdateStatus"/> reads.</param>
    /// <param name="releaseCheckClient">Runs the immediate re-check <see cref="CheckForUpdatesNow"/> performs.</param>
    /// <param name="applier">Downloads, verifies, and hands off to the updater for <see cref="ApplyUpdate"/>.</param>
    /// <param name="logger">The logger.</param>
    public UpdateAdminGrpcService(
        IUpdateStateStore stateStore,
        IReleaseCheckClient releaseCheckClient,
        IUpdateApplier applier,
        ILogger<UpdateAdminGrpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(releaseCheckClient);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(logger);

        _stateStore = stateStore;
        _releaseCheckClient = releaseCheckClient;
        _applier = applier;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task<Contract.UpdateStatusResponse> GetUpdateStatus(
        Contract.GetUpdateStatusRequest request,
        ServerCallContext context) =>
        Task.FromResult(MapSnapshot(_stateStore.Current));

    /// <inheritdoc />
    public override async Task<Contract.UpdateStatusResponse> CheckForUpdatesNow(
        Contract.CheckForUpdatesNowRequest request,
        ServerCallContext context)
    {
        var result = await _releaseCheckClient.CheckAsync(context.CancellationToken).ConfigureAwait(false);
        _stateStore.Record(result);

        _logger.LogInformation(
            "Manual update check requested: UpdateAvailable={UpdateAvailable} LatestVersion={LatestVersion}",
            result.IsUpdateAvailable,
            result.LatestVersion);

        return MapSnapshot(_stateStore.Current);
    }

    /// <inheritdoc />
    public override async Task<Contract.ApplyUpdateResponse> ApplyUpdate(
        Contract.ApplyUpdateRequest request,
        ServerCallContext context)
    {
        var current = _stateStore.Current.Result;
        if (current is null || !current.IsUpdateAvailable)
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "No verified update is currently known available. Call CheckForUpdatesNow first."));
        }

        var outcome = await _applier.ApplyAsync(current, context.CancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Update apply requested for version {LatestVersion}: Succeeded={Succeeded} Message={Message}",
            current.LatestVersion,
            outcome.Succeeded,
            outcome.Message);

        return new Contract.ApplyUpdateResponse
        {
            Succeeded = outcome.Succeeded,
            Message = outcome.Message,
        };
    }

    /// <summary>Converts an <see cref="UpdateStateSnapshot"/> into the wire response.</summary>
    private static Contract.UpdateStatusResponse MapSnapshot(UpdateStateSnapshot snapshot)
    {
        var response = new Contract.UpdateStatusResponse
        {
            CurrentVersion = snapshot.Result?.CurrentVersion ?? string.Empty,
            LatestVersion = snapshot.Result?.LatestVersion ?? string.Empty,
            UpdateAvailable = snapshot.Result?.IsUpdateAvailable ?? false,
            // Distinct from ReleaseCheckUnavailableReason.None (a check that ran and resolved cleanly):
            // no check has ever completed yet, so nothing is known - the wire enum's zero value.
            UnavailableReason = snapshot.Result is null
                ? Contract.UpdateUnavailableReason.Unspecified
                : MapReason(snapshot.Result.UnavailableReason),
        };

        if (snapshot.CheckedAtUtc is { } checkedAt)
        {
            response.CheckedAtUtc = Timestamp.FromDateTimeOffset(checkedAt);
        }

        if (snapshot.Result?.UnavailableDetail is { } detail)
        {
            response.UnavailableDetail = detail;
        }

        return response;
    }

    /// <summary>Maps the domain reason enum onto the wire enum.</summary>
    private static Contract.UpdateUnavailableReason MapReason(ReleaseCheckUnavailableReason reason) => reason switch
    {
        ReleaseCheckUnavailableReason.None => Contract.UpdateUnavailableReason.None,
        ReleaseCheckUnavailableReason.NoReleasesPublished => Contract.UpdateUnavailableReason.NoReleasesPublished,
        ReleaseCheckUnavailableReason.MalformedTag => Contract.UpdateUnavailableReason.MalformedTag,
        ReleaseCheckUnavailableReason.AssetOrChecksumMissing => Contract.UpdateUnavailableReason.AssetOrChecksumMissing,
        ReleaseCheckUnavailableReason.NetworkOrApiFailure => Contract.UpdateUnavailableReason.NetworkOrApiFailure,
        _ => Contract.UpdateUnavailableReason.Unspecified,
    };
}
