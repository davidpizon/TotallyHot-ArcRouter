using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Why a check could not resolve a definite answer. Mirrors
/// <c>TotallyHot.ArcRouter.Update.ReleaseCheckUnavailableReason</c>.
/// </summary>
public enum UpdateUnavailableReasonInfo
{
    /// <summary>The check resolved definitely; not itself a reason.</summary>
    None,

    /// <summary>The repository has no GitHub Releases published yet.</summary>
    NoReleasesPublished,

    /// <summary>The release's tag was not a parseable version.</summary>
    MalformedTag,

    /// <summary>The release does not publish both a recognizable asset and a matching checksum.</summary>
    AssetOrChecksumMissing,

    /// <summary>The GitHub API request itself failed.</summary>
    NetworkOrApiFailure
}

/// <summary>The Router's last-known (or freshly forced) self-update check outcome.</summary>
/// <param name="CurrentVersion">The running Router's own version.</param>
/// <param name="LatestVersion">The latest published release's version, empty when unknown.</param>
/// <param name="UpdateAvailable">Whether a newer, verified, installable release is known.</param>
/// <param name="CheckedAtUtc">
/// When this status was computed, or <see langword="null"/> before the first check has ever
/// run.
/// </param>
/// <param name="UnavailableReason">
/// Why the check could not resolve, or <see cref="UpdateUnavailableReasonInfo.None"/> on a
/// definite outcome.
/// </param>
/// <param name="UnavailableDetail">A human-readable elaboration of <paramref name="UnavailableReason"/>.</param>
/// <param name="AssetDownloadUrl">
/// The installer MSI's direct download URL, set only when <paramref name="UpdateAvailable"/> is
/// <see langword="true"/>. The GUI downloads this itself via <see cref="IMsiUpdateApplier"/> - the
/// Router never does.
/// </param>
/// <param name="AssetSha256">
/// The MSI's published SHA256 (lowercase hex), set only when <paramref name="UpdateAvailable"/>
/// is <see langword="true"/>.
/// </param>
/// <param name="ApplyPending">
/// Whether a dashboard apply request is waiting for the tray to claim it.
/// </param>
public sealed record UpdateStatusInfo(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    DateTimeOffset? CheckedAtUtc,
    UpdateUnavailableReasonInfo UnavailableReason,
    string? UnavailableDetail,
    string? AssetDownloadUrl = null,
    string? AssetSha256 = null,
    bool ApplyPending = false);

/// <summary>The outcome of asking the router to remember one installer for the tray.</summary>
/// <param name="Accepted">Whether an install is now waiting, including one that was already waiting.</param>
/// <param name="AlreadyPending">Whether this call left an earlier install in place instead of recording another.</param>
/// <param name="LatestVersion">The version of the install that is waiting.</param>
public sealed record RequestApplyInfo(bool Accepted, bool AlreadyPending, string LatestVersion);

/// <summary>The install the tray claimed, or an empty claim when nothing was waiting.</summary>
/// <param name="Claimed">Whether an install was waiting and is now this caller's to launch.</param>
/// <param name="LatestVersion">The release version, empty when <paramref name="Claimed"/> is false.</param>
/// <param name="AssetDownloadUrl">The MSI URL, or <see langword="null"/> when nothing was claimed.</param>
/// <param name="AssetSha256">The published SHA256, or <see langword="null"/> when nothing was claimed.</param>
public sealed record ClaimedUpdateApply(
    bool Claimed,
    string LatestVersion,
    string? AssetDownloadUrl,
    string? AssetSha256);

/// <summary>The outcome of a "notify the Router an apply is starting" audit call.</summary>
/// <param name="Acknowledged">
/// Whether the Router recorded the notification. Never gates the apply itself - the GUI
/// proceeds regardless.
/// </param>
public sealed record NotifyApplyStartingInfo(bool Acknowledged);

/// <summary>
/// The Router self-update operations the Governance UI's System Settings window's "Software Update"
/// section needs. An interface so the consuming store can be unit-tested against a fake without a live
/// proxy or a gRPC channel, mirroring <see cref="ILlmRouterModelAdminClient"/>.
/// </summary>
public interface IUpdateAdminClient
{
    /// <summary>Reads the last-known check outcome.</summary>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<UpdateStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Forces an immediate re-check and returns the fresh outcome.</summary>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<UpdateStatusInfo> CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the Router it is about to apply an update to <paramref name="version"/>, purely for the
    /// audit log - the Router does not download, verify, or gate anything here. The caller (
    /// <see cref="IMsiUpdateApplier"/>, driven from <c>UpdateStore</c>) already has everything it needs
    /// from a prior <see cref="GetStatusAsync"/>/<see cref="CheckNowAsync"/> result and proceeds with the
    /// apply even if this call fails to reach the Router.
    /// </summary>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<NotifyApplyStartingInfo> NotifyApplyStartingAsync(string version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the router to remember one pending install for the tray. The router does not download or
    /// start a process. A second call while one install is unclaimed does not record another.
    /// </summary>
    /// <exception cref="GrpcAdminException">
    /// The call failed, no verified update is available, or the router is unreachable.
    /// </exception>
    Task<RequestApplyInfo> RequestApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the one pending install, if any. The first claim clears it; a later claim is empty, so
    /// the tray launches the installer once.
    /// </summary>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<ClaimedUpdateApply> ClaimPendingApplyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for the proxy's <c>UpdateAdminService</c> - the Governance UI's System Settings window's
/// "Software Update" section (docs/router/auto-update-plan.md Phase 2, packaging superseded by
/// docs/router/packaging-and-distribution.md). Lives in this plain <c>net10.0</c> library rather than the
/// Windows-only MAUI project so CI can unit-test it, exactly like <see cref="LlmRouterModelAdminClient"/>.
/// </summary>
public sealed class UpdateAdminClient
    : GrpcAdminClientBase<Contract.UpdateAdminService.UpdateAdminServiceClient>,
        IUpdateAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateAdminClient"/> class over a shared, already-
    /// authenticated call invoker (web GUI migration plan Phase P5a) - see
    /// <see cref="IRouterChannelProvider"/>'s remarks for why production goes through this constructor.
    /// The caller owns the invoker's underlying channel.
    /// </summary>
    /// <param name="callInvoker">The shared call invoker - see <see cref="IRouterChannelProvider.CallInvoker"/>.</param>
    public UpdateAdminClient(CallInvoker callInvoker)
        : base(new Contract.UpdateAdminService.UpdateAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns
    /// the channel's lifetime.
    /// </summary>
    public UpdateAdminClient(Contract.UpdateAdminService.UpdateAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<UpdateStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetUpdateStatusAsync(request: new Contract.GetUpdateStatusRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the update status");
        }
    }

    /// <inheritdoc/>
    public async Task<UpdateStatusInfo> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .CheckForUpdatesNowAsync(request: new Contract.CheckForUpdatesNowRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Update check failed");
        }
    }

    /// <inheritdoc/>
    public async Task<NotifyApplyStartingInfo> NotifyApplyStartingAsync(string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        try
        {
            var response = await Client
                .NotifyApplyStartingAsync(request: new Contract.NotifyApplyStartingRequest { Version = version },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new NotifyApplyStartingInfo(response.Acknowledged);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not notify the router that an apply is starting");
        }
    }

    /// <inheritdoc/>
    public async Task<RequestApplyInfo> RequestApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .RequestApplyAsync(request: new Contract.RequestApplyRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RequestApplyInfo(
                Accepted: response.Accepted,
                AlreadyPending: response.AlreadyPending,
                LatestVersion: response.LatestVersion);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not request the update");
        }
    }

    /// <inheritdoc/>
    public async Task<ClaimedUpdateApply> ClaimPendingApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .ClaimPendingApplyAsync(request: new Contract.ClaimPendingApplyRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new ClaimedUpdateApply(
                Claimed: response.Claimed,
                LatestVersion: response.LatestVersion,
                AssetDownloadUrl: string.IsNullOrEmpty(response.AssetDownloadUrl) ? null : response.AssetDownloadUrl,
                AssetSha256: string.IsNullOrEmpty(response.AssetSha256) ? null : response.AssetSha256);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not claim the pending update");
        }
    }

    /// <summary>Converts a gRPC-contract status response into the client's <see cref="UpdateStatusInfo"/>.</summary>
    private static UpdateStatusInfo MapStatus(Contract.UpdateStatusResponse response)
    {
        return new UpdateStatusInfo(
            CurrentVersion: response.CurrentVersion,
            LatestVersion: response.LatestVersion,
            UpdateAvailable: response.UpdateAvailable,
            CheckedAtUtc: response.CheckedAtUtc?.ToDateTimeOffset(),
            UnavailableReason: MapReason(response.UnavailableReason),
            UnavailableDetail: response.HasUnavailableDetail ? response.UnavailableDetail : null,
            AssetDownloadUrl: response.HasAssetDownloadUrl ? response.AssetDownloadUrl : null,
            AssetSha256: response.HasAssetSha256 ? response.AssetSha256 : null,
            ApplyPending: response.ApplyPending);
    }

    /// <summary>Maps the wire reason enum onto the client's enum.</summary>
    private static UpdateUnavailableReasonInfo MapReason(Contract.UpdateUnavailableReason reason)
    {
        return reason switch
        {
            Contract.UpdateUnavailableReason.None => UpdateUnavailableReasonInfo.None,
            Contract.UpdateUnavailableReason.NoReleasesPublished => UpdateUnavailableReasonInfo.NoReleasesPublished,
            Contract.UpdateUnavailableReason.MalformedTag => UpdateUnavailableReasonInfo.MalformedTag,
            Contract.UpdateUnavailableReason.AssetOrChecksumMissing => UpdateUnavailableReasonInfo
                .AssetOrChecksumMissing,
            Contract.UpdateUnavailableReason.NetworkOrApiFailure => UpdateUnavailableReasonInfo.NetworkOrApiFailure,
            _ => UpdateUnavailableReasonInfo.None
        };
    }
}