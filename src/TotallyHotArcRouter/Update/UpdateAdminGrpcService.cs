using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// gRPC service backing the Governance UI's System Settings window's "Software Update" section
/// (docs/router/auto-update-plan.md Phase 2, packaging superseded by
/// docs/router/packaging-and-distribution.md): reports the Router's last-known self-update check
/// outcome, forces an immediate re-check, and receives the GUI's audit notification that it is about to
/// apply an update. Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> unconditionally onto
/// the same loopback TLS endpoint as <c>TelemetryService</c> and the other admin services - update
/// status is core operational state, the same way <c>RoutingModeAdminService</c> is always mapped.
/// </summary>
/// <remarks>
/// The Router does not download, verify, or launch anything for an update. <c>RequestApply</c> only
/// remembers one pending install (ADR-0028). The logged-on tray claims it and runs
/// <c>TotallyHot.ArcRouter.Gui.Telemetry.MsiUpdateApplier</c>, which downloads, checksum-verifies, and
/// launches an elevated <c>msiexec</c>. A browser page cannot start that process, and this service
/// runs where a Windows service cannot show the administrator prompt.
/// </remarks>
public sealed class UpdateAdminGrpcService : Contract.UpdateAdminService.UpdateAdminServiceBase
{
    private readonly ILogger<UpdateAdminGrpcService> _logger;
    private readonly IReleaseCheckClient _releaseCheckClient;
    private readonly IUpdateStateStore _stateStore;

    /// <summary>Initializes a new instance of the <see cref="UpdateAdminGrpcService"/> class.</summary>
    /// <param name="stateStore">The last-known check outcome <see cref="GetUpdateStatus"/> reads.</param>
    /// <param name="releaseCheckClient">Runs the immediate re-check <see cref="CheckForUpdatesNow"/> performs.</param>
    /// <param name="logger">The logger.</param>
    public UpdateAdminGrpcService(
        IUpdateStateStore stateStore,
        IReleaseCheckClient releaseCheckClient,
        ILogger<UpdateAdminGrpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(releaseCheckClient);
        ArgumentNullException.ThrowIfNull(logger);

        _stateStore = stateStore;
        _releaseCheckClient = releaseCheckClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override Task<Contract.UpdateStatusResponse> GetUpdateStatus(
        Contract.GetUpdateStatusRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(MapSnapshot(_stateStore.Current));
    }

    /// <inheritdoc/>
    public override async Task<Contract.UpdateStatusResponse> CheckForUpdatesNow(
        Contract.CheckForUpdatesNowRequest request,
        ServerCallContext context)
    {
        var result = await _releaseCheckClient.CheckAsync(context.CancellationToken).ConfigureAwait(false);
        _stateStore.Record(result);

        _logger.LogInformation(
            message: "Manual update check requested: UpdateAvailable={UpdateAvailable} LatestVersion={LatestVersion}",
            result.IsUpdateAvailable,
            result.LatestVersion);

        return MapSnapshot(_stateStore.Current);
    }

    /// <inheritdoc/>
    public override Task<Contract.NotifyApplyStartingResponse> NotifyApplyStarting(
        Contract.NotifyApplyStartingRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            message: "GUI reports it is about to apply update to version {Version}; this service will restart shortly.",
            request.Version);

        return Task.FromResult(new Contract.NotifyApplyStartingResponse { Acknowledged = true });
    }

    /// <inheritdoc/>
    public override Task<Contract.RequestApplyResponse> RequestApply(
        Contract.RequestApplyRequest request,
        ServerCallContext context)
    {
        (bool created, PendingUpdateApply pending) requested;
        try
        {
            requested = _stateStore.TryRequestApply();
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(statusCode: StatusCode.FailedPrecondition, detail: ex.Message));
        }

        _logger.LogInformation(
            message: "Update apply requested for version {Version}; the tray will launch the installer. AlreadyPending={AlreadyPending}",
            requested.pending.LatestVersion,
            !requested.created);

        return Task.FromResult(new Contract.RequestApplyResponse
        {
            Accepted = true,
            AlreadyPending = !requested.created,
            LatestVersion = requested.pending.LatestVersion
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.ClaimPendingApplyResponse> ClaimPendingApply(
        Contract.ClaimPendingApplyRequest request,
        ServerCallContext context)
    {
        var claimed = _stateStore.TryClaimApply();
        if (claimed is null) return Task.FromResult(new Contract.ClaimPendingApplyResponse { Claimed = false });

        _logger.LogInformation(
            message: "Tray claimed the pending update to version {Version}.",
            claimed.LatestVersion);

        return Task.FromResult(new Contract.ClaimPendingApplyResponse
        {
            Claimed = true,
            LatestVersion = claimed.LatestVersion,
            AssetDownloadUrl = claimed.AssetDownloadUrl,
            AssetSha256 = claimed.AssetSha256
        });
    }

    /// <summary>Converts an <see cref="UpdateStateSnapshot"/> into the wire response.</summary>
    private static Contract.UpdateStatusResponse MapSnapshot(UpdateStateSnapshot snapshot)
    {
        var response = new Contract.UpdateStatusResponse
        {
            CurrentVersion = snapshot.Result?.CurrentVersion ?? string.Empty,
            LatestVersion = snapshot.Result?.LatestVersion ?? string.Empty,
            UpdateAvailable = snapshot.Result?.IsUpdateAvailable ?? false,
            ApplyPending = snapshot.ApplyPending,
            // Distinct from ReleaseCheckUnavailableReason.None (a check that ran and resolved cleanly):
            // no check has ever completed yet, so nothing is known - the wire enum's zero value.
            UnavailableReason = snapshot.Result is null
                ? Contract.UpdateUnavailableReason.Unspecified
                : MapReason(snapshot.Result.UnavailableReason)
        };

        if (snapshot.CheckedAtUtc is { } checkedAt) response.CheckedAtUtc = Timestamp.FromDateTimeOffset(checkedAt);

        if (snapshot.Result?.UnavailableDetail is { } detail) response.UnavailableDetail = detail;

        if (snapshot.Result?.IsUpdateAvailable == true)
        {
            if (snapshot.Result.AssetDownloadUrl is { } assetDownloadUrl) response.AssetDownloadUrl = assetDownloadUrl;

            if (snapshot.Result.AssetSha256 is { } assetSha256) response.AssetSha256 = assetSha256;
        }

        return response;
    }

    /// <summary>Maps the domain reason enum onto the wire enum.</summary>
    private static Contract.UpdateUnavailableReason MapReason(ReleaseCheckUnavailableReason reason)
    {
        return reason switch
        {
            ReleaseCheckUnavailableReason.None => Contract.UpdateUnavailableReason.None,
            ReleaseCheckUnavailableReason.NoReleasesPublished => Contract.UpdateUnavailableReason.NoReleasesPublished,
            ReleaseCheckUnavailableReason.MalformedTag => Contract.UpdateUnavailableReason.MalformedTag,
            ReleaseCheckUnavailableReason.AssetOrChecksumMissing => Contract.UpdateUnavailableReason
                .AssetOrChecksumMissing,
            ReleaseCheckUnavailableReason.NetworkOrApiFailure => Contract.UpdateUnavailableReason.NetworkOrApiFailure,
            _ => Contract.UpdateUnavailableReason.Unspecified
        };
    }
}