namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// The last-known outcome of a release check, and when it was recorded. A read-model snapshot for
/// <c>UpdateAdminGrpcService.GetUpdateStatus</c> - ephemeral, in-memory, operational state rather than
/// data that needs to survive a restart (a restarted Router simply checks again).
/// </summary>
/// <param name="Result">
/// The most recent <see cref="ReleaseCheckResult"/>, or <see langword="null"/> before the first check
/// has completed.
/// </param>
/// <param name="CheckedAtUtc">
/// When <paramref name="Result"/> was recorded, or <see langword="null"/> before the first
/// check.
/// </param>
/// <param name="ApplyPending">
/// Whether a dashboard apply request is waiting for the tray to claim it. Cleared by the claim,
/// and also when a later check says no verified update is available.
/// </param>
public sealed record UpdateStateSnapshot(
    ReleaseCheckResult? Result,
    DateTimeOffset? CheckedAtUtc,
    bool ApplyPending = false);

/// <summary>
/// One installer the dashboard asked for and the tray has not claimed yet. Stored in memory with the
/// check snapshot; a router restart drops it, and the operator can click Update again after the next check.
/// </summary>
/// <param name="LatestVersion">The release version the request was made against.</param>
/// <param name="AssetDownloadUrl">The MSI download URL copied from that check.</param>
/// <param name="AssetSha256">The published SHA256 copied from that check.</param>
public sealed record PendingUpdateApply(string LatestVersion, string AssetDownloadUrl, string AssetSha256);

/// <summary>
/// Holds the Router's last-known update-check outcome, shared between <see cref="UpdateCheckHostedService"/>
/// (the writer) and <see cref="UpdateAdminGrpcService"/> (the reader the GUI polls). In-memory only - see
/// <see cref="UpdateStateSnapshot"/>'s remarks for why persistence is unnecessary here.
/// </summary>
public interface IUpdateStateStore
{
    /// <summary>
    /// Gets the current snapshot. Never <see langword="null"/>; before the first check, both members of the snapshot
    /// are <see langword="null"/>.
    /// </summary>
    UpdateStateSnapshot Current { get; }

    /// <summary>
    /// Records a fresh check outcome, timestamped now. A check that is not a verified update clears any
    /// pending install, so a withdrawn release is not installed later.
    /// </summary>
    void Record(ReleaseCheckResult result);

    /// <summary>
    /// Records one pending install from the current verified update. A call while one is already waiting
    /// leaves the first install in place.
    /// </summary>
    /// <returns>
    /// The pending install, and whether this call created it. <c>Created</c> is <see langword="false"/>
    /// when an install was already waiting.
    /// </returns>
    /// <exception cref="InvalidOperationException">The current check is not a verified, installable update.</exception>
    (bool Created, PendingUpdateApply Pending) TryRequestApply();

    /// <summary>
    /// Removes and returns the pending install, or <see langword="null"/> when none is waiting. A second
    /// call returns <see langword="null"/> so the tray launches the installer once.
    /// </summary>
    PendingUpdateApply? TryClaimApply();
}