namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Checks GitHub Releases for a Router version newer than the one currently running. The sole
/// implementation is <see cref="GitHubReleaseCheckClient"/>; the interface exists so
/// <see cref="UpdateCheckHostedService"/> and <see cref="UpdateAdminGrpcService"/> can be unit-tested
/// against a fake.
/// </summary>
public interface IReleaseCheckClient
{
    /// <summary>
    /// Queries the latest published GitHub Release and compares it against the running version. Never
    /// throws for an ordinary failure mode (no releases, malformed tag, missing asset/checksum, network
    /// failure) - every one of those is reported via <see cref="ReleaseCheckResult.UnavailableReason"/>
    /// instead, so a background poller never needs to guard this call with a try/catch of its own.
    /// </summary>
    Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}