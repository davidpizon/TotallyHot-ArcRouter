namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Downloads and checksum-verifies a Router release asset, then hands off to <c>Updater.exe</c> to stop
/// the Windows Service, swap the install directory, and restart it. The seam
/// <see cref="UpdateAdminGrpcService.ApplyUpdate"/> is tested against, so a unit test never needs to
/// spawn a real process or touch a real Windows service. The production implementation,
/// <see cref="UpdateApplier"/>, wraps <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
/// </summary>
public interface IUpdateApplier
{
    /// <summary>
    /// Downloads <paramref name="update"/>'s asset, verifies it against <paramref name="update"/>'s
    /// published SHA256, and - only if that succeeds - launches the detached updater process. Never
    /// throws; every failure (download, checksum mismatch, launch failure) is reported via
    /// <see cref="ApplyUpdateResult.Succeeded"/> being <see langword="false"/>, and in that case nothing
    /// on disk has been touched and no updater process was started.
    /// </summary>
    /// <param name="update">A resolved, update-available <see cref="ReleaseCheckResult"/> (i.e. <see cref="ReleaseCheckResult.IsUpdateAvailable"/> is <see langword="true"/>).</param>
    /// <param name="cancellationToken">Cancels the download only; once the updater process is launched, applying is out of this process's hands.</param>
    Task<ApplyUpdateResult> ApplyAsync(ReleaseCheckResult update, CancellationToken cancellationToken = default);
}
