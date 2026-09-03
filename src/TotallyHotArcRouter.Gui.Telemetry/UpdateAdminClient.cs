using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Why an <see cref="IUpdateAdminClient"/> call failed - mirrors <see cref="LlmRouterModelAdminException"/>'s
/// "flag Unavailable distinctly" shape: the Router being down is an ordinary state for a GUI that can
/// outlive it, not the same kind of failure as a rejected request. See
/// <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class UpdateAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="UpdateAdminException"/> class.</summary>
    /// <param name="message">A plain-language description of the failure.</param>
    /// <param name="innerException">The underlying <see cref="RpcException"/>, if any.</param>
    /// <param name="isUnavailable">Whether the failure was specifically the router being unreachable.</param>
    public UpdateAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message: message, innerException: innerException, isUnavailable: isUnavailable)
    {
    }
}

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
public sealed record UpdateStatusInfo(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    DateTimeOffset? CheckedAtUtc,
    UpdateUnavailableReasonInfo UnavailableReason,
    string? UnavailableDetail,
    string? AssetDownloadUrl = null,
    string? AssetSha256 = null);

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
    /// <exception cref="UpdateAdminException">The call failed or the router is unreachable.</exception>
    Task<UpdateStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Forces an immediate re-check and returns the fresh outcome.</summary>
    /// <exception cref="UpdateAdminException">The call failed or the router is unreachable.</exception>
    Task<UpdateStatusInfo> CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the Router it is about to apply an update to <paramref name="version"/>, purely for the
    /// audit log - the Router does not download, verify, or gate anything here. The caller (
    /// <see cref="IMsiUpdateApplier"/>, driven from <c>UpdateStore</c>) already has everything it needs
    /// from a prior <see cref="GetStatusAsync"/>/<see cref="CheckNowAsync"/> result and proceeds with the
    /// apply even if this call fails to reach the Router.
    /// </summary>
    /// <exception cref="UpdateAdminException">The call failed or the router is unreachable.</exception>
    Task<NotifyApplyStartingInfo> NotifyApplyStartingAsync(string version,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for the proxy's <c>UpdateAdminService</c> - the Governance UI's System Settings window's
/// "Software Update" section (docs/router/auto-update-plan.md Phase 2, packaging superseded by
/// docs/router/packaging-and-distribution.md). Lives in this plain <c>net10.0</c> library rather than the
/// Windows-only MAUI project so CI can unit-test it, exactly like <see cref="LlmRouterModelAdminClient"/>.
/// </summary>
public sealed class UpdateAdminClient
    : GrpcAdminClientBase<Contract.UpdateAdminService.UpdateAdminServiceClient, UpdateAdminException>,
        IUpdateAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public UpdateAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress: serverAddress,
            createClient: callInvoker => new Contract.UpdateAdminService.UpdateAdminServiceClient(callInvoker))
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
            AssetSha256: response.HasAssetSha256 ? response.AssetSha256 : null);
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

    /// <inheritdoc/>
    protected override UpdateAdminException CreateException(string message, Exception? innerException,
        bool isUnavailable)
    {
        return new UpdateAdminException(message: message, innerException: innerException, isUnavailable: isUnavailable);
    }
}